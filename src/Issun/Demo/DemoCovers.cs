using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Issun.Demo;

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

        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
