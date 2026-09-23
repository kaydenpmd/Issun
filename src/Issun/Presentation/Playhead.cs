using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// Where the song is now, in exactly the numbers Discord shows.
///
/// Discord draws its bar from two whole-second timestamps, a start and an end,
/// each cut to a whole second on its own, so its length is End − Start. That
/// can be a second more than the track's own length rounded down: 180.9 s
/// read 3:01 on Discord and 3:00 here until 23 Sept 2026.
///
/// So the window has no arithmetic of its own. It reads the start and end
/// Issun built for Discord (HostSnapshot.Intended), which the presence worker
/// builds every second whether or not Discord is open. The owner asked for
/// exactly that: the window must not show one thing with Discord open and
/// another with it closed. The anchor inside those timestamps is the
/// builder's, paired with the push's arrival time and never with "now" — see
/// ActivityBuilder for why that matters.
/// </summary>
public static class Playhead
{
    /// <summary>
    /// Discord's start and end for the current track, or null when there is no
    /// bar: nothing playing, no length (a live station), or the moment after a
    /// skip before the worker has built the new track, when showing the
    /// previous track's numbers against the new title would be wrong.
    /// </summary>
    public static (long Start, long End)? Span(HostSnapshot s) =>
        s.Track is { } track
        && s.Intended is { Start: long start, End: long end } intended
        && end > start
        && SameTrack(intended, track)
            ? (start, end)
            : null;

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
    /// Whether the built activity is for this track. Details and State carry
    /// invisible U+2060 padding for one-character names, stripped before comparing.
    /// </summary>
    private static bool SameTrack(DiscordActivity activity, TrackInfo track) =>
        activity.Details.TrimEnd('⁠') == TrackText.Title(track)
        && activity.State.TrimEnd('⁠') == TrackText.Artist(track);
}
