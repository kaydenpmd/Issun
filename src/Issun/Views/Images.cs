using System.Runtime.InteropServices.WindowsRuntime;
using Issun.Core;
using Issun.Presentation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Issun.Views;

/// <summary>Turning bytes and pixels into images WinUI can draw. UI thread: a WinUI image belongs to the thread that made it.</summary>
public static class Images
{
    /// <summary>Premultiplied BGRA pixels as a bitmap, at one image pixel per pixel.</summary>
    public static ImageSource FromPixels(Pixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// A JPEG, PNG or BMP, shrunk to fit a <paramref name="box"/> pixels
    /// square, its longer side the box's, the other in proportion (a cover is
    /// fitted, never cropped: the owner, 14 Nov 2026), with WIC's Fant
    /// interpolation, an area average:
    /// what Quick Settings' media card does with a cover (measured 14 Nov
    /// 2026 against the exact thumbnail Spotify gave Windows; the box filter
    /// fit at 1.4 of 255, GPU bilinear at 8.6, design library
    /// microsoft\windows11-media-controls). BitmapImage's DecodePixelWidth,
    /// used before, fit a softer triangle filter. Null, logged, when it isn't
    /// an image.
    /// </summary>
    public static async Task<WriteableBitmap?> DecodeAsync(byte[] bytes, int box, string what)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double w = Math.Max(1, decoder.OrientedPixelWidth), h = Math.Max(1, decoder.OrientedPixelHeight);
            var k = box / Math.Max(w, h);
            int width = Math.Max(1, (int)Math.Round(w * k)), height = Math.Max(1, (int)Math.Round(h * k));
            // ScaledWidth/Height apply before orientation, so swap them for a
            // picture its EXIF turns sideways.
            var turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)(turned ? height : width),
                ScaledHeight = (uint)(turned ? width : height),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var image = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap.CopyToBuffer(image.PixelBuffer);
            image.Invalidate();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] {what} didn't decode ({bytes.Length} bytes): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
