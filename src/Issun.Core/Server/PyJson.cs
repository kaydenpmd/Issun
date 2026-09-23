using System.Globalization;
using System.Numerics;
using System.Text;

namespace Issun.Core.Server;

/// <summary>
/// One JSON object written the way Python's <c>json.dumps()</c> writes it with
/// default arguments: ", " and ": " separators, keys in insertion order,
/// non-ASCII escaped as \uXXXX (ensure_ascii), floats as <c>repr()</c>.
///
/// GET /now-playing is read by web pages and overlays that were written against
/// relay.py. Matching its output byte for byte means nothing downstream can
/// notice the switch, and it lets the tests compare against what relay.py
/// actually produced rather than against a description of it.
/// </summary>
internal sealed class PyJsonObject
{
    private readonly StringBuilder _text = new("{");
    private bool _empty = true;

    public bool IsEmpty => _empty;

    public PyJsonObject Add(string key, bool value) => Raw(key, value ? "true" : "false");

    public PyJsonObject Add(string key, string value)
    {
        Key(key);
        PyJson.AppendString(_text, value);
        return this;
    }

    /// <summary>A float, or null for JSON null.</summary>
    public PyJsonObject AddNumber(string key, double? value) =>
        Raw(key, value is { } d ? PyJson.FloatRepr(d) : "null");

    public PyJsonObject Add(string key, PyJsonObject value) => Raw(key, value.ToString());

    public override string ToString() => _text + "}";

    private PyJsonObject Raw(string key, string literal)
    {
        Key(key);
        _text.Append(literal);
        return this;
    }

    private void Key(string key)
    {
        if (!_empty)
            _text.Append(", ");
        _empty = false;
        PyJson.AppendString(_text, key);
        _text.Append(": ");
    }
}

internal static class PyJson
{
    /// <summary>
    /// A JSON string as json.dumps writes it with ensure_ascii=True: anything
    /// outside printable ASCII becomes \uXXXX, lowercase hex, with characters
    /// beyond the BMP written as their UTF-16 surrogate pair — which is exactly
    /// what a C# string already holds, so iterating chars gives the same output.
    /// Unlike System.Text.Json's default encoder, &lt; &gt; &amp; and ' are left alone.
    /// </summary>
    public static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ' || c > '~')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Python's <c>repr(float)</c>, which is what json.dumps writes: the shortest
    /// digits that round-trip, "231.0" rather than "231", and exponent notation
    /// only below 1e-4 or from 1e16 up ("1e+16", "1.5e-05").
    ///
    /// Non-finite values come out as Python's tokens (NaN, Infinity), which are
    /// not valid JSON; the projection never passes one in.
    /// </summary>
    public static string FloatRepr(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsPositiveInfinity(x)) return "Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        if (x == 0) return double.IsNegative(x) ? "-0.0" : "0.0";

        // .NET's "R" is the shortest round-trip form too, just laid out
        // differently ("1E+16", "231"). Take its digits and decimal exponent
        // and lay them out the way Python's format_float_short does.
        var text = Math.Abs(x).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.');
        var digits = point < 0 ? text : text.Remove(point, 1);
        // Position of the decimal point relative to the first digit: x = 0.DIGITS × 10^decpt.
        var decpt = (point < 0 ? text.Length : point) + exponent;

        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
            lead++;
        digits = digits[lead..].TrimEnd('0');
        decpt -= lead;

        string body;
        if (decpt <= -4 || decpt > 16)
        {
            var exp = decpt - 1;
            var mantissa = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
            body = mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (decpt <= 0)
            body = "0." + new string('0', -decpt) + digits;
        else if (decpt >= digits.Length)
            body = digits + new string('0', decpt - digits.Length) + ".0";
        else
            body = digits[..decpt] + "." + digits[decpt..];

        return x < 0 ? "-" + body : body;
    }

    /// <summary>
    /// Python's <c>round(x, ndigits)</c>. Not Math.Round: Python rounds the
    /// double's exact binary value, half to even, so round(0.35, 1) is 0.3
    /// (0.35 is really 0.34999…) and round(0.25, 1) is 0.2 (an exact tie).
    /// Math.Round scales by ten first, and 0.35 × 10 rounds up to exactly 3.5 in
    /// floating point before the rounding ever happens, giving 0.4.
    /// </summary>
    public static double Round(double x, int ndigits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ndigits);
        if (!double.IsFinite(x) || x == 0)
            return x;

        var bits = BitConverter.DoubleToInt64Bits(x);
        var negative = bits < 0;
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFFL;
        BigInteger mantissa = biased == 0 ? fraction : fraction | (1L << 52);
        var exponent = biased == 0 ? -1074 : biased - 1075;

        // x = mantissa × 2^exponent exactly. From 2^0 up it is an integer, and
        // rounding an integer to decimal places leaves it alone.
        if (exponent >= 0)
            return x;

        var numerator = mantissa * BigInteger.Pow(10, ndigits);
        var denominator = BigInteger.One << -exponent;
        var scaled = BigInteger.DivRem(numerator, denominator, out var remainder);
        var vsHalf = (remainder * 2).CompareTo(denominator);
        if (vsHalf > 0 || (vsHalf == 0 && !scaled.IsEven))
            scaled += 1;

        if (scaled.IsZero)
            return negative ? -0.0 : 0.0;

        // Back to a double the way Python does it: through the decimal string,
        // which double.Parse rounds correctly.
        var text = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(ndigits + 1, '0');
        if (ndigits > 0)
            text = text[..^ndigits] + "." + text[^ndigits..];
        var result = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return negative ? -result : result;
    }
}
