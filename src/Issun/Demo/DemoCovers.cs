using System.Buffers.Binary;

namespace Issun.Demo;

/// <summary>A colour for the demo's covers: what WPF's Color.FromRgb gave them.</summary>
internal readonly record struct Color(byte R, byte G, byte B)
{
    public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b);
}

/// <summary>
/// Made-up album covers for the demo, drawn in code and handed over as data
/// URLs, so --demo shows artwork without fetching anything from anywhere and
/// without shipping images that look like real releases.
/// </summary>
internal static class DemoCovers
{
    private const int Size = 240;

    /// <summary>An ink sun on tinted paper: two paper tones, a disc, and a few brush bars.</summary>
    public static string Create(Color top, Color bottom, Color disc, int seed)
    {
        var rng = new Random(seed);
        var pixels = new byte[Size * Size * 4];
        var cx = Size * (0.35 + rng.NextDouble() * 0.3);
        var cy = Size * (0.3 + rng.NextDouble() * 0.2);
        var radius = Size * (0.16 + rng.NextDouble() * 0.08);
        var bars = Enumerable.Range(0, 3).Select(_ => (Y: Size * (0.62 + rng.NextDouble() * 0.3), H: 3 + rng.NextDouble() * 6)).ToArray();

        for (var y = 0; y < Size; y++)
        {
            var t = y / (double)(Size - 1);
            for (var x = 0; x < Size; x++)
            {
                var r = top.R + (bottom.R - top.R) * t;
                var g = top.G + (bottom.G - top.G) * t;
                var b = top.B + (bottom.B - top.B) * t;

                // Soft-edged disc.
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                var cover = Math.Clamp(radius + 0.75 - d, 0, 1);
                r += (disc.R - r) * cover; g += (disc.G - g) * cover; b += (disc.B - b) * cover;

                // Horizontal brush bars that thin out towards the right.
                foreach (var (barY, barH) in bars)
                {
                    var h = barH * (1 - x / (double)Size * 0.8);
                    var ink = Math.Clamp(h / 2 + 0.5 - Math.Abs(y - barY), 0, 1) * 0.85;
                    r += (24 - r) * ink; g += (20 - g) * ink; b += (18 - b) * ink;
                }

                // Paper grain.
                var grain = (rng.NextDouble() - 0.5) * 10;
                var i = (y * Size + x) * 4;
                pixels[i] = (byte)Math.Clamp(b + grain, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(g + grain, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(r + grain, 0, 255);
                pixels[i + 3] = 255;
            }
        }

        return "data:image/bmp;base64," + Convert.ToBase64String(Bmp(pixels));
    }

    /// <summary>
    /// A 32-bit top-down BMP of BGRA pixels: no encoder needed, and every
    /// image decoder reads it.
    /// </summary>
    private static byte[] Bmp(byte[] bgra)
    {
        const int header = 14 + 40;
        var file = new byte[header + bgra.Length];
        var s = file.AsSpan();
        s[0] = (byte)'B';
        s[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], header);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], Size);
        BinaryPrimitives.WriteInt32LittleEndian(s[22..], -Size);    // negative: rows run top to bottom
        BinaryPrimitives.WriteInt16LittleEndian(s[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[28..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[34..], bgra.Length);
        bgra.CopyTo(s[header..]);
        return file;
    }
}
