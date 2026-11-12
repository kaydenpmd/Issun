using System.Text.Json;
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
    ///
    /// Drawn here as pixels rather than as a PNG for WPF to decode: WinUI has
    /// no blur effect, so the hidden code is blurred in these pixels
    /// (<see cref="Blur"/>) and the view shows whichever it needs.
    /// </summary>
    public static Pixels Render(string payload, bool dark, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;        // quiet zone included
        var n = modules.Count;
        var size = n * pixelsPerModule;
        var bgra = new byte[size * size * 4];
        byte ink = dark ? (byte)255 : (byte)0, paper = dark ? (byte)0 : (byte)255;
        for (var y = 0; y < size; y++)
        {
            var row = modules[y / pixelsPerModule];
            for (var x = 0; x < size; x++)
            {
                var v = row[x / pixelsPerModule] ? ink : paper;
                var i = (y * size + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        }
        return new Pixels(bgra, size, size);
    }
}
