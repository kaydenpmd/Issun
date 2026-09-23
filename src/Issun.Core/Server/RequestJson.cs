using System.Text.Json;
using System.Text.Json.Nodes;

namespace Issun.Core.Server;

/// <summary>
/// The POST /now-playing body, parsed the way relay.py's
/// <c>json.loads(self.rfile.read(length) or b"{}")</c> parsed it.
/// </summary>
internal static class RequestJson
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 128 };

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The body as an object, or null with <paramref name="problem"/> saying why.
    ///
    /// An empty body is <c>{}</c>, as it was in relay.py: a valid push that says
    /// nothing is playing. Anything that parses but is not an object is refused
    /// here — relay.py accepted it, then crashed on <c>body.get</c> and dropped
    /// the connection without a response.
    ///
    /// Duplicate keys keep the last value, as Python's dict does, rather than
    /// failing the way JsonNode.Parse would.
    ///
    /// Numbers too large for a double become infinity, as they did in Python
    /// (json.loads("1e400") is inf). Strings are decoded here, eagerly, so a
    /// lone escaped surrogate ("\ud800") is refused as bad JSON now, rather than
    /// surfacing as an exception in whichever module first reads that field —
    /// which would turn one malformed push into a 500 from somewhere unrelated.
    /// Python accepted lone surrogates; Swift strings cannot hold them, so Ammy
    /// never sends one.
    /// </summary>
    public static JsonObject? ParseObject(ReadOnlyMemory<byte> body, out string? problem)
    {
        problem = null;

        // json.loads on bytes detects and skips a UTF-8 BOM; System.Text.Json
        // doesn't. Spelled as bytes on purpose: a u8 string literal of the
        // three hex escapes is three Latin-1 characters, six bytes once encoded,
        // and never matches.
        if (body.Span.StartsWith(Utf8Bom))
            body = body[3..];

        if (body.IsEmpty)
            return new JsonObject();

        try
        {
            using var document = JsonDocument.Parse(body, Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = $"expected an object, got {document.RootElement.ValueKind.ToString().ToLowerInvariant()}";
                return null;
            }
            return ToObject(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException is System.Text.Json's word for a string
            // that parsed but cannot be decoded — the lone surrogate above.
            problem = ex.Message;
            return null;
        }
    }

    private static JsonObject ToObject(JsonElement element)
    {
        var result = new JsonObject();
        foreach (var property in element.EnumerateObject())
            result[property.Name] = ToNode(property.Value);
        return result;
    }

    private static JsonNode? ToNode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToObject(element),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(ToNode).ToArray()),
        JsonValueKind.Null => null,
        JsonValueKind.String => JsonValue.Create(element.GetString()),
        // Numbers and booleans stay backed by the element, cloned because the
        // document is disposed on the way out. That keeps a number's literal
        // text, which NowPlayingPush.Integer needs to tell 5 from 5.0 the way
        // Python's isinstance(seq, int) did.
        _ => JsonValue.Create(element.Clone()),
    };
}
