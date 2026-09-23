using System.Globalization;
using System.Text;

namespace Issun.Core.Platform;

/// <summary>
/// The handful of Python string and number semantics relay.py's .env loader
/// leaned on, reproduced exactly. C#'s nearest equivalents differ at the
/// edges — <c>string.Trim()</c> leaves U+001C–U+001F alone where Python strips
/// them, <c>File.ReadAllLines</c> doesn't break on U+2028 or form feed, and
/// <c>int.Parse</c> rejects "8_787" — and an importer that disagrees with the
/// program it replaces at the edges imports a different key than the one Ammy
/// has, which reads as "Key Rejected" with nothing in the file looking wrong.
/// </summary>
internal static class PyText
{
    /// <summary>Python's <c>str.isspace()</c>, enumerated from CPython 3.14.</summary>
    public static bool IsSpace(char c) => (int)c switch
    {
        >= 0x09 and <= 0x0D => true,
        >= 0x1C and <= 0x20 => true,
        0x85 or 0xA0 or 0x1680 => true,
        >= 0x2000 and <= 0x200A => true,
        0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 => true,
        _ => false,
    };

    /// <summary><c>s.strip()</c>.</summary>
    public static string Strip(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary><c>s.strip(c)</c>: every leading and trailing <paramref name="c"/>, not just one.</summary>
    public static string Strip(string s, char c) => s.Trim(c);

    /// <summary><c>s.rstrip(c)</c>.</summary>
    public static string RStrip(string s, char c) => s.TrimEnd(c);

    /// <summary>
    /// <c>s.lower()</c>. Python lowercases by Unicode rules with no locale, which
    /// is what the invariant culture does — never the machine's culture, where a
    /// Turkish "I" would stop "ON" from matching "on". The one character the two
    /// disagree on is "İ" (U+0130): Python gives "i" plus a combining dot, the
    /// invariant culture a bare "i" — which would let "DETAİLS" match "details"
    /// here when relay.py never matched it.
    /// </summary>
    public static string Lower(string s) => s.Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant();

    private static bool IsLineBreak(char c) => (int)c switch
    {
        0x0A or 0x0B or 0x0C or 0x0D or 0x1C or 0x1D or 0x1E or 0x85 or 0x2028 or 0x2029 => true,
        _ => false,
    };

    /// <summary>
    /// <c>s.splitlines()</c>: breaks on every separator Python recognises, "\r\n"
    /// counts once, and a trailing separator does not produce an empty last line.
    /// </summary>
    public static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (!IsLineBreak(c))
            {
                i++;
                continue;
            }
            lines.Add(s[start..i]);
            i += c == '\r' && i + 1 < s.Length && s[i + 1] == '\n' ? 2 : 1;
            start = i;
        }
        if (start < s.Length)
            lines.Add(s[start..]);
        return lines;
    }

    /// <summary>
    /// <c>int(s)</c> in base 10: surrounding whitespace, one sign, any Unicode
    /// decimal digits, and single underscores between digits ("8_787").
    /// </summary>
    public static bool TryParseInt(string s, out long value)
    {
        value = 0;
        var digits = NormaliseDigits(Strip(s), allowFloatSyntax: false);
        return digits is not null
            && long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// <c>float(s)</c>, restricted to finite results. Python also accepts
    /// "inf", "nan" and overflowing exponents; those come back false with
    /// <paramref name="nonFinite"/> set, because no setting here means anything
    /// at infinity.
    /// </summary>
    public static bool TryParseFloat(string s, out double value, out bool nonFinite)
    {
        value = 0;
        nonFinite = false;
        var text = Strip(s);
        var unsigned = text.TrimStart('+', '-');
        if (text.Length - unsigned.Length <= 1 && Lower(unsigned) is "inf" or "infinity" or "nan")
        {
            nonFinite = true;
            return false;
        }

        var normalised = NormaliseDigits(text, allowFloatSyntax: true);
        if (normalised is null
            || !double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (double.IsFinite(value))
            return true;
        nonFinite = true;
        return false;
    }

    /// <summary>
    /// ASCII digits in place of any Unicode decimal digit, and the underscores
    /// Python allows (one, between two digits) removed. Null when an underscore
    /// is misplaced or a character is outside what the number syntax permits.
    /// </summary>
    private static string? NormaliseDigits(string text, bool allowFloatSyntax)
    {
        // Runes rather than chars: some decimal digits (mathematical bold, for
        // one) sit outside the Basic Multilingual Plane, arrive as surrogate
        // pairs, and Python accepts them all the same.
        var runes = text.EnumerateRunes().ToArray();
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < runes.Length; i++)
        {
            var r = runes[i];
            if (IsDecimalDigit(r))
            {
                sb.Append((char)('0' + (int)Rune.GetNumericValue(r)));
                continue;
            }
            if (r.Value == '_')
            {
                var before = i > 0 && IsDecimalDigit(runes[i - 1]);
                var after = i + 1 < runes.Length && IsDecimalDigit(runes[i + 1]);
                if (!before || !after)
                    return null;
                continue;
            }
            if (r.Value is '+' or '-' || (allowFloatSyntax && r.Value is '.' or 'e' or 'E'))
            {
                sb.Append((char)r.Value);
                continue;
            }
            return null;
        }
        return sb.ToString();
    }

    /// <summary>Unicode category Nd — what Python's int() and float() accept as a digit. Not "²", which is No.</summary>
    private static bool IsDecimalDigit(Rune r) => Rune.GetUnicodeCategory(r) == UnicodeCategory.DecimalDigitNumber;
}
