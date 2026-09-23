using System.Globalization;
using System.Numerics;

namespace Issun.Core.State;

/// <summary>
/// relay.py's <c>State</c>: the presence Discord should show, written by
/// request threads through <see cref="CheckinProcessor"/> and read by the
/// presence worker once a second.
/// </summary>
public sealed class PhoneState : IPhoneState
{
    /// <summary>
    /// How far back a seq may be and still count as a race between requests in
    /// flight at the same time. Those are milliseconds apart, and Ammy caps any
    /// one request at 20 s (timeoutIntervalForResource), so a minute is far
    /// outside anything a race produces.
    /// </summary>
    internal const double ClockJumpThreshold = 60.0;

    /// <summary>
    /// Slack when deciding whether two pushes came from the same app process.
    /// app_uptime_s is truncated to whole seconds and is sampled a moment
    /// before seq is stamped, so one process's pushes can disagree by a second
    /// or so; a genuine relaunch disagrees by however long the old process lived.
    /// </summary>
    internal const double SameProcessSlack = 5.0;

    private readonly object _gate = new();
    private TrackInfo? _track;
    private double _updatedAt;
    private double _lastCheckinAt;

    // Highest push `seq` accepted so far. Each push is its own independent
    // request on the phone with no ordering guarantee against the others, so
    // the farewell from stop() (playing: false) can land after an in-flight
    // now-playing push was already sent but arrives late -- applying it in
    // arrival order would leave Discord showing a track Ammy already stopped
    // broadcasting. Null until the first push carrying a seq arrives, so a
    // build that predates seq (or the first push after Issun starts) is never
    // compared against anything.
    private long? _lastSeq;

    // The app_uptime_s that arrived with _lastSeq, when it was an integer. The
    // pair identifies which app process the held seq belongs to.
    private BigInteger? _lastSeqUptime;

    public (TrackInfo? Track, double UpdatedAt) Get()
    {
        lock (_gate)
            return (_track, _updatedAt);
    }

    public double LastCheckinAt
    {
        get { lock (_gate) return _lastCheckinAt; }
    }

    internal long? LastSeq
    {
        get { lock (_gate) return _lastSeq; }
    }

    /// <summary>
    /// relay.py's <c>State.set()</c>: apply an update unless a newer one has
    /// already been seen, and stamp the check-in either way.
    ///
    /// <paramref name="uptime"/> is the push's diag app_uptime_s when it is an
    /// integer. Two cases relay.py got wrong are decided with it and the seq:
    /// see <see cref="Decide"/>.
    /// </summary>
    internal CheckinResult Apply(TrackInfo? track, long? seq, BigInteger? uptime, double now)
    {
        string? note;
        CheckinResult result;

        lock (_gate)
        {
            // Every authorised push is a check-in, applied or not: a phone whose
            // pushes are all arriving out of order is still very much alive, and
            // gap detection must not report it silent.
            _lastCheckinAt = now;

            (result, note) = Decide(seq, uptime);
            if (result.Applied)
            {
                _track = track;
                // Stamped on playing:false pushes too — relay.py did, and a paused
                // phone that keeps checking in must not look like a dead one.
                _updatedAt = now;
                if (seq is not null)
                {
                    _lastSeq = seq;
                    _lastSeqUptime = uptime;
                }
            }
        }

        // Written outside the lock: a slow log subscriber must not hold up the
        // presence worker's Get().
        if (note is not null)
            Log.Write(note);
        return result;
    }

    private (CheckinResult Result, string? Note) Decide(long? seq, BigInteger? uptime)
    {
        // Absent seq always applies — an app build that predates it talking to
        // a receiver that has it, same rule as RELAY_SECRET/RELAY_KEY.
        if (seq is not { } s || _lastSeq is not { } last || s > last)
            return (new CheckinResult(true, null), null);

        var seqBack = (last - s) / 1000.0;

        // Not in relay.py. A seq more than a minute older than the last one is
        // not a late request — those are milliseconds apart — but the phone's
        // clock having been set back. relay.py would compare every later push
        // against the high-water mark from before the change and drop them
        // all, and presence would stay frozen until the clock caught up again.
        if (seqBack > ClockJumpThreshold)
        {
            return (new CheckinResult(true, null),
                string.Create(CultureInfo.InvariantCulture,
                    $"[state] accepted push despite older seq (seq={s}, last={last}): {Seconds(seqBack)}s back is no in-flight race, the phone's clock moved backwards"));
        }

        // Also not in relay.py. A push from an app process launched after the
        // one whose seq is held supersedes it rather than being judged against
        // it: sessions are keyed and superseded, never stacked (Ammy CLAUDE.md,
        // open work item 7). Uptime alone going backwards is not enough to say
        // so. A request that was in flight while a later one overtook it comes
        // from the same process, sampled a moment earlier, so its uptime is
        // behind by about as much as its seq is — and treating that as a new
        // process would apply exactly the late push the seq exists to drop.
        // Only uptime falling well beyond what the seq explains is a relaunch.
        if (uptime is { } up && _lastSeqUptime is { } lastUp && up < lastUp)
        {
            var uptimeBack = (double)(lastUp - up);
            if (uptimeBack > seqBack + SameProcessSlack)
            {
                return (new CheckinResult(true, null),
                    string.Create(CultureInfo.InvariantCulture,
                        $"[state] accepted push despite older seq (seq={s}, last={last}): app uptime went {lastUp}s -> {up}s, so a new app process sent it and supersedes the old one"));
            }
        }

        var reason = string.Create(CultureInfo.InvariantCulture, $"dropped out-of-order push (seq={s}, last={last})");
        return (new CheckinResult(false, reason), "[state] " + reason);
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
