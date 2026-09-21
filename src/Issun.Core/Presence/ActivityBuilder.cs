using System.Globalization;
using System.Reflection;

namespace Issun.Core.Presence;

/// <summary>
/// relay.py's <c>build_payload()</c> minus the artwork lookup, which the caller
/// has already done (<see cref="IArtworkResolver"/>), plus <c>playhead.reset()</c>
/// and <c>_materially_different()</c>.
///
/// Thread-safe. The presence worker builds once a second; nothing here reads
/// the clock, so a build is a pure function of the reading, the settings, and
/// the playhead's memory of the current track.
/// </summary>
public sealed class ActivityBuilder : IActivityBuilder
{
    /// <summary>
    /// pypresence 4.6.2's <c>ActivityType.LISTENING</c>. Renders as
    /// "Listening to &lt;application name&gt;" instead of "Playing" — which is why
    /// the owner's Discord application is named "Apple Music": the name should
    /// describe the source, not the bridge.
    /// </summary>
    public const int Listening = 2;

    /// <summary>relay.py clipped every link to this many characters.</summary>
    public const int MaxUrl = 256;

    // Timestamps within this many seconds of each other are the same timestamp.
    private const long TimestampTolerance = 2;

    // Everything _materially_different compares for plain equality: every
    // property but the two timestamps, found by reflection so a field added to
    // DiscordActivity later is compared without anyone remembering to list it.
    // A field left out would never trigger a push on its own — a silent failure.
    private static readonly PropertyInfo[] ExactFields = typeof(DiscordActivity)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0
                    && p.Name is not (nameof(DiscordActivity.Start) or nameof(DiscordActivity.End)))
        .ToArray();

    private readonly IConfig _config;
    private readonly Playhead _playhead = new();
    private readonly DiscordText _text = new();
    private readonly FirstSeen _unresolvedSeen = new();
    private readonly FirstSeen _unusablePositionSeen = new();

    public ActivityBuilder(IConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// <paramref name="observedAt"/> is when this reading *arrived from the
    /// phone*, never now. That distinction is the whole ballgame for the
    /// progress bar: this runs once a second, but the phone pushes every thirty,
    /// so <paramref name="track"/> holds the same frozen <c>elapsed</c> for
    /// thirty consecutive calls. Pairing it with the current time slides
    /// <c>start</c> forward a second per second — the bar falls behind, then
    /// snaps back when the next push lands (relay 1.2.0's rubberbanding, fixed in
    /// 1.2.1). Pairing it with the arrival time keeps <c>start</c> genuinely
    /// constant between pushes, which is what Discord needs to tick smoothly.
    ///
    /// <paramref name="links"/> are applied as given; the caller passes them only
    /// for a track with a store ID, since they come from the exact store-ID
    /// lookup and never from fuzzy search — a near-miss cover is a cosmetic
    /// annoyance, but a link that opens the wrong song is a broken promise.
    /// </summary>
    public DiscordActivity Build(TrackInfo track, double observedAt, ArtworkResult art, CatalogLinks? links)
    {
        ArgumentNullException.ThrowIfNull(track);
        art ??= ArtworkResult.None;
        var settings = _config.Settings;

        var title = TrackText.Title(track);
        var artist = TrackText.Artist(track);
        var album = TrackText.Album(track);
        var key = TrackText.Key(track);

        // In relay.py's order, so the log lines come out in relay.py's order:
        // padding, then the playhead, then artwork.
        var details = _text.Pad(title, "title");
        var state = _text.Pad(artist, "artist");
        var (start, end) = Timestamps(track, key, title, observedAt);

        // Clickable text and artwork. Discord opens details_url from the title
        // line, state_url from the artist line and large_url from the cover.
        var detailsUrl = links?.Song is { Length: > 0 } song ? TrackText.Clip(song, MaxUrl) : null;
        var stateUrl = links?.Artist is { Length: > 0 } artistUrl ? TrackText.Clip(artistUrl, MaxUrl) : null;

        string? largeImage = null, largeUrl = null, largeText = null;
        if (art.Url is { Length: > 0 } cover)
        {
            largeImage = cover;

            // A link on the cover only makes sense when there's a cover to click.
            if (links?.Album is { Length: > 0 } albumUrl)
                largeUrl = TrackText.Clip(albumUrl, MaxUrl);

            // large_text is both the cover's tooltip and a visible third line on
            // the card — one field, two places, and Discord won't separate them.
            // Hence off by default. When on, prefer the album the artwork actually
            // came from: if the match was imperfect, hovering the cover reveals
            // what it thinks it found.
            if (settings.ShowAlbum)
            {
                var label = art.MatchedAlbum is { Length: > 0 } matched ? matched : album;
                if (label.Length > 0)
                    largeText = AlbumText(label);
            }
        }
        else if (_unresolvedSeen.Add(key))
        {
            // Record what the phone actually sent, once per track. Every artwork
            // path failing silently is what made this bug expensive to find.
            var storeId = PyText.Strip(track.StoreId ?? "");
            Log.Write($"[art] UNRESOLVED {title} — {artist} [{album}] "
                      + $"store_id={(storeId.Length > 0 ? storeId : "none")} "
                      + $"uploaded_jpeg={(string.IsNullOrEmpty(track.ArtworkB64) ? "no" : "yes")}");
        }

        return new DiscordActivity
        {
            Details = details,
            State = state,
            Type = Listening,
            StatusDisplayType = StatusDisplayFor(settings.StatusLine),
            Start = start,
            End = end,
            LargeImage = largeImage,
            LargeText = largeText,
            LargeUrl = largeUrl,
            DetailsUrl = detailsUrl,
            StateUrl = stateUrl,
        };
    }

    /// <summary>playhead.reset(): playback stopped, so the anchor is invalid.</summary>
    public void Reset() => _playhead.Reset();

    /// <inheritdoc cref="AreMateriallyDifferent"/>
    public bool MateriallyDifferent(DiscordActivity? next, DiscordActivity? previous) =>
        AreMateriallyDifferent(next, previous);

    /// <summary>
    /// relay.py's <c>_materially_different()</c>. Timestamps are recomputed on
    /// every build, so truncation makes them drift a second either way even when
    /// nothing changed; within <see cref="TimestampTolerance"/> seconds they count
    /// as identical — otherwise each heartbeat re-pushes and visibly nudges the
    /// bar. (With the arrival-time anchor they no longer move between pushes at
    /// all, so this tolerance is now belt-and-braces rather than load-bearing.)
    /// Everything else must match exactly, and null against non-null differs.
    /// </summary>
    public static bool AreMateriallyDifferent(DiscordActivity? next, DiscordActivity? previous)
    {
        if (next is null || previous is null)
            return !ReferenceEquals(next, previous);

        foreach (var field in ExactFields)
        {
            if (!Equals(field.GetValue(next), field.GetValue(previous)))
                return true;
        }

        return Apart(next.Start, previous.Start) || Apart(next.End, previous.End);
    }

    /// <summary>
    /// STATUS_LINE → <c>status_display_type</c>, pypresence 4.6.2's
    /// <c>StatusDisplayType</c>: which line Discord's compact member list shows.
    /// NAME (0) is the application name, "Apple Music"; STATE (1) the artist;
    /// DETAILS (2) the title. Anything else is omitted.
    ///
    /// Normalised the way relay.py normalised the environment variable,
    /// <c>.strip().lower()</c>, but read on every build rather than once at
    /// startup so a changed setting applies to the next build. Lower-casing is
    /// ASCII-only on purpose. In Python no non-ASCII character lower-cases to a
    /// letter of "name", "state" or "details" (the Kelvin sign becomes 'k', which
    /// none of them contains), but .NET's culture-sensitive ToLower maps 'İ' to
    /// 'i' under en-US and tr-TR alike, which would accept "DETAİLS" where
    /// relay.py did not — and the machine's culture is not ours to choose.
    /// </summary>
    public static int? StatusDisplayFor(string? statusLine) =>
        AsciiLower(PyText.Strip(statusLine ?? "state")) switch
        {
            "name" => 0,
            "state" => 1,
            "details" => 2,
            _ => null,
        };

    private (long? Start, long? End) Timestamps(TrackInfo track, string key, string title, double observedAt)
    {
        var duration = track.Duration ?? 0;
        var elapsed = track.Elapsed ?? 0;

        // NaN > 0 is false, as it was in Python: no duration, no progress bar.
        if (!(duration > 0))
            return (null, null);

        // relay.py computed int(start) and int(start + duration) with nothing in
        // the way, and that code sat outside the worker's try: an infinite
        // duration raised OverflowError, a NaN elapsed ValueError, and either one
        // killed the RPC thread for good. Presence froze until a restart, and the
        // only trace was a traceback on a console pythonw doesn't have. Here an
        // unusable position costs the progress bar for that track, once logged.
        if (double.IsFinite(duration) && double.IsFinite(elapsed) && double.IsFinite(observedAt - elapsed))
        {
            var start = _playhead.Anchor(key, elapsed, observedAt);
            // Python's int() truncates toward zero, and so does a C# cast; End is
            // the truncated sum, not the sum of truncations.
            if (TryTruncate(start, out var startSeconds) && TryTruncate(start + duration, out var endSeconds))
                return (startSeconds, endSeconds);
        }

        if (_unusablePositionSeen.Add(key))
        {
            Log.Write(string.Create(CultureInfo.InvariantCulture,
                $"[playhead] unusable position for {title} (duration={duration}, elapsed={elapsed}) — sending it without a progress bar"));
        }
        return (null, null);
    }

    /// <summary>
    /// The album as Discord will accept it. relay.py sent the label as-is; two
    /// hazards in that are closed here, both only reachable with SHOW_ALBUM on.
    /// A catalog collectionName is clipped nowhere upstream, and long classical
    /// album names run past the 128 characters Discord allows details and state.
    /// And a one-character album ("4") would be the "i" bug again in a different
    /// field. Both assume the image text is validated like details and state —
    /// by analogy, not seen in relay.log — and cost at most a clipped tooltip or
    /// one invisible character if that is wrong; being right saves the whole
    /// activity from being refused for the length of the track. Padded, not
    /// replaced: an album tooltip reading "Unknown" would be a false statement.
    /// </summary>
    private string AlbumText(string label) =>
        _text.PadToMinimum(TrackText.Clip(label), "album");

    private static bool TryTruncate(double seconds, out long truncated)
    {
        var whole = Math.Truncate(seconds);
        // Exactly the doubles that fit a long; 2^63 itself does not.
        if (whole >= -9_223_372_036_854_775_808.0 && whole < 9_223_372_036_854_775_808.0)
        {
            truncated = (long)whole;
            return true;
        }
        truncated = 0;
        return false;
    }

    private static bool Apart(long? a, long? b)
    {
        if (a.HasValue != b.HasValue)
            return true;
        // Int128 so the difference of two extreme longs cannot overflow.
        return a.HasValue && Int128.Abs((Int128)a.Value - b!.Value) > TimestampTolerance;
    }

    private static string AsciiLower(string s) =>
        string.Create(s.Length, s, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        });
}
