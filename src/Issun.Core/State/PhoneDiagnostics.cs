using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Issun.Core.State;

/// <summary>
/// What <see cref="PhoneDiagnostics.RecordDiag"/> saw, for the rest of the
/// check-in: the app uptime of the snapshot it replaced (what _gap_verdict
/// calls _prev_app_uptime), and the snapshot this push carried. Current is
/// null when the push carried no diag object at all.
/// </summary>
internal readonly record struct DiagRecord(PyValue PreviousUptime, PyDict? Current)
{
    public static readonly DiagRecord None = new(PyValue.None, null);

    /// <summary>This push's app_uptime_s, when it is an integer.</summary>
    public BigInteger? CurrentUptime =>
        Current?.Get("app_uptime_s") is { IsInt: true } up ? up.IntValue : null;
}

/// <summary>
/// The phone's own account of itself: which Ammy build is talking, and the
/// <c>diag</c> snapshot every push carries.
///
/// The app cannot report its own death — by the time anyone notices it is gone
/// there is nothing left running to ask. So the most recent snapshot is kept
/// and printed on the "phone stopped checking in" line. Two readings decide
/// most cases:
///
///   engine_running false while running is true — the keepalive was already
///   dead before the app was, and iOS suspended it for having no audio to
///   justify its background time.
///
///   mem_mb climbing towards a death — iOS reclaimed the app under memory
///   pressure instead, which is a different bug and is not fixed by anything in
///   KeepAlive.swift.
///
/// Values are held with Python's types (<see cref="PyValue"/>) because every
/// line this class writes is compared, character for character, with months of
/// relay.log that Python wrote.
/// </summary>
public sealed class PhoneDiagnostics : IPhoneDiagnostics
{
    // Logged the moment they change, rather than only at a death.
    private static readonly string[] Flags =
        ["engine_running", "running", "low_power", "app_state", "route", "thermal"];

    // Counters only ever climb, so any increase is an event that just happened.
    private static readonly string[] Counters =
    [
        "resume_failures", "self_heals", "config_changes", "media_resets",
        // A memory warning arriving before a death is direct evidence of
        // pressure, rather than the inference you are left with from footprint
        // alone.
        "mem_warnings",
        // Both halves of the interruption pair. began outrunning ended is the
        // case KeepAlive.swift warns about — iOS does not guarantee .ended, and
        // before relay 1.5.1 only began was logged, so the mismatch that
        // motivated counting them separately was the one thing the log could
        // not show.
        "int_began", "int_ended",
    ];

    private readonly object _gate = new();

    // The build matters more than Issun's own version: whether the phone
    // survives backgrounding is decided by code in the iOS build, so it is the
    // axis every uptime-log line is attributed to.
    private string _version = "unknown";

    private PyDict _diag = PyDict.Empty;
    private JsonObject? _diagJson;
    private double _diagAt;

    public string PhoneVersion
    {
        get { lock (_gate) return _version; }
    }

    // What the source calls itself (app_name), for the window's "Source: Ammy
    // 1.0 (97)". Null until a source sends one; older Ammy builds don't.
    private string? _name;

    public string? SourceName
    {
        get { lock (_gate) return _name; }
    }

    /// <summary>
    /// The source's name from <c>app_name</c>: a non-empty JSON string, clipped
    /// to 32 code points like the version. Anything else is ignored, so a
    /// source that sends no name keeps showing only its version.
    /// </summary>
    internal void RecordName(JsonNode? value)
    {
        if (value is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.String)
            return;
        string raw;
        try { raw = v.GetValue<string>(); }
        catch (InvalidOperationException) { return; }
        var text = PyText.Clip(raw.Trim(), 32);
        if (text.Length == 0)
            return;
        lock (_gate)
            _name = text;
    }

    /// <summary>
    /// A copy of the latest snapshot, so a caller can't mutate the stored one
    /// or read it while another thread replaces it. An empty object reads as
    /// none, as relay.py's <c>if not _phone_diag</c> did.
    /// </summary>
    public JsonObject? Latest
    {
        get
        {
            lock (_gate)
                return _diag.Count > 0 ? _diagJson?.DeepClone().AsObject() : null;
        }
    }

    public double LatestAt
    {
        get { lock (_gate) return _diagAt; }
    }

    public string Summary()
    {
        lock (_gate)
            return Summarize(_diag);
    }

    /// <summary>
    /// relay.py's <c>record_phone_version()</c>. Anything falsy is ignored;
    /// anything else is rendered with Python's str() and clipped to 32 code
    /// points, and a change is logged.
    /// </summary>
    internal void RecordVersion(JsonNode? value)
    {
        PyValue parsed;
        try
        {
            parsed = PyValue.FromJson(value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            Log.Write($"[init] unreadable app_version ignored ({ex.Message})");
            return;
        }
        if (!parsed.Truthy)
            return;

        var text = PyText.Clip(parsed.ToString(), 32);
        lock (_gate)
        {
            if (text == _version)
                return;
            _version = text;
        }
        Log.Write($"[init] phone reports Ammy {text}");
    }

    /// <summary>
    /// relay.py's <c>record_phone_diag()</c>: store the phone's latest
    /// self-report, and log whatever changed.
    ///
    /// Logging changes as they happen — rather than only dumping state at a
    /// death — is what turns the log into a sequence: a route change stopping
    /// the engine, a resume failing, a self-heal putting it back. A death
    /// preceded by "config_changes +1" and no recovery says something a
    /// timestamp cannot.
    /// </summary>
    internal DiagRecord RecordDiag(JsonNode? value, double now)
    {
        if (value is not JsonObject json)
            return DiagRecord.None;

        PyDict current;
        JsonObject copy;
        try
        {
            current = PyDict.FromJson(json);
            copy = json.DeepClone().AsObject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // Nothing Ammy sends does this; a push that does must not fail the
            // check-in over its self-report.
            Log.Write($"[keepalive] unreadable diag ignored ({ex.Message})");
            return DiagRecord.None;
        }

        PyDict previous;
        lock (_gate)
        {
            previous = _diag;
            _diag = current;
            _diagJson = copy;
            _diagAt = now;
        }

        // Captured here, at the moment the snapshot is replaced: note_checkin
        // runs after this and compares the two uptimes to decide whether a
        // silence was a death or a network outage.
        var record = new DiagRecord(previous.Count > 0 ? previous.Get("app_uptime_s") : PyValue.None, current);

        foreach (var line in Changes(previous, current))
            Log.Write(line);
        return record;
    }

    private static IEnumerable<string> Changes(PyDict previous, PyDict value)
    {
        if (previous.Count == 0)
        {
            yield return $"[keepalive] first report: {Summarize(value)}";
            yield break;
        }

        // A relaunch resets every counter at once, so the comparison below sees
        // only decreases and prints nothing — the app restarting would be the
        // one event the log stays silent about. Report it and stop: every other
        // field here is being compared across two different processes and means
        // nothing.
        var wasUp = previous.Get("app_uptime_s");
        var nowUp = value.Get("app_uptime_s");
        if (wasUp.IsInt && nowUp.IsInt && nowUp.IntValue < wasUp.IntValue)
        {
            yield return $"[keepalive] app restarted: uptime {wasUp}s -> {nowUp}s, counters reset";
            yield break;
        }

        var changes = new List<string>();
        foreach (var key in Flags)
        {
            if (value.ContainsKey(key) && !PyValue.Equal(previous.Get(key), value.Get(key)))
                changes.Add($"{key} {previous.Get(key).Repr()} -> {value.Get(key).Repr()}");
        }
        foreach (var key in Counters)
        {
            // Python's isinstance(x, int) accepts True and False; this doesn't.
            // A bool is not a count, and no build of Ammy sends one here.
            var was = previous.Get(key, PyValue.Zero);
            var now = value.Get(key, PyValue.Zero);
            if (was.IsInt && now.IsInt && now.IntValue > was.IntValue)
                changes.Add(string.Create(CultureInfo.InvariantCulture, $"{key} +{now.IntValue - was.IntValue} (now {now})"));
        }

        if (changes.Count > 0)
            yield return $"[keepalive] {string.Join("; ", changes)}";

        var error = value.Get("last_error");
        if (error.Truthy && !PyValue.Equal(error, previous.Get("last_error")))
            yield return $"[keepalive] resume failed: {error}";
    }

    /// <summary>relay.py's <c>_diag_summary()</c>: the phone's last self-report on one line, for the silence entry.</summary>
    internal static string Summarize(PyDict d)
    {
        if (d.Count == 0)
            return "no diagnostics (app build predates them)";

        string Flag(string key) => d.Get(key).Truthy ? "yes" : "no";
        string Value(string key) => d.TryGet(key, out var v) ? v.ToString() : "?";

        var parts = new List<string>
        {
            $"engine={Flag("engine_running")}",
            $"want={Flag("running")}",
            $"route={Value("route")}",
            $"resumes={Value("resumes")}",
            $"fails={Value("resume_failures")}",
            $"heals={Value("self_heals")}",
            $"cfg={Value("config_changes")}",
            $"routechg={Value("route_changes")}",
            $"int={Value("int_began")}/{Value("int_ended")}",
            $"mem={Value("mem_mb")}MB",
            $"avail={Value("mem_avail_mb")}MB",
            $"availmin={Value("mem_avail_min_mb")}MB",
            $"memwarn={Value("mem_warnings")}",
            $"state={Value("app_state")}",
            $"lpm={Flag("low_power")}",
            $"thermal={Value("thermal")}",
            $"appup={Value("app_uptime_s")}s",
            $"devup={Value("device_uptime_s")}s",
            $"pushfail={Value("push_fails")}/{Value("push_fails_total")}",
        };

        // Present only while the phone is failing to send, so seeing it at all
        // is the signal. On a death line it is absent by definition — the last
        // push to arrive is one that worked.
        if (d.Get("offline_s").Kind != PyKind.None)
            parts.Add($"offline={d.Get("offline_s")}s");
        if (d.Get("bg_secs_left").Kind != PyKind.None)
            parts.Add($"bgleft={d.Get("bg_secs_left")}s");
        if (d.Get("last_error").Truthy)
            parts.Add($"last_error={d.Get("last_error").Repr()}");
        return string.Join(" ", parts);
    }
}
