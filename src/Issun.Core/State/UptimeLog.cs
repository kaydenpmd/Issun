using System.Globalization;
using System.Numerics;
using System.Text;

namespace Issun.Core.State;

/// <summary>
/// ammy-uptime.log: an append-only record of when the phone stopped checking
/// in and when it came back, so "does iOS actually kill this?" is answered
/// with data instead of speculation.
///
/// Two halves write it, and both are needed. <see cref="NoteCheckin"/> writes
/// a gap when a push *arrives*, so it can only measure a silence that ended —
/// a death nobody noticed would never be written down at all.
/// <see cref="NoteSilence"/> is the other half: the presence worker watches the
/// clock and writes the silence the moment it starts.
///
/// Every phrase of every line is relay.py's, word for word. Months of history
/// were written by relay.py, and <see cref="Summarize"/> — like relay.py's
/// --summary before it — finds lines by those phrases. Issun is the relay now,
/// so "relay up throughout" still says the right thing. Only the stamp names
/// the program that wrote the line.
/// </summary>
public sealed class UptimeLog : IUptimeLog
{
    private readonly object _fileGate = new();
    private readonly object _silenceGate = new();
    private readonly IPhoneDiagnostics _diag;
    private readonly IClock _clock;
    private readonly double _startedAt;

    // Set when the silence starting at this last-seen time has been logged,
    // cleared when the phone is heard from again.
    private double? _silenceLoggedAt;

    /// <param name="path">The log file. Created on first write, with its folder.</param>
    /// <param name="diag">Supplies the phone build for the stamp and the snapshot for silence lines.</param>
    /// <param name="startedAt">
    /// When this process started (relay.py's RELAY_STARTED_AT). A gap longer
    /// than Issun has been running is one the receiver was down for, which is
    /// not the phone dying and must not be counted as one.
    /// </param>
    public UptimeLog(string path, IPhoneDiagnostics diag, IClock clock, double startedAt)
    {
        FilePath = path;
        _diag = diag;
        _clock = clock;
        _startedAt = startedAt;
    }

    public string FilePath { get; }

    /// <summary>relay.py's <c>_log_line()</c>.</summary>
    public void Line(string text)
    {
        var stamp = UnixTime.ToLocal(_clock.Now).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        // Versions go on the line rather than into separate files. Splitting the
        // log per version can't be undone and fragments it by a variable that
        // changes far more often than the thing being measured; a stamped line
        // can still be grouped any way you like afterwards, and keeps one
        // continuous history.
        var tags = $"[{IssunInfo.Wire} / app {_diag.PhoneVersion}]";
        Log.Write($"[uptime] {text}  {tags}");

        try
        {
            lock (_fileGate)
            {
                if (Path.GetDirectoryName(Path.GetFullPath(FilePath)) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                // CRLF, as relay.py's text-mode writes produced on Windows, so
                // an imported history and Issun's additions read as one file.
                File.AppendAllText(FilePath, $"{stamp}  {text}  {tags}\r\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            // relay.py passed silently here. The line above still reached
            // issun.log; this says why the uptime history is missing it.
            Log.Write($"[uptime] could not write {FilePath} ({ex.Message}); the line above is missing from it");
        }
    }

    /// <summary>
    /// relay.py's <c>note_silence()</c>: record the moment the phone stops
    /// checking in, rather than waiting for it to come back.
    ///
    /// The check works whether or not music was playing, because every push —
    /// playing:false included — is a check-in. That distinction matters: a
    /// paused app keeps checking in, a dead one doesn't.
    /// </summary>
    public void NoteSilence(double lastSeen)
    {
        if (lastSeen <= 0)              // nothing has ever checked in
            return;

        var silentFor = _clock.Now - lastSeen;
        lock (_silenceGate)
        {
            if (silentFor <= Timing.GapThreshold)
            {
                _silenceLoggedAt = null;
                return;
            }
            if (_silenceLoggedAt == lastSeen)
                return;                 // already recorded this silence
            _silenceLoggedAt = lastSeen;
        }

        var when = UnixTime.ToLocal(lastSeen).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // What the phone was last doing is the whole point of this line. Without
        // it a death is only a timestamp, which is how the September 2026
        // keepalive regression went four days without anyone being able to say why.
        Line($"phone stopped checking in  last seen {when}  last state: {_diag.Summary()}");
    }

    /// <summary>
    /// relay.py's <c>note_checkin()</c>: called on every push from the phone.
    /// If there was a meaningful silence beforehand, record it — and say
    /// whether the receiver itself was down for it, because a PC reboot is not
    /// the phone dying and shouldn't be counted as one.
    /// </summary>
    /// <param name="previous">The check-in before this one; 0 = none since Issun started.</param>
    /// <param name="previousUptime">app_uptime_s from the diag snapshot this push replaced.</param>
    /// <param name="current">This push's diag snapshot; null when it carried none.</param>
    internal void NoteCheckin(double previous, double now, PyValue previousUptime, PyDict? current)
    {
        if (previous <= 0)
            return;

        var gap = now - previous;
        if (gap < Timing.GapThreshold)
            return;

        var uptime = now - _startedAt;
        var wasDown = uptime < gap;
        var verdict = GapVerdict(gap, wasDown, previousUptime, current);
        Line(wasDown
            ? $"gap {FormatGap(gap)}  relay was down for {FormatGap(gap - uptime)} of it{verdict}"
            : $"gap {FormatGap(gap)}  phone silent, relay up throughout{verdict}");
    }

    /// <summary>
    /// relay.py's <c>_gap_verdict()</c>: whether the app survived the silence
    /// that just ended, in its own words.
    ///
    /// Silence has two causes — iOS killed the app, or the app was alive and
    /// could not reach the receiver — and they need different fixes. From here
    /// they look identical, because a push that fails never arrives. The
    /// evidence therefore has to survive the outage and be carried back by the
    /// first push that gets through, which is what these two fields are for.
    ///
    /// <c>app_uptime_s</c> resets when the process does. Uptime that advanced by
    /// roughly the length of the gap means one process ran straight through it.
    /// Uptime that went backwards, or forwards by far less than the gap, means
    /// a different process is talking now — it died and something restarted it.
    ///
    /// <c>push_fails</c> corroborates from the other direction: an app that spent
    /// the silence failing to send knows it was alive, and says so. The two
    /// agree or the verdict is worth distrusting.
    /// </summary>
    internal static string GapVerdict(double gap, bool relayWasDown, PyValue previousUptime, PyDict? current)
    {
        // An app build predating this, the first push, or — unlike relay.py,
        // which fell back on whatever snapshot it last held — a push that
        // carried no diag of its own.
        var now = current?.Get("app_uptime_s") ?? PyValue.None;
        if (!previousUptime.IsInt || !now.IsInt)
            return "";

        var prev = previousUptime.IntValue;
        var up = now.IntValue;

        var tried = "";
        var fails = current!.Get("push_fails");
        if (fails.IsInt && fails.IntValue > 0)
        {
            tried = $", {fails} pushes failed";
            var offline = current.Get("offline_s");
            if (offline.IsInt)
                tried += $" over {FormatGap(offline.IntValue)}";
        }

        // 10% of slack absorbs integer truncation and ordinary clock drift. The
        // backwards case is checked separately because it is unambiguous on its own.
        if (up < prev || PyText.LessThan(up - prev, gap * 0.9))
            return $", app restarted {previousUptime}s -> {now}s — it died{tried}";
        // When the receiver was the thing that was down, that already explains
        // the silence; repeating "the path failed" would be editorialising over
        // a cause the same line has just stated.
        if (relayWasDown)
            return $", app stayed up {previousUptime}s -> {now}s{tried}";
        return $", app stayed up {previousUptime}s -> {now}s — the path failed, not the app{tried}";
    }

    /// <summary>relay.py's <c>_format_gap()</c>: whole seconds as H:MM:SS, hours unbounded.</summary>
    internal static string FormatGap(double seconds)
    {
        // Python's int() of a float truncates towards zero. Gaps are differences
        // of clock readings and always finite; a NaN would have raised in
        // relay.py, and here it prints as unreadable rather than crashing a check-in.
        if (!double.IsFinite(seconds))
            return "??:??:??";
        return FormatGap(new BigInteger(Math.Truncate(seconds)));
    }

    internal static string FormatGap(BigInteger total)
    {
        var hours = PyText.FloorDiv(total, 3600);
        var minutes = PyText.FloorDiv(PyText.FloorMod(total, 3600), 60);
        var secs = PyText.FloorMod(total, 60);
        return $"{PyText.Pad2(hours)}:{PyText.Pad2(minutes)}:{PyText.Pad2(secs)}";
    }

    /// <summary>
    /// relay.py's <c>print_summary()</c> (<c>python relay.py --summary</c>), returned as text.
    ///
    /// Two changes, both to reading the log rather than to what is counted.
    /// "issun started" counts as a start alongside relay.py's "relay started".
    /// And the build is read from the stamp at the end of the line: relay.py
    /// searched for the first "app ...]" anywhere, which from relay 1.8.0 on
    /// matched the verdict — "app restarted 3358s -> 3359s — it died  [relay
    /// 1.8.0 / app 1.0 (51)" became a build name — so every gap with a verdict
    /// was filed under a group of its own instead of under its build.
    /// </summary>
    public string Summarize()
    {
        string content;
        try
        {
            if (!File.Exists(FilePath))
                return "No uptime log yet.";
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
            content = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            Log.Write($"[uptime] could not read {FilePath} ({ex.Message})");
            return $"Could not read the uptime log: {ex.Message}";
        }

        return Summarize(content);
    }

    internal static string Summarize(string content)
    {
        int starts = 0, silences = 0, mixed = 0;
        // Grouped by the app build that was running, so a regression in one iOS
        // build stands out instead of being averaged into everything before it.
        // Lines predating the stamping have no build to attribute.
        var byApp = new Dictionary<string, List<BigInteger>>(StringComparer.Ordinal);

        foreach (var line in PyText.SplitLines(content))
        {
            var app = StampedApp(line);

            if (line.Contains("relay started", StringComparison.Ordinal)
                || line.Contains("issun started", StringComparison.Ordinal))
            {
                starts++;
            }
            else if (line.Contains("stopped checking in", StringComparison.Ordinal))
            {
                silences++;
            }
            else if (line.Contains("gap ", StringComparison.Ordinal))
            {
                if (!TryParseGap(line, out var seconds))
                    continue;
                if (line.Contains("phone silent", StringComparison.Ordinal))
                {
                    if (!byApp.TryGetValue(app, out var list))
                        byApp[app] = list = [];
                    list.Add(seconds);
                }
                else
                {
                    mixed++;
                }
            }
        }

        var counted = byApp.Values.Sum(v => v.Count);
        var output = new List<string>
        {
            Invariant($"relay starts            : {starts}"),
            Invariant($"silences detected live  : {silences}"),
        };
        var unreturned = silences - counted - mixed;
        if (unreturned > 0)
            output.Add(Invariant($"  never came back       : {unreturned}   <- app died and stayed dead"));
        output.Add(Invariant($"gaps including downtime : {mixed}"));

        if (byApp.Count == 0)
        {
            output.Add("");
            output.Add("No unexplained phone gaps recorded — backgrounding is holding.");
            return string.Join("\n", output);
        }

        output.Add("");
        output.Add("phone-only gaps, by app build");
        foreach (var app in byApp.Keys.OrderBy(k => k, PyText.CodePointOrder))
        {
            var gaps = byApp[app].Order().ToList();
            var total = gaps.Aggregate(BigInteger.Zero, (sum, g) => sum + g);
            output.Add($"  {app}");
            output.Add(Invariant($"    count    : {gaps.Count}"));
            output.Add($"    longest  : {FormatGap(gaps[^1])}");
            output.Add($"    median   : {FormatGap(gaps[gaps.Count / 2])}");
            output.Add($"    total    : {FormatGap(total)}");
        }
        return string.Join("\n", output);
    }

    /// <summary>
    /// The build from a line's trailing "[relay 1.9.0 / app 1.0 (80)]" or
    /// "[issun 0.1.0 (12) / app 1.0 (80)]" stamp, or the group for lines
    /// written before builds were recorded.
    /// </summary>
    private static string StampedApp(string line)
    {
        var s = PyText.RStrip(line);
        if (s.EndsWith(']'))
        {
            var at = Math.Max(s.LastIndexOf("  [relay ", StringComparison.Ordinal),
                              s.LastIndexOf("  [issun ", StringComparison.Ordinal));
            if (at >= 0)
            {
                var k = s.IndexOf(" / app ", at, StringComparison.Ordinal);
                if (k >= 0)
                    return PyText.Strip(s[(k + " / app ".Length)..^1]);
            }
        }
        return "before builds were recorded";
    }

    /// <summary>
    /// relay.py's parse of "gap HH:MM:SS": the first token after the first
    /// "gap ", split on colons into exactly three Python ints. Anything else
    /// is skipped, as relay.py's except (IndexError, ValueError) skipped it.
    /// </summary>
    private static bool TryParseGap(string line, out BigInteger seconds)
    {
        seconds = BigInteger.Zero;
        var rest = line[(line.IndexOf("gap ", StringComparison.Ordinal) + "gap ".Length)..];
        var next = rest.IndexOf("gap ", StringComparison.Ordinal);
        if (next >= 0)
            rest = rest[..next];

        var tokens = PyText.SplitWhitespace(rest);
        if (tokens.Count == 0)
            return false;

        var parts = tokens[0].Split(':');
        if (parts.Length != 3
            || !PyText.TryParseInt(parts[0], out var hours)
            || !PyText.TryParseInt(parts[1], out var minutes)
            || !PyText.TryParseInt(parts[2], out var secs))
            return false;

        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
