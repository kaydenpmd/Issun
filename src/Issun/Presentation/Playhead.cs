using Issun.Core;

namespace Issun.Presentation;

/// <summary>
/// Where the song is now, extrapolated from the phone's last reading.
///
/// The reading is anchored to the moment it *arrived* (HostSnapshot.TrackObservedAt),
/// never to the moment the window happens to redraw. Anchoring a frozen reading
/// to "now" is exactly the bug that made Discord's bar rubberband for thirty
/// seconds at a time in relay.py (see "The playhead anchor is paired with the
/// push's arrival time" in Ammy's CLAUDE.md). The window ticks every second
/// between pushes, so it would make the same mistake just as visibly.
/// </summary>
public static class Playhead
{
    /// <summary>Seconds into the track, capped at its duration; null when the phone sent no position.</summary>
    public static double? Elapsed(TrackInfo track, double observedAt, double now)
    {
        if (track.Elapsed is not double reported || !double.IsFinite(reported))
            return null;

        // A reading can't have come from the future; a clock step backwards on
        // this PC must not rewind the bar either.
        var since = observedAt > 0 ? Math.Max(0, now - observedAt) : 0;
        var position = Math.Max(0, reported + since);

        if (Duration(track) is double duration)
            position = Math.Min(position, duration);
        return position;
    }

    /// <summary>The track's length when it is a usable number; null otherwise.</summary>
    public static double? Duration(TrackInfo track) =>
        track.Duration is double d && double.IsFinite(d) && d > 0 ? d : null;

    /// <summary>0..1 for a progress bar; null when there is no duration to measure against.</summary>
    public static double? Fraction(TrackInfo track, double observedAt, double now) =>
        Duration(track) is double d && Elapsed(track, observedAt, now) is double e ? Math.Clamp(e / d, 0, 1) : null;
}
