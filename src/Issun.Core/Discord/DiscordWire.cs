using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Issun.Core.Discord;

/// <summary>
/// Discord's local RPC framing and the bodies Issun sends over it.
///
/// The wire format was checked against pypresence 4.6.2, the library relay.py
/// used. First by reading its payloads.py, presence.py and baseclient.py, then
/// by driving its real Presence.update(), clear() and close() into a capturing
/// writer and comparing what came out with what this class builds, key by key
/// and in the same order. tests/Issun.Core.Tests/Discord/DiscordWireTests.cs
/// holds those captures. Anything Discord accepted from relay.py it should
/// accept from Issun: same fields, same nesting, same omissions.
///
/// What differs is lexical, and invisible to any JSON parser: json.dumps puts a
/// space after ',' and ':' and this doesn't, and the nonce is a GUID rather
/// than pypresence's "{:.20f}" of the clock (see <see cref="SetActivity"/>).
/// </summary>
internal static class DiscordWire
{
    public const int OpHandshake = 0;
    public const int OpFrame = 1;
    public const int OpClose = 2;
    public const int OpPing = 3;
    public const int OpPong = 4;

    public const int HeaderBytes = 8;

    // json.dumps defaults to ensure_ascii=True: everything outside ASCII goes
    // out escaped, and pypresence framed with len() of that string, which is a
    // byte count only because of it. System.Text.Json's default encoder also
    // escapes everything outside ASCII (and a few HTML-sensitive characters
    // such as < and & that json.dumps leaves alone, which parses the same), so
    // U+2060, the invisible padding that keeps a one-character title like "i"
    // legal, travels escaped either way. It never throws on text, either: a
    // lone surrogate is written as U+FFFD instead of failing the push.
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>What pypresence's handshake() sends: <c>{"v": 1, "client_id": ...}</c>. close() sends the same body with opcode 2.</summary>
    public static JsonObject Hello(string clientId) => new() { ["v"] = 1, ["client_id"] = clientId };

    /// <summary>
    /// pypresence's Payload.set_activity(). A null activity is clear(), which
    /// builds <c>"activity": None</c> and then remove_none() deletes the key,
    /// so a clear is <c>{"pid": ...}</c> with no activity at all — not
    /// <c>"activity": null</c>. Discord treats the missing key as "clear".
    /// </summary>
    public static JsonObject SetActivity(DiscordActivity? activity, int pid, string nonce)
    {
        var args = new JsonObject { ["pid"] = pid };
        if (activity is not null)
            args["activity"] = Activity(activity);

        return new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            // pypresence used "{:.20f}".format(time.time()). Discord only echoes
            // the nonce back; uniqueness is all that matters, and a timestamp is
            // not unique when two requests land in the same clock tick.
            ["nonce"] = nonce,
        };
    }

    /// <summary>
    /// The activity object in pypresence's key order. remove_none() drops null
    /// fields and then any nested object left empty, so an activity without
    /// timestamps has no "timestamps" key rather than an empty one.
    /// </summary>
    public static JsonObject Activity(DiscordActivity a)
    {
        var activity = new JsonObject
        {
            // pypresence always sends both, defaulting to PLAYING (0) and NAME (0)
            // when the caller passed nothing. A null here therefore means "0 on
            // the wire", exactly as an omitted keyword did in relay.py — which is
            // what makes stripping StatusDisplayType in the fallback the same
            // thing push_with_fallback shedding status_display_type was.
            ["type"] = a.Type ?? 0,
            ["status_display_type"] = a.StatusDisplayType ?? 0,
        };
        AddIfPresent(activity, "state", a.State);
        AddIfPresent(activity, "state_url", a.StateUrl);
        AddIfPresent(activity, "details", a.Details);
        AddIfPresent(activity, "details_url", a.DetailsUrl);

        var timestamps = new JsonObject();
        AddIfPresent(timestamps, "start", a.Start);
        AddIfPresent(timestamps, "end", a.End);
        if (timestamps.Count > 0)
            activity["timestamps"] = timestamps;

        var assets = new JsonObject();
        AddIfPresent(assets, "large_image", a.LargeImage);
        AddIfPresent(assets, "large_text", a.LargeText);
        AddIfPresent(assets, "large_url", a.LargeUrl);
        if (assets.Count > 0)
            activity["assets"] = assets;

        // set_activity's default, never overridden by relay.py.
        activity["instance"] = true;
        return activity;
    }

    public static byte[] Encode(int opcode, JsonNode body)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString(Json));
        var frame = new byte[HeaderBytes + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, HeaderBytes);
        return frame;
    }

    public static JsonNode? Get(JsonNode? node, string key) => (node as JsonObject)?[key];

    public static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int Int(JsonNode? node)
    {
        if (node is not JsonValue v)
            return 0;
        if (v.TryGetValue<int>(out var i))
            return i;
        return v.TryGetValue<double>(out var d) ? (int)d : 0;
    }

    private static void AddIfPresent(JsonObject target, string key, string? value)
    {
        if (value is not null)
            target[key] = value;
    }

    private static void AddIfPresent(JsonObject target, string key, long? value)
    {
        if (value is not null)
            target[key] = value.Value;
    }
}
