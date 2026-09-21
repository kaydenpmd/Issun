using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Issun.Core.Tests.Artwork;

/// <summary>
/// Stands in for itunes.apple.com. Every request is recorded; the response
/// comes from <see cref="Respond"/>, which tests replace as they need.
/// </summary>
internal sealed class FakeItunes : HttpMessageHandler
{
    private readonly ConcurrentQueue<HttpRequestMessage> _requests = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Json("""{"resultCount":0,"results":[]}"""));

    public IReadOnlyList<HttpRequestMessage> Requests => _requests.ToArray();

    public IReadOnlyList<string> Urls => _requests.Select(r => r.RequestUri!.OriginalString).ToArray();

    public int Count => _requests.Count;

    /// <summary>Serves the same body for every request.</summary>
    public static FakeItunes Serving(string body, bool bom = false) =>
        new() { Respond = (_, _) => Task.FromResult(Json(body, bom)) };

    /// <summary>Serves one body for /lookup and another for /search.</summary>
    public static FakeItunes Serving(string lookup, string search) => new()
    {
        Respond = (request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.StartsWith("/lookup", StringComparison.Ordinal) ? lookup : search)),
    };

    public static HttpResponseMessage Json(string body, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (bom)
            bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            // iTunes answers text/javascript, which is what json.load() never cared about either.
            Content = new ByteArrayContent(bytes) { Headers = { { "Content-Type", "text/javascript; charset=utf-8" } } },
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _requests.Enqueue(request);
        return Respond(request, ct);
    }
}

/// <summary>A folder under %TEMP% that is deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => System.IO.Directory.CreateDirectory(Path);

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "issun-artwork-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class Py
{
    /// <summary>
    /// A string from the generated parity data: plain JSON, or {"cp": [...]}
    /// for one carrying a lone surrogate, which JSON cannot hold.
    /// </summary>
    public static string Str(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString()!;
        var sb = new StringBuilder();
        foreach (var cp in e.GetProperty("cp").EnumerateArray())
        {
            var value = cp.GetInt32();
            if (value <= 0xFFFF)
                sb.Append((char)value);
            else
                sb.Append(char.ConvertFromUtf32(value));
        }
        return sb.ToString();
    }

    public static string? OptStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? Str(v) : null;

    public static JsonElement.ArrayEnumerator Cases(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray();

    /// <summary>Printable form of a string for a failure message.</summary>
    public static string Show(string s) => JsonSerializer.Serialize(s);

    /// <summary>A small but genuine-looking JPEG: SOI, a JFIF APP0 header and filler.</summary>
    public static byte[] Jpeg(int filler = 64, byte seed = 1)
    {
        byte[] header = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00];
        var body = Enumerable.Range(0, filler).Select(i => (byte)(i * 7 + seed)).ToArray();
        return [.. header, .. body, 0xFF, 0xD9];
    }
}
