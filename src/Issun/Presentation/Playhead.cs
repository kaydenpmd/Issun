using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// Where the song is now, in the same numbers Discord shows.
///
/// Discord draws its bar from two whole-second timestamps, a start and an end,
/// each cut to a whole second on its own. That makes Discord's length End −
/// Start, which can be a second longer than the track's own length rounded
/// down: 180.9 s showed as 3:01 on Discord and 3:00 here until 23 Sept 2026. So
/// the window works in that same pair. When Discord is showing this track it
/// uses exactly what was sent, and otherwise it does the same arithmetic
/// ActivityBuilder would.
///
/// The fallback anchors the reading to the moment it *arrived*
/// (HostSnapshot.TrackObservedAt), never to the moment the window redraws.
/// Anchoring a frozen reading to "now" is the bug that made Discord's bar
/// rubberband in relay.py (see "The playhead anchor is paired with the push's
/// arrival time" in Ammy's CLAUDE.md).
/// </summary>
public static class Playhead
{
    /// <summary>The whole-second start and end Discord was sent, or would be; null when there is no bar.</summary>
    public static (long Start, long End)? Span(HostSnapshot s)
    {
        if (s.Track is not { } track)
            return null;

        if (s.Activity is { Start: long start, End: long end } activity && end > start && SameTrack(activity, track))
            return (start, end);

        if (track.Duration is not double duration || !double.IsFinite(duration) || duration <= 0)
            return null;
        if (track.Elapsed is not double elapsed || !double.IsFinite(elapsed))
            return null;

        // ActivityBuilder: start = observed_at − elapsed, then int() on start
        // and on start + duration separately, as relay.py did.
        var anchor = s.TrackObservedAt - elapsed;
        if (!double.IsFinite(anchor + duration))
            return null;
        return ((long)Math.Truncate(anchor), (long)Math.Truncate(anchor + duration));
    }

    /// <summary>Seconds into the track, capped at its length; null when there is no bar.</summary>
    public static double? Elapsed(HostSnapshot s, double now) =>
        Span(s) is { } span ? Math.Clamp(now - span.Start, 0, span.End - span.Start) : null;

    /// <summary>The track's length as Discord shows it; null when there is no bar.</summary>
    public static double? Duration(HostSnapshot s) =>
        Span(s) is { } span ? span.End - span.Start : null;

    /// <summary>0..1 for a progress bar; null when there is no bar.</summary>
    public static double? Fraction(HostSnapshot s, double now) =>
        Duration(s) is double d && Elapsed(s, now) is double e ? Math.Clamp(e / d, 0, 1) : null;

    /// <summary>
    /// Whether Discord's activity is for this track and not the last one: a
    /// skip reaches the window a moment before the new activity reaches
    /// Discord. Details and State carry invisible U+2060 padding for
    /// one-character names, which is stripped before comparing.
    /// </summary>
    private static bool SameTrack(DiscordActivity activity, TrackInfo track) =>
        activity.Details.TrimEnd('⁠') == TrackText.Title(track)
        && activity.State.TrimEnd('⁠') == TrackText.Artist(track);
}
