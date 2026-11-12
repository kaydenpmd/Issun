using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Issun.Presentation;

/// <summary>
/// The QR code that hands a source its endpoint and key in one scan.
///
/// The payload is plain JSON, <c>{"endpoint": "...", "key": "..."}</c>, the
/// two values the window shows under the same names, so any source can read
/// it: Issun is a receiver for any source, and the code mustn't name one. It
/// is not a link. A link would have to name an app to open, and the phone's
/// camera would offer to open a URL with the key in it.
/// </summary>
public static class PairingCode
{
    /// <summary>
    /// Either value may be left out (the owner, 12 Nov 2026): a code with only
    /// the key, or only the endpoint, fills in just that field and leaves the
    /// source's other one as it was.
    /// </summary>
    public static string Payload(string? endpoint, string? key) =>
        JsonSerializer.Serialize(new PairingPayload(endpoint, key));

    private sealed record PairingPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("endpoint"),
                   System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Endpoint,
        [property: System.Text.Json.Serialization.JsonPropertyName("key"),
                   System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Key);

    /// <summary>
    /// Black on white in the light theme, and white on black in the dark one
    /// (the owner, 12 Nov 2026), with the standard four-module quiet zone.
    /// Error correction M, the common default. Light-on-dark codes are the
    /// "inverted" kind the QR standard allows; whether Ammy's scanner reads
    /// them is for the phone to show.
    /// </summary>
    public static BitmapSource Render(string payload, bool dark)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        byte[] black = [0, 0, 0, 255], white = [255, 255, 255, 255];
        var png = dark
            ? new PngByteQRCode(data).GetGraphic(8, white, black, drawQuietZones: true)
            : new PngByteQRCode(data).GetGraphic(8, black, white, drawQuietZones: true);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Whether the window is drawn dark: the Fluent theme's primary text
    /// colour is light then. Follows --theme as well as Windows' setting.
    /// </summary>
    public static bool ThemeIsDark() =>
        System.Windows.Application.Current?.TryFindResource("TextFillColorPrimaryBrush") is System.Windows.Media.SolidColorBrush { Color: var c }
        && 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 128;
}
