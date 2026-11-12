namespace Issun.Presentation;

/// <summary>Premultiplied BGRA pixels, top row first, as WinUI's bitmaps take them.</summary>
public sealed record Pixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// A Gaussian blur done on the CPU. WPF had BlurEffect, which Issun used to
/// blur the setup window's key and QR code until Show; WinUI 3 has no effect
/// like it (the winui3-spike's report, question 5), so the hidden key and
/// code are blurred here instead and drawn as images. The images are small
/// (a 200 px code, one line of text), so this costs a few milliseconds.
/// </summary>
public static class Blur
{
    /// <summary>
    /// <paramref name="radius"/> in pixels, as WPF's BlurEffect.Radius was in
    /// its own units: the kernel reaches that far, which is three standard
    /// deviations of the Gaussian.
    /// </summary>
    public static Pixels Gaussian(Pixels source, double radius)
    {
        if (radius < 0.5)
            return source;
        var sigma = radius / 3;
        var reach = (int)Math.Ceiling(radius);
        var kernel = new double[reach * 2 + 1];
        double sum = 0;
        for (var i = -reach; i <= reach; i++)
            sum += kernel[i + reach] = Math.Exp(-(i * i) / (2 * sigma * sigma));
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;

        int w = source.Width, h = source.Height;
        var temp = new double[w * h * 4];
        var src = source.Bgra;
        // Horizontal pass, edges clamped.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    var sx = Math.Clamp(x + k, 0, w - 1);
                    var i = (y * w + sx) * 4;
                    var f = kernel[k + reach];
                    b += src[i] * f; g += src[i + 1] * f; r += src[i + 2] * f; a += src[i + 3] * f;
                }
                var o = (y * w + x) * 4;
                temp[o] = b; temp[o + 1] = g; temp[o + 2] = r; temp[o + 3] = a;
            }
        }
        // Vertical pass.
        var output = new byte[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    var sy = Math.Clamp(y + k, 0, h - 1);
                    var i = (sy * w + x) * 4;
                    var f = kernel[k + reach];
                    b += temp[i] * f; g += temp[i + 1] * f; r += temp[i + 2] * f; a += temp[i + 3] * f;
                }
                var o = (y * w + x) * 4;
                output[o] = (byte)Math.Clamp(Math.Round(b), 0, 255);
                output[o + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
                output[o + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
                output[o + 3] = (byte)Math.Clamp(Math.Round(a), 0, 255);
            }
        }
        return new Pixels(output, w, h);
    }
}
