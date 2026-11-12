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
    /// Black on white with the standard four-module quiet zone, whatever the
    /// theme: phone cameras read dark-on-light codes most reliably. Error
    /// correction M, the common default.
    /// </summary>
    public static BitmapSource Render(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, drawQuietZones: true);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
