using System.Globalization;
using System.Numerics;

namespace Issun.Core.Artwork;

internal static class PyFormat
{
    /// <summary>
    /// Python's <c>f"{value:.2f}"</c>: the exact binary value, rounded half to
    /// even. The confidence in "[art] weak match (0.48): …" is printed this way,
    /// and the log has to read the same whichever program wrote it.
    ///
    /// .NET's "F2" is not guaranteed to agree on exact ties — 0.125 is exactly
    /// representable, Python prints 0.12 — so the rounding is done here on the
    /// exact value rather than trusted to the formatter.
    /// </summary>
    public static string Fixed2(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";

        var negative = double.IsNegative(value);
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent = 1;
        else
            mantissa |= 1L << 52;
        var shift = exponent - 1075; // |value| = mantissa * 2^shift

        BigInteger hundredths;
        var scaled = new BigInteger(mantissa) * 100;
        if (shift >= 0)
        {
            hundredths = scaled << shift;
        }
        else
        {
            var denominator = BigInteger.One << -shift;
            hundredths = BigInteger.DivRem(scaled, denominator, out var remainder);
            var twice = remainder * 2;
            if (twice > denominator || (twice == denominator && !hundredths.IsEven))
                hundredths += 1;
        }

        var whole = BigInteger.DivRem(hundredths, 100, out var cents);
        return (negative ? "-" : "")
            + whole.ToString(CultureInfo.InvariantCulture) + "."
            + ((int)cents).ToString("00", CultureInfo.InvariantCulture);
    }
}
