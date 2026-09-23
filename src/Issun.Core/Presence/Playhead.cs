using System.Globalization;

namespace Issun.Core.Presence;

/// <summary>
/// relay.py's <c>Playhead</c>: holds <c>start</c> steady for the length of a
/// track.
///
/// Discord renders the position as <c>now - start</c>, so <c>start</c> is an
/// anchor, not a reading. Recomputing it on every build is what made the bar
/// twitch before relay 1.2.0: the anchor moved, and the bar jumped with it.
///
/// The anchor comes from the phone's <c>elapsed</c>, which can be stale —
/// <c>currentPlaybackTime</c> is not updated eagerly for a backgrounded app.
/// That staleness is one-sided: a late read makes the song look *earlier*
/// than it is and can never make it look later, so of two candidate anchors
/// the smaller is the less stale one, and keeping the minimum converges on the
/// truth instead of wandering around it. (In practice no staleness has been
/// observed since the arrival-time fix, so that rule has never fired on the
/// owner's phone. It is insurance, not evidence that staleness exists.)
///
/// Three cases break the rule and are handled explicitly:
/// <list type="bullet">
/// <item>A different track — nothing worth keeping.</item>
/// <item>A seek — a deliberate jump either way, told apart from staleness by
/// size (<see cref="Timing.PlayheadSeekTolerance"/>).</item>
/// <item>The seconds just after a track change — <c>currentPlaybackTime</c> can
/// still report the previous song, which reads as further along and would
/// otherwise be locked in as a great anchor. Ammy's correction push 2.5 s
/// later must be able to move it back, so inside
/// <see cref="Timing.PlayheadSettleWindow"/> every reading is accepted.</item>
/// </list>
///
/// Thread-safe. One per <see cref="ActivityBuilder"/>, as relay.py had one
/// module-level playhead; it outlives Discord reconnects, as that one did.
/// </summary>
public sealed class Playhead
{
    // A reading implying the song is more than this much further along than the
    // anchor says replaces it. Staleness cannot produce that, so the new reading
    // is the fresher one; smaller moves are rounding, not information.
    private const double ForwardCorrection = 0.5;

    private readonly object _gate = new();
    private string? _key;
    private double? _start;
    private double _settledAt;

    /// <summary>
    /// The anchor for <paramref name="key"/>, given a reading of
    /// <paramref name="elapsed"/> seconds that arrived from the phone at
    /// <paramref name="observedAt"/>.
    ///
    /// relay.py named that parameter <c>now</c>, and passing the actual current
    /// time is the single most expensive bug the project has had: the worker
    /// builds once a second, the phone refreshes <c>elapsed</c> once every
    /// thirty, so for thirty builds the same frozen reading is in hand and
    /// <c>now - elapsed</c> slides the anchor forward one second per second. The
    /// bar fell behind for thirty seconds and snapped forward on the next push.
    /// The giveaway in relay.log was "[playhead] re-anchored +10.0s" repeating
    /// with identical drift — a noisy sensor gives varying numbers; a constant
    /// drift is a clock.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The reading gives no finite anchor. relay.py stored a NaN anchor in that
    /// case, which no later reading could ever move (every comparison with NaN is
    /// false), so it is refused here instead; <see cref="ActivityBuilder"/> never
    /// passes one.
    /// </exception>
    public double Anchor(string key, double elapsed, double observedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        var candidate = observedAt - elapsed;
        if (!double.IsFinite(candidate))
            throw new ArgumentOutOfRangeException(nameof(elapsed),
                string.Create(CultureInfo.InvariantCulture, $"No finite anchor from elapsed={elapsed} at {observedAt}."));

        string? line = null;
        double anchored;

        lock (_gate)
        {
            if (key != _key || _start is null)
            {
                _key = key;
                _start = candidate;
                _settledAt = observedAt + Timing.PlayheadSettleWindow;
                return candidate;
            }

            var drift = candidate - _start.Value;

            if (observedAt < _settledAt)
            {
                _start = candidate;
            }
            else if (Math.Abs(drift) > Timing.PlayheadSeekTolerance)
            {
                line = $"[playhead] re-anchored {PyText.Fixed(drift, 1, plusSign: true)}s — treating as a seek";
                _start = candidate;
            }
            else if (drift < -ForwardCorrection)
            {
                // Implies the song is further along than the current anchor says.
                // Staleness cannot produce that, so this reading is the fresher one.
                line = $"[playhead] corrected {PyText.Fixed(-drift, 1)}s forward";
                _start = candidate;
            }

            anchored = _start.Value;
        }

        // Outside the lock: a Log subscriber is free to do anything, including
        // reading presence state back.
        if (line is not null)
            Log.Write(line);
        return anchored;
    }

    /// <summary>
    /// Playback stopped. A pause of unknown length invalidates the anchor —
    /// Discord would otherwise keep ticking through it.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _key = null;
            _start = null;
        }
    }

    /// <summary>The current anchor, for tests and diagnostics; null after <see cref="Reset"/>.</summary>
    internal double? Start
    {
        get { lock (_gate) return _start; }
    }
}
