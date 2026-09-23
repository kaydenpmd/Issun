using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Issun.Core;

/// <summary>
/// The track fields of a POST /now-playing body, as Ammy's PresenceRelay.push()
/// sends them. Values are raw: null means the key was absent. Defaults and the
/// 128-character clip are applied by <see cref="TrackText"/>, the way
/// relay.py's build_payload applied them, so projections that want the raw
/// value (GET /now-playing) still have it.
/// </summary>
public sealed record TrackInfo
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public double? Duration { get; init; }
    public double? Elapsed { get; init; }

    /// <summary>playbackStoreID, trimmed. May be "0" or "-1", which mean none — see <see cref="TrackText.StoreId"/>.</summary>
    public string? StoreId { get; init; }

    /// <summary>~80 KB of JPEG, sent once per track rather than on every heartbeat.</summary>
    public string? ArtworkB64 { get; init; }

    /// <summary>
    /// The source's word on whether the track is explicit; null when it said
    /// nothing. Ammy sends <c>explicit: true</c> and never false: iOS answers
    /// with a plain yes or no, and its no covers an unrated track as well as a
    /// clean one. Read it through <see cref="TrackText.Explicit"/>.
    /// </summary>
    public bool? Explicit { get; init; }
}

/// <summary>One POST /now-playing body, parsed defensively.</summary>
public sealed class NowPlayingPush
{
    public required JsonObject Raw { get; init; }

    /// <summary>Python truthiness of body["playing"].</summary>
    public bool Playing { get; init; }

    public string? AppVersion { get; init; }

    /// <summary>
    /// Milliseconds since epoch on the phone. Only a JSON integer counts —
    /// relay.py required isinstance(seq, int) and not bool — anything else is
    /// null, and a null seq always applies.
    /// </summary>
    public long? Seq { get; init; }

    public JsonObject? Diag { get; init; }

    /// <summary>
    /// The track, only when <see cref="Playing"/> — relay.py stored the body
    /// only then (<c>state.set(body if body.get("playing") else None)</c>).
    /// </summary>
    public TrackInfo? Track { get; init; }

    public static NowPlayingPush Parse(JsonObject body)
    {
        var playing = Truthy(body["playing"]);
        return new NowPlayingPush
        {
            Raw = body,
            Playing = playing,
            AppVersion = Truthy(body["app_version"]) ? Str(body["app_version"]) : null,
            Seq = Integer(body["seq"]),
            Diag = body["diag"] as JsonObject,
            Track = playing ? new TrackInfo
            {
                Title = Str(body["title"]),
                Artist = Str(body["artist"]),
                Album = Str(body["album"]),
                Duration = Number(body["duration"]),
                Elapsed = Number(body["elapsed"]),
                StoreId = Str(body["store_id"])?.Trim(),
                ArtworkB64 = Str(body["artwork_b64"]),
                Explicit = Bool(body["explicit"]),
            } : null,
        };
    }

    /// <summary>
    /// A JSON true or false; null for anything else, absence included. Stricter
    /// than <see cref="Truthy"/> on purpose: relay.py never read this field, so
    /// there is no Python behaviour to match, and "false" is truthy.
    /// </summary>
    public static bool? Bool(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : null;

    /// <summary>Python's bool() over a JSON value.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length > 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>() != 0,
        _ => false,
    };

    /// <summary>str(value) for strings and numbers; null when absent or JSON null.</summary>
    public static string? Str(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => node.ToJsonString(),
    };

    /// <summary>float(value) for numbers and numeric strings; null otherwise.</summary>
    public static double? Number(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.String
            && double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>A JSON integer literal as long; null for bools, fractions, strings and absence.</summary>
    public static long? Integer(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
            return null;
        var text = v.ToJsonString();
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

/// <summary>
/// The defaults and clipping relay.py's build_payload applied, in one place.
/// <see cref="Key"/> is also the input to the uploaded-art filename hash, so it
/// must match relay.py byte for byte.
/// </summary>
public static class TrackText
{
    public const int MaxField = 128;

    public static string Title(TrackInfo t) => Clip(t.Title ?? "Unknown Track");
    public static string Artist(TrackInfo t) => Clip(t.Artist ?? "Unknown Artist");
    public static string Album(TrackInfo t) => Clip(t.Album ?? "");

    /// <summary>relay.py's track_key: <c>f"{artist}|{title}|{album}"</c>, after clipping.</summary>
    public static string Key(TrackInfo t) => $"{Artist(t)}|{Title(t)}|{Album(t)}";

    /// <summary>The store ID worth looking up, or null. "0" is what local files report; "-1" is never valid.</summary>
    public static string? StoreId(TrackInfo t) =>
        t.StoreId?.Trim() is { Length: > 0 } id && id != "0" && id != "-1" ? id : null;

    /// <summary>
    /// Whether to mark the track explicit, as Apple Music's "E" does. The
    /// source's own word first; when it said nothing, what the exact store-ID
    /// lookup found, once that has run. Never fuzzy search: the clean and
    /// explicit versions of a song are separate catalog entries with the same
    /// title, which is exactly the near miss a fuzzy match makes. Never
    /// performs a lookup, so GET /now-playing can use it.
    /// </summary>
    public static bool Explicit(TrackInfo t, IArtworkResolver artwork) =>
        t.Explicit ?? (StoreId(t) is { } id ? artwork.CachedExplicit(id) : null) ?? false;

    /// <summary>Python's <c>s[:max]</c>, which counts code points rather than UTF-16 units.</summary>
    public static string Clip(string s, int max = MaxField)
    {
        if (s.Length <= max)
            return s;
        var sb = new StringBuilder(max);
        var count = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            if (count++ == max)
                break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}
