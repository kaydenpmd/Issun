using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Issun.Core.Artwork;

/// <summary>
/// The corner of Python's urllib.parse that relay.py's artwork code leaned on,
/// reproduced exactly: urlencode (via quote_plus) for building iTunes request
/// URLs, and the urlsplit → parse_qsl → urlencode → urlunsplit round trip in
/// <c>_album_url</c>.
///
/// .NET's own helpers are close but not the same — Uri.EscapeDataString writes
/// a space as %20 where quote_plus writes "+", and System.Uri normalises paths
/// and hosts that urlsplit leaves alone — and "close" would make the album link
/// Discord shows differ from the one relay.py sent, for no reason anyone could
/// see from the outside.
/// </summary>
internal static class PyUrl
{
    // RFC 3986 unreserved: urllib's _ALWAYS_SAFE.
    private static bool AlwaysSafe(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
            or (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~';

    /// <summary>
    /// urllib.parse.quote_plus(s, safe=""): UTF-8, unreserved characters kept,
    /// space as "+", everything else as uppercase %XX.
    ///
    /// Python encodes strictly and raises on a lone surrogate; relay.py built
    /// the search query outside its try block, so a title carrying one would
    /// have escaped build_payload entirely. Here it becomes U+FFFD instead.
    /// </summary>
    public static string QuotePlus(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (AlwaysSafe(b))
                sb.Append((char)b);
            else if (b == (byte)' ')
                sb.Append('+');
            else
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>urllib.parse.urlencode over (key, value) pairs, in order.</summary>
    public static string UrlEncode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => QuotePlus(p.Key) + "=" + QuotePlus(p.Value)));

    /// <summary>urllib.parse.parse_qsl(qs, keep_blank_values=True): split on "&amp;" only, "+" is a space.</summary>
    public static List<(string Key, string Value)> ParseQsl(string qs)
    {
        var result = new List<(string, string)>();
        if (qs.Length == 0)
            return result;
        foreach (var field in qs.Split('&'))
        {
            if (field.Length == 0)
                continue;
            var eq = field.IndexOf('=');
            var name = eq >= 0 ? field[..eq] : field;
            var value = eq >= 0 ? field[(eq + 1)..] : "";
            result.Add((UnquotePlus(name), UnquotePlus(value)));
        }
        return result;
    }

    public static string UnquotePlus(string s) => Unquote(s.Replace('+', ' '));

    /// <summary>
    /// urllib.parse.unquote(s, errors="replace"). Percent escapes are decoded
    /// per run of ASCII characters, and each run's bytes as UTF-8 with
    /// replacement — so a multi-byte sequence split by a raw non-ASCII
    /// character decodes as replacement characters, exactly as Python's does.
    /// Malformed escapes ("%zz", a trailing "%") are kept literally.
    /// </summary>
    public static string Unquote(string s)
    {
        if (!s.Contains('%'))
            return s;

        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            if (s[i] < 0x80)
            {
                while (i < s.Length && s[i] < 0x80)
                    i++;
                sb.Append(Encoding.UTF8.GetString(UnquoteAscii(s.AsSpan(start, i - start))));
            }
            else
            {
                while (i < s.Length && s[i] >= 0x80)
                    i++;
                sb.Append(s, start, i - start);
            }
        }
        return sb.ToString();
    }

    private static byte[] UnquoteAscii(ReadOnlySpan<char> run)
    {
        var bytes = new List<byte>(run.Length);
        for (var i = 0; i < run.Length; i++)
        {
            if (run[i] == '%' && i + 2 < run.Length
                && HexValue(run[i + 1]) is var hi and >= 0 && HexValue(run[i + 2]) is var lo and >= 0)
            {
                bytes.Add((byte)(hi * 16 + lo));
                i += 2;
            }
            else
            {
                bytes.Add((byte)run[i]);
            }
        }
        return bytes.ToArray();
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ── urlsplit / urlunsplit ───────────────────────────────────────────────

    public sealed record SplitResult(string Scheme, string Netloc, string Path, string Query, string Fragment);

    private static readonly HashSet<string> UsesNetloc =
    [
        "", "ftp", "http", "gopher", "nntp", "telnet", "imap", "wais", "file", "mms", "https", "shttp",
        "snews", "prospero", "rtsp", "rtsps", "rtspu", "rsync", "svn", "svn+ssh", "sftp", "nfs", "git", "git+ssh",
        "ws", "wss", "itms-services",
    ];

    private static bool SchemeChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '+' or '-' or '.';

    /// <summary>
    /// urllib.parse.urlsplit. Throws <see cref="FormatException"/> where Python
    /// raises ValueError: unbalanced or invalid IPv6 brackets, and a non-ASCII
    /// netloc that NFKC-normalises into URL delimiters.
    /// </summary>
    public static SplitResult UrlSplit(string url)
    {
        // WHATWG: strip leading C0 controls and space (not trailing), and drop
        // tab, CR and LF wherever they are.
        url = url.TrimStart(WhatwgC0OrSpace).Replace("\t", "").Replace("\r", "").Replace("\n", "");

        string scheme = "", netloc = "", query = "", fragment = "";
        var colon = url.IndexOf(':');
        if (colon > 0 && url[0] < 0x80 && char.IsAsciiLetter(url[0]) && url[..colon].All(SchemeChar))
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var delim = url.Length;
            foreach (var c in "/?#")
            {
                var w = url.IndexOf(c, 2);
                if (w >= 0)
                    delim = Math.Min(delim, w);
            }
            netloc = url[2..delim];
            url = url[delim..];
            var open = netloc.Contains('[');
            var shut = netloc.Contains(']');
            if (open != shut)
                throw new FormatException("Invalid IPv6 URL");
            if (open)
                CheckBracketedNetloc(netloc);
        }
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }
        var question = url.IndexOf('?');
        if (question >= 0)
        {
            query = url[(question + 1)..];
            url = url[..question];
        }
        CheckNetloc(netloc);
        return new SplitResult(scheme, netloc, url, query, fragment);
    }

    private static readonly char[] WhatwgC0OrSpace = Enumerable.Range(0, 0x21).Select(i => (char)i).ToArray();

    /// <summary>urllib.parse.urlunsplit. Empty query and fragment are dropped, as Python drops them.</summary>
    public static string UrlUnsplit(SplitResult parts)
    {
        var (scheme, netloc, url, query, fragment) = (parts.Scheme, parts.Netloc, parts.Path, parts.Query, parts.Fragment);
        string? net = netloc.Length > 0
            ? netloc
            : scheme.Length > 0 && UsesNetloc.Contains(scheme) && (url.Length == 0 || url[0] == '/') ? "" : null;

        if (net is not null)
        {
            if (url.Length > 0 && url[0] != '/')
                url = "/" + url;
            url = "//" + net + url;
        }
        else if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "//" + url;
        }
        if (scheme.Length > 0)
            url = scheme + ":" + url;
        if (query.Length > 0)
            url = url + "?" + query;
        if (fragment.Length > 0)
            url = url + "#" + fragment;
        return url;
    }

    private static void CheckNetloc(string netloc)
    {
        if (netloc.Length == 0 || netloc.All(c => c < 0x80))
            return;
        // Characters like U+2100 (℀) expand to "a/c" under NFKC, which IDNA
        // applies — so a netloc that looks harmless could smuggle a delimiter.
        // .NET refuses to normalise a lone surrogate, which Python passes
        // through unchanged; U+FFFD is equally inert under NFKC, so standing it
        // in keeps the check running on everything else in the netloc.
        var n = TitleMatch.ReplaceLoneSurrogates(
            netloc.Replace("@", "").Replace(":", "").Replace("#", "").Replace("?", ""));
        var normalized = n.Normalize(NormalizationForm.FormKC);
        if (n == normalized)
            return;
        if (normalized.IndexOfAny(['/', '?', '#', '@', ':']) >= 0)
            throw new FormatException($"netloc '{netloc}' contains invalid characters under NFKC normalization");
    }

    private static void CheckBracketedNetloc(string netloc)
    {
        var at = netloc.LastIndexOf('@');
        var hostAndPort = at >= 0 ? netloc[(at + 1)..] : netloc;
        string hostname;
        var open = hostAndPort.IndexOf('[');
        if (open >= 0)
        {
            if (open > 0)
                throw new FormatException("Invalid IPv6 URL");
            var bracketed = hostAndPort[(open + 1)..];
            var close = bracketed.IndexOf(']');
            hostname = close >= 0 ? bracketed[..close] : bracketed;
            var port = close >= 0 ? bracketed[(close + 1)..] : "";
            if (port.Length > 0 && !port.StartsWith(':'))
                throw new FormatException("Invalid IPv6 URL");
        }
        else
        {
            var colon = hostAndPort.IndexOf(':');
            hostname = colon >= 0 ? hostAndPort[..colon] : hostAndPort;
        }
        CheckBracketedHost(hostname);
    }

    private static readonly Regex IpvFuture = new(@"\A[vV][a-fA-F0-9]+\..+\z", RegexOptions.CultureInvariant);

    private static void CheckBracketedHost(string hostname)
    {
        if (hostname.StartsWith('v') || hostname.StartsWith('V'))
        {
            if (!IpvFuture.IsMatch(hostname))
                throw new FormatException("IPvFuture address is invalid");
            return;
        }
        // ipaddress.ip_address(): an IPv4 address is refused in brackets, and so
        // is anything that is neither. An optional %scope must be non-empty and
        // contain no second "%". IPAddress.TryParse alone is too lenient — it
        // reads "1" as 0.0.0.1 — so IPv4 is recognised by Python's own rules
        // and IPv6 must at least contain a colon.
        if (IsPythonIPv4(hostname))
            throw new FormatException("An IPv4 address cannot be in brackets");
        var pct = hostname.IndexOf('%');
        var address = pct >= 0 ? hostname[..pct] : hostname;
        var badScope = pct >= 0 && (pct == hostname.Length - 1 || hostname.IndexOf('%', pct + 1) >= 0);
        if (badScope || !address.Contains(':') || address.Contains('[') || address.Contains(']')
            || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            throw new FormatException($"'{hostname}' does not appear to be an IPv4 or IPv6 address");
    }

    /// <summary>ipaddress.IPv4Address's parser: four dot-separated decimal octets, 0–255, no leading zeros.</summary>
    private static bool IsPythonIPv4(string s)
    {
        var octets = s.Split('.');
        if (octets.Length != 4)
            return false;
        foreach (var octet in octets)
        {
            if (octet.Length is 0 or > 3 || !octet.All(char.IsAsciiDigit) || (octet.Length > 1 && octet[0] == '0'))
                return false;
            if (int.Parse(octet, System.Globalization.CultureInfo.InvariantCulture) > 255)
                return false;
        }
        return true;
    }

    /// <summary>
    /// relay.py's <c>_album_url</c>: the album's own URL, with Apple's
    /// ?i=&lt;trackId&gt; removed.
    ///
    /// collectionViewUrl comes back from the lookup carrying the track id, so
    /// opening it lands on the album with the current song selected. That is
    /// what the title line already does — details_url is trackViewUrl and
    /// points at the song. The cover art should point at the album itself, or
    /// the two links are the same link wearing different hats.
    ///
    /// Everything else in the query string is kept, though not byte for byte:
    /// Python's parse-then-re-encode round trip normalises it (%20 and "+"
    /// both come back as "+", reserved characters come back percent-encoded, a
    /// bare "x" comes back as "x="). That normalisation is reproduced too, so
    /// the link is the one relay.py sent. Throws <see cref="FormatException"/>
    /// where Python's urlsplit raised ValueError.
    /// </summary>
    public static string AlbumUrl(string url)
    {
        if (url.Length == 0)
            return "";
        var parts = UrlSplit(url);
        var kept = ParseQsl(parts.Query).Where(kv => kv.Key != "i");
        return UrlUnsplit(parts with { Query = UrlEncode(kept) });
    }
}
