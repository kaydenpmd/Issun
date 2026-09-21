using System.Globalization;
using System.Text;

namespace Issun.Core.Presence;

/// <summary>
/// The handful of Python string behaviours relay.py's activity code leaned on
/// without saying so. Each one decides either what Discord is sent or the
/// exact text of a log line, and relay.log history has to stay greppable
/// across the switch to Issun, so "close enough" .NET equivalents are not.
/// </summary>
internal static class PyText
{
    // str.isspace(): every code point Python 3.14 (Unicode 16.0) treats as
    // whitespace, all of them in the BMP. Not char.IsWhiteSpace — that omits
    // U+001C..U+001F, which Python's strip() removes, so a title of "\x1c"
    // would be padded here and replaced by "Unknown" in relay.py.
    private static readonly char[] Spaces =
    [
        '\t', '\n', '\u000B', '\u000C', '\r', '\u001C', '\u001D', '\u001E', '\u001F', ' ',
        '\u0085', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A',
        '\u2028', '\u2029', '\u202F', '\u205F', '\u3000',
    ];

    public static bool IsSpace(int codePoint) => codePoint <= 0xFFFF && Array.IndexOf(Spaces, (char)codePoint) >= 0;

    /// <summary><c>s.strip()</c> with no arguments.</summary>
    public static string Strip(string s) => s.Trim(Spaces);

    /// <summary><c>not s.strip()</c>.</summary>
    public static bool IsBlank(string s) => Strip(s).Length == 0;

    /// <summary>
    /// <c>len(s)</c>: code points, not UTF-16 units, so "🎵" is one character
    /// here exactly as it was to relay.py. A lone surrogate counts as one, as
    /// it does in a Python string.
    /// </summary>
    public static int CodePoints(string s)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            count++;
        }
        return count;
    }

    /// <summary>
    /// <c>repr(s)</c>, as <c>{text!r}</c> put it into relay.log: single quotes
    /// unless the text holds a ' and no ", backslash escapes for the quote and
    /// backslash, \t \n \r, \xhh below 0x20 and for DEL, and \x / \u / \U for any
    /// other non-printable code point.
    /// </summary>
    public static string Repr(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);

        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                cp = char.ConvertToUtf32(s[i], s[++i]);

            if (cp == quote || cp == '\\')
                sb.Append('\\').Append((char)cp);
            else if (cp == '\t')
                sb.Append("\\t");
            else if (cp == '\n')
                sb.Append("\\n");
            else if (cp == '\r')
                sb.Append("\\r");
            else if (cp < 0x20 || cp == 0x7F)
                sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            else if (cp < 0x7F)
                sb.Append((char)cp);
            else if (IsPrintable(cp))
                sb.Append(char.ConvertFromUtf32(cp));
            else if (cp <= 0xFF)
                sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            else if (cp <= 0xFFFF)
                sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
            else
                sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
        }

        return sb.Append(quote).ToString();
    }

    /// <summary>
    /// str.isprintable() for one code point: everything except the Other and
    /// Separator categories, with the ASCII space let back in.
    ///
    /// Categories come from .NET's own Unicode tables rather than Python's
    /// 16.0 ones, so a character first assigned in a Unicode version .NET does
    /// not know yet reads as unassigned and is escaped where Python would print
    /// it. That only ever changes how a one-character song title is quoted in a
    /// log line.
    /// </summary>
    public static bool IsPrintable(int codePoint)
    {
        if (codePoint == ' ')
            return true;
        if (codePoint is >= 0xD800 and <= 0xDFFF)
            return false;
        return CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }

    /// <summary>
    /// Python's <c>format(value, ".Nf")</c>, or <c>"+.Nf"</c> with
    /// <paramref name="plusSign"/>, for the drift figures in the [playhead] lines.
    ///
    /// Both runtimes round the double's exact binary value half to even, so
    /// .NET's standard "F" format already agrees with Python on the ties a drift
    /// between two timestamps near 1.75e9 can land on: a 12.25 s seek is "12.2"
    /// in both (the parity data checks the ties). The obvious way to get the plus
    /// sign, the custom format "+0.0;-0.0", does not agree — it rounds from 15
    /// significant digits, half away from zero, and prints that seek as "+12.3".
    /// So the sign is added by hand, including Python's "-0.0".
    /// </summary>
    public static string Fixed(double value, int decimals, bool plusSign = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);

        if (double.IsNaN(value))
            return (plusSign ? "+" : "") + "nan";

        var sign = double.IsNegative(value) ? "-" : plusSign ? "+" : "";
        if (double.IsInfinity(value))
            return sign + "inf";

        return sign + Math.Abs(value).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
