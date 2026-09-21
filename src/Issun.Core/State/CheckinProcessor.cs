namespace Issun.Core.State;

/// <summary>
/// Everything relay.py's do_POST did with a push once it was authenticated and
/// parsed, as one atomic step.
///
/// relay.py ran the same sequence without a lock around it, and it showed. On
/// 14 Sept 2026 a push ended a 38-minute silence and a second push — the
/// correction Ammy fires right after a change, most likely — arrived with it.
/// Both read the same pre-gap check-in as "previous", so the gap was logged
/// twice; and by the time the second one ran, the first had already replaced
/// the uptime it compared against, so the second verdict read "app restarted
/// 3358s -> 3359s — it died". Uptime going *up* by a second is not a restart.
/// One of the fourteen death verdicts in the log was double-counted and wrong.
/// Here the whole sequence holds one lock, so the second push sees the first
/// as its previous check-in, measures no gap, and writes nothing.
/// </summary>
public sealed class CheckinProcessor : ICheckinProcessor
{
    private readonly object _gate = new();
    private readonly PhoneState _state;
    private readonly PhoneDiagnostics _diag;
    private readonly UptimeLog _uptime;
    private readonly IClock _clock;

    public CheckinProcessor(PhoneState state, PhoneDiagnostics diag, UptimeLog uptime, IClock clock)
    {
        _state = state;
        _diag = diag;
        _uptime = uptime;
        _clock = clock;
    }

    public CheckinResult Accept(NowPlayingPush push)
    {
        lock (_gate)
        {
            var now = _clock.Now;
            var previous = _state.LastCheckinAt;

            _diag.RecordVersion(push.Raw["app_version"]);
            // The raw node rather than push.Diag: record_phone_diag took whatever
            // json.loads produced, and the log's rendering of it depends on that.
            var diag = _diag.RecordDiag(push.Raw["diag"], now);

            // push.Track is null for playing:false, so a farewell clears the
            // track — relay.py's state.set(body if body.get("playing") else None).
            var result = _state.Apply(push.Track, push.Seq, diag.CurrentUptime, now);

            _uptime.NoteCheckin(previous, now, diag.PreviousUptime, diag.Current);
            return result;
        }
    }
}
