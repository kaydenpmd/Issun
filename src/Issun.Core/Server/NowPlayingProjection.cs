namespace Issun.Core.Server;

/// <summary>
/// relay.py's <c>public_state()</c>: the current track, shaped for GET /now-playing.
///
/// A projection, not the raw push. The push carries <c>artwork_b64</c> (~80 KB
/// of base64) and the whole <c>diag</c> block, and neither belongs in a
/// response that may be served publicly. Add fields here deliberately.
///
/// Nothing in here performs a lookup. A GET must never trigger an outbound
/// iTunes request, or a public endpoint becomes a way for a stranger to make
/// this machine issue traffic — so artwork and links come only from
/// <see cref="IArtworkResolver.CachedArtwork"/> and
/// <see cref="IArtworkResolver.CachedLinks"/>, or are omitted.
/// </summary>
internal static class NowPlayingProjection
{
    public static string Json(IPhoneState phone, IArtworkResolver artwork, double now)
    {
        var (track, updatedAt) = phone.Get();
        double? age = updatedAt != 0 ? now - updatedAt : null;
        var fresh = age < Timing.IdleTimeout;

        // Keys and their order are relay.py's; consumers written against it may
        // well index by position in a debugger or diff the text.
        var json = new PyJsonObject()
            .Add("playing", track is not null && fresh)
            .Add("stale", !fresh)
            .AddNumber("updated_ago", age is { } a ? PyJson.Round(a, 1) : null);

        if (track is null || !fresh)
            return json.ToString();

        // relay.py included a text field only when it was truthy, so an empty
        // album is left out rather than sent as "".
        if (track.Title is { Length: > 0 } title) json.Add("title", title);
        if (track.Artist is { Length: > 0 } artist) json.Add("artist", artist);
        if (track.Album is { Length: > 0 } album) json.Add("album", album);

        // round(x, 1), with Python's rounding — see PyJson.Round. A non-finite
        // value is left out: Python would have written a bare Infinity or NaN,
        // which JSON.parse in the browsers this route exists for rejects.
        if (track.Duration is { } duration && double.IsFinite(duration))
            json.AddNumber("duration", PyJson.Round(duration, 1));
        if (track.Elapsed is { } elapsed && double.IsFinite(elapsed))
            json.AddNumber("elapsed", PyJson.Round(elapsed, 1));

        // "0" (local files) and "-1" never resolve, so nothing can be cached
        // under them; TrackText.StoreId filters them the way build_payload did.
        if (TrackText.StoreId(track) is { } storeId)
        {
            if (artwork.CachedArtwork(storeId) is { Length: > 0 } cover)
                json.Add("artwork", cover);

            if (artwork.CachedLinks(storeId) is { } links)
            {
                // Only the links the lookup actually returned, in relay.py's
                // order, and the whole object omitted when none were.
                var linkJson = new PyJsonObject();
                if (links.Song is { Length: > 0 } song) linkJson.Add("song", song);
                if (links.Artist is { Length: > 0 } artistUrl) linkJson.Add("artist", artistUrl);
                if (links.Album is { Length: > 0 } albumUrl) linkJson.Add("album", albumUrl);
                if (!linkJson.IsEmpty)
                    json.Add("links", linkJson);
            }
        }

        return json.ToString();
    }
}
