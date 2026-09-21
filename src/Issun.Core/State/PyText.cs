using System.Globalization;
using System.Numerics;
using System.Text;

namespace Issun.Core.State;

/// <summary>
/// The handful of Python string and integer operations relay.py's uptime code
/// leaned on, with Python's exact meaning. Each one differs from its nearest
/// .NET equivalent in a way the uptime log can actually hit: splitlines()
/// breaks on more than CR and LF, split() and strip() treat U+001C..U+001F as
/// whitespace, int() takes signs, underscores and non-ASCII digits, sorted()
/// orders by code point rather than by UTF-16 unit, and // and % floor
/// towards negative infinity.
/// </summary>
internal static class PyText
{
    /// <summary>str.isspace() for one UTF-16 unit.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>
    /// str.splitlines(): breaks on \n, \r, \r\n, \v, \f, \x1c, \x1d, \x1e,
    /// \x85, U+2028 and U+2029, and drops the break itself. A trailing break
    /// does not produce an empty last line.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\x2028' or '\x2029')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>str.split() with no arguments: runs of whitespace separate, and empty strings are dropped.</summary>
    public static List<string> SplitWhitespace(string text)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSpace(text[i]))
                i++;
            var start = i;
            while (i < text.Length && !IsSpace(text[i]))
                i++;
            if (i > start)
                parts.Add(text[start..i]);
        }
        return parts;
    }

    public static string Strip(string s) => RStrip(LStrip(s));

    public static string LStrip(string s)
    {
        var i = 0;
        while (i < s.Length && IsSpace(s[i]))
            i++;
        return s[i..];
    }

    public static string RStrip(string s)
    {
        var end = s.Length;
        while (end > 0 && IsSpace(s[end - 1]))
            end--;
        return s[..end];
    }

    /// <summary>
    /// int(text) for a str: surrounding whitespace, one optional sign, then
    /// decimal digits — any script's, as Python allows — with single
    /// underscores permitted between them. False wherever Python raises ValueError.
    /// </summary>
    public static bool TryParseInt(string text, out BigInteger value)
    {
        value = BigInteger.Zero;
        var s = Strip(text);
        var i = 0;
        var negative = false;
        if (i < s.Length && s[i] is '+' or '-')
        {
            negative = s[i] == '-';
            i++;
        }

        var digits = new StringBuilder();
        var lastWasDigit = false;
        while (i < s.Length)
        {
            var cp = CodePointAt(s, i, out var width);
            if (cp == '_')
            {
                if (!lastWasDigit)
                    return false;
                lastWasDigit = false;
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.DecimalDigitNumber)
            {
                var d = (int)CharUnicodeInfo.GetDecimalDigitValue(s, i);
                if (d < 0)
                    return false;
                digits.Append((char)('0' + d));
                lastWasDigit = true;
            }
            else
            {
                return false;
            }
            i += width;
        }

        // Empty, sign-only, or ending on an underscore.
        if (digits.Length == 0 || !lastWasDigit)
            return false;

        value = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
            value = -value;
        return true;
    }

    private static int CodePointAt(string s, int index, out int width)
    {
        if (char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]))
        {
            width = 2;
            return char.ConvertToUtf32(s[index], s[index + 1]);
        }
        width = 1;
        return s[index];
    }

    /// <summary>
    /// Python's ordering of str: by code point. Ordinal comparison in .NET
    /// orders by UTF-16 unit instead, which puts everything above U+FFFF
    /// (surrogates, D800..DFFF) before U+E000..U+FFFF — so an emoji build
    /// name would sort differently from the way --summary printed it.
    /// </summary>
    public static readonly IComparer<string> CodePointOrder = Comparer<string>.Create(CompareCodePoints);

    private static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var x = CodePointAt(a, i, out var wa);
            var y = CodePointAt(b, j, out var wb);
            if (x != y)
                return x < y ? -1 : 1;
            i += wa;
            j += wb;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>Python's <c>a // b</c> for a positive divisor: rounds towards negative infinity.</summary>
    public static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        var q = BigInteger.DivRem(a, b, out var r);
        return r.Sign != 0 && (r.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
    }

    /// <summary>Python's <c>a % b</c>: takes the sign of the divisor.</summary>
    public static BigInteger FloorMod(BigInteger a, BigInteger b) => a - FloorDiv(a, b) * b;

    /// <summary>
    /// <c>f"{n:02d}"</c>: at least two characters, zero-padded — and a minus
    /// sign counts towards the two, so -5 is "-5" rather than "-05".
    /// </summary>
    public static string Pad2(BigInteger n) =>
        n.Sign < 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : n.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');

    /// <summary>
    /// Exact <c>a &lt; b</c> between a Python int and a float, the way Python
    /// compares them — no rounding of either side, so a very large uptime is
    /// never misjudged by a conversion to double.
    /// </summary>
    public static bool LessThan(BigInteger a, double b)
    {
        if (double.IsNaN(b))
            return false;
        if (double.IsPositiveInfinity(b))
            return true;
        if (double.IsNegativeInfinity(b))
            return false;
        var floor = Math.Floor(b);
        var whole = new BigInteger(floor);
        if (a != whole)
            return a < whole;
        return b > floor;
    }

    /// <summary>
    /// <c>s[:max]</c>: counts code points, not UTF-16 units. A lone surrogate
    /// is one code point here as it is in Python, rather than being replaced
    /// the way <see cref="string.EnumerateRunes"/> would.
    /// </summary>
    public static string Clip(string s, int max)
    {
        var i = 0;
        for (var count = 0; count < max && i < s.Length; count++)
        {
            CodePointAt(s, i, out var width);
            i += width;
        }
        return s[..i];
    }
}
