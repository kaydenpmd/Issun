using System.Runtime.InteropServices.WindowsRuntime;
using Issun.Core;
using Issun.Presentation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

    /// <summary>A JPEG, PNG or BMP, decoded to <paramref name="decodePixelWidth"/>. Null, logged, when it isn't an image.</summary>
    public static async Task<ImageSource?> DecodeAsync(byte[] bytes, int decodePixelWidth, string what)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = decodePixelWidth, DecodePixelType = DecodePixelType.Physical };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] {what} didn't decode ({bytes.Length} bytes): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
