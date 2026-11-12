using System.Globalization;
using System.Text.Json.Nodes;
using Issun.Core.State;

namespace Issun.Core.Tests.State;

// Expected values in RelayParityData were printed by relay.py itself; these
// tests only feed Issun the same JSON text and compare.
public class PhoneDiagnosticsTests
{
    private static JsonNode? Json(string text) => JsonNode.Parse(text);

    private static PyDict Dict(string json) => PyDict.FromJson(JsonNode.Parse(json)!.AsObject());

    public static TheoryData<string> SummaryNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in RelayParityData.Summaries)
            data.Add(s.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SummaryNames))]
    public void Summary_matches_relay(string name)
    {
        var (_, json, expected) = RelayParityData.Summaries.Single(s => s.Name == name);

        Assert.Equal(expected, PhoneDiagnostics.Summarize(Dict(json)));

        var diag = new PhoneDiagnostics();
        using var capture = Log.Capture();
        diag.RecordDiag(Json(json), 1);
        Assert.Equal(expected, diag.Summary());
    }

    public static TheoryData<string> SequenceNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in RelayParityData.Sequences)
            data.Add(s.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SequenceNames))]
    public void Change_logging_matches_relay(string name)
    {
        var steps = RelayParityData.Sequences.Single(s => s.Name == name).Steps;
        var diag = new PhoneDiagnostics();

        for (var i = 0; i < steps.Length; i++)
        {
            using var capture = Log.Capture();
            diag.RecordDiag(Json(steps[i].Json), 1000 + i);
            Assert.Equal(steps[i].Lines, capture.Lines);
        }
    }

    [Fact]
    public void Version_logging_matches_relay()
    {
        var diag = new PhoneDiagnostics();
        Assert.Equal("unknown", diag.PhoneVersion);

        foreach (var (json, lines, version) in RelayParityData.Versions)
        {
            using var capture = Log.Capture();
            diag.RecordVersion(Json(json));
            Assert.Equal(lines, capture.Lines);
            Assert.Equal(version, diag.PhoneVersion);
        }
    }

    [Fact]
    public void A_missing_or_non_object_diag_changes_nothing()
    {
        var diag = new PhoneDiagnostics();
        using var capture = Log.Capture();

        Assert.Null(diag.RecordDiag(null, 5).Current);
        Assert.Null(diag.RecordDiag(Json("[1]"), 5).Current);
        Assert.Null(diag.RecordDiag(Json("\"x\""), 5).Current);

        Assert.Empty(capture.Lines);
        Assert.Null(diag.Latest);
        Assert.Equal(0, diag.LatestAt);
        Assert.Equal("no diagnostics (app build predates them)", diag.Summary());
    }

    [Fact]
    public void Latest_is_a_copy_and_an_empty_object_reads_as_none()
    {
        var diag = new PhoneDiagnostics();
        using var capture = Log.Capture();

        diag.RecordDiag(Json("{}"), 10);
        Assert.Null(diag.Latest);
        Assert.Equal(10, diag.LatestAt);

        diag.RecordDiag(Json("{\"route\": \"Speaker\", \"app_uptime_s\": 5}"), 20);
        var latest = diag.Latest!;
        Assert.Equal("Speaker", latest["route"]!.GetValue<string>());
        Assert.Equal(20, diag.LatestAt);

        latest["route"] = "changed";
        Assert.Equal("Speaker", diag.Latest!["route"]!.GetValue<string>());
    }

    [Fact]
    public void The_record_carries_the_uptime_it_replaced()
    {
        var diag = new PhoneDiagnostics();
        using var capture = Log.Capture();

        var first = diag.RecordDiag(Json("{\"app_uptime_s\": 100}"), 1);
        Assert.Equal(PyKind.None, first.PreviousUptime.Kind);
        Assert.Equal(100, (int)first.CurrentUptime!.Value);

        var second = diag.RecordDiag(Json("{\"app_uptime_s\": 130}"), 2);
        Assert.Equal("100", second.PreviousUptime.Repr());
        Assert.Equal(130, (int)second.CurrentUptime!.Value);

        // A float uptime is not an int to relay.py's isinstance, so it carries none.
        Assert.Null(diag.RecordDiag(Json("{\"app_uptime_s\": 160.0}"), 3).CurrentUptime);
    }

    [Fact]
    public void Lone_surrogates_do_not_throw()
    {
        // Legal JSON that json.loads accepts and System.Text.Json won't decode.
        var diag = new PhoneDiagnostics();
        using var capture = Log.Capture();

        var value = diag.RecordDiag(Json("{\"last_error\": \"bad \\ud800 here\", \"app_uptime_s\": 7}"), 1);
        Assert.Equal("'bad \\ud800 here'", value.Current!.Get("last_error").Repr());

        var key = diag.RecordDiag(Json("{\"\\ud800\": 1, \"app_uptime_s\": 8}"), 2);
        Assert.Null(key.Current);
        Assert.StartsWith("[keepalive] unreadable diag ignored (", capture.Lines[^1]);
        Assert.Equal(1, diag.LatestAt);

        diag.RecordVersion(Json("\"1.0 \\udfff\""));
        Assert.Equal("1.0 \xDFFF", diag.PhoneVersion);
    }

    // ───────────────────────── Ammy build 167's additions ─────────────────────────

    /// <summary>A UTC ISO 8601 instant in this machine's local time, as the log prints it.</summary>
    private static string Local(string utc) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture).ToLocalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public void The_summary_carries_build_167s_fields_beside_the_ones_they_explain()
    {
        const string json = "{\"running\": true, \"engine_running\": false, \"node_playing\": false, \"resumes\": 4, " +
            "\"resume_failures\": 3, \"int_began\": 8, \"int_ended\": 1, \"config_changes\": 0, \"route_changes\": 2, " +
            "\"media_resets\": 0, \"self_heals\": 2, \"secs_since_resume\": 40, \"route\": \"Speaker\", " +
            "\"fail_run\": 2, \"fail_run_max\": 5, \"int_on_resume\": 1, " +
            "\"last_error\": \"engine.start: refused [2003329396 'what']\", \"fail_stage\": \"engine.start\", " +
            "\"fail_code\": 2003329396, \"int_reason\": 0, \"secs_since_fail\": 3, \"down_s\": 12, " +
            "\"secs_since_began\": 15, \"secs_since_ended\": 900, \"app_state\": \"background\", \"mem_mb\": 21, " +
            "\"mem_avail_mb\": 2000, \"mem_avail_min_mb\": 1990, \"mem_warnings\": 3, \"low_power\": false, " +
            "\"thermal\": \"nominal\", \"app_uptime_s\": 7000, \"device_uptime_s\": 90000, \"push_fails\": 0, " +
            "\"push_fails_total\": 1}";

        Assert.Equal(
            "engine=no want=yes route=Speaker resumes=4 fails=3 failrun=2/5 failstage=engine.start " +
            "failcode=2003329396 sincefail=3s down=12s heals=2 cfg=0 routechg=2 int=8/1 intlate=1 intreason=0 " +
            "sincebegan=15s sinceended=900s mem=21MB avail=2000MB availmin=1990MB memwarn=3 state=background " +
            "lpm=no thermal=nominal appup=7000s devup=90000s pushfail=0/1 " +
            "last_error=\"engine.start: refused [2003329396 'what']\"",
            PhoneDiagnostics.Summarize(Dict(json)));

        // After a recovery: the run is over and the engine is up, and the last
        // failure's step and code are still there to read.
        Assert.Equal(
            "engine=yes want=yes route=? resumes=? fails=3 failrun=0/5 failstage=engine.start failcode=2003329396 " +
            "sincefail=300s heals=? cfg=? routechg=? int=?/? mem=?MB avail=?MB availmin=?MB memwarn=? state=? " +
            "lpm=no thermal=? appup=?s devup=?s pushfail=?/?",
            PhoneDiagnostics.Summarize(Dict("{\"engine_running\": true, \"running\": true, \"resume_failures\": 3, " +
                "\"fail_run\": 0, \"fail_run_max\": 5, \"fail_stage\": \"engine.start\", \"fail_code\": 2003329396, " +
                "\"secs_since_fail\": 300, \"down_s\": null}")));
    }

    [Fact]
    public void Late_interruption_notices_and_reasons_are_logged_as_they_change()
    {
        var diag = new PhoneDiagnostics();
        using (Log.Capture())
            diag.RecordDiag(Json("{\"app_uptime_s\": 100, \"int_began\": 1, \"int_on_resume\": 0}"), 1);

        using var capture = Log.Capture();
        diag.RecordDiag(Json("{\"app_uptime_s\": 130, \"int_began\": 2, \"int_on_resume\": 1, \"int_reason\": 0}"), 2);
        diag.RecordDiag(Json("{\"app_uptime_s\": 160, \"int_began\": 2, \"int_on_resume\": 1, \"int_reason\": 0}"), 3);

        Assert.Equal(new[] { "[keepalive] int_reason None -> 0; int_began +1 (now 2); int_on_resume +1 (now 1)" },
            capture.Lines);
    }

    [Fact]
    public void The_previous_runs_journal_is_logged_once_in_local_time()
    {
        var diag = new PhoneDiagnostics();
        using (Log.Capture())
            diag.RecordDiag(Json("{\"app_uptime_s\": 6554}"), 1);

        const string carried = "\"prev_alive\": \"2026-11-04T18:50:06Z engine=no\", \"prev_tail\": [" +
            "\"2026-11-04T17:00:00Z --- launch, Ammy 1.0 (167)\", " +
            "\"2026-11-04T18:49:58Z failed, 1 in a row: engine.start: refused [2003329396 'what']\", " +
            "\"no stamp, and a\\nline break\"]";

        using (var capture = Log.Capture())
        {
            diag.RecordDiag(Json("{\"app_uptime_s\": 12, " + carried + "}"), 2);
            Assert.Equal(new[]
            {
                "[keepalive] app restarted: uptime 6554s -> 12s, counters reset",
                $"[keepalive] previous run last ran {Local("2026-11-04T18:50:06Z")} engine=no; its journal's last 3 lines follow",
                $"[keepalive] journal: {Local("2026-11-04T17:00:00Z")} --- launch, Ammy 1.0 (167)",
                $"[keepalive] journal: {Local("2026-11-04T18:49:58Z")} failed, 1 in a row: engine.start: refused [2003329396 'what']",
                "[keepalive] journal: no stamp, and a line break",
            }, capture.Lines);
        }

        // Sent again by a phone that didn't hear the first one land.
        using (var again = Log.Capture())
        {
            diag.RecordDiag(Json("{\"app_uptime_s\": 42, " + carried + "}"), 3);
            Assert.Empty(again.Lines);
        }

        // The latest snapshot keeps them, for GET /diag, until the next replaces it.
        Assert.NotNull(diag.Latest!["prev_tail"]);
    }

    [Fact]
    public void A_previous_run_missing_its_stamp_or_its_lines_says_which()
    {
        var diag = new PhoneDiagnostics();
        using var capture = Log.Capture();

        diag.RecordDiag(Json("{\"app_uptime_s\": 10, \"prev_tail\": [\"2026-11-04T17:00:00Z stop\"]}"), 1);
        diag.RecordDiag(Json("{\"app_uptime_s\": 5, \"prev_alive\": \"2026-11-04T18:50:06Z engine=yes\"}"), 2);
        diag.RecordDiag(Json("{\"app_uptime_s\": 4, \"prev_alive\": \"2026-11-04T18:50:06Z engine=yes\", \"prev_tail\": []}"), 3);

        Assert.Equal(new[]
        {
            Assert.Single(capture.Lines, l => l.StartsWith("[keepalive] first report: ", StringComparison.Ordinal)),
            "[keepalive] previous run left no last-ran stamp; its journal's last 1 lines follow",
            $"[keepalive] journal: {Local("2026-11-04T17:00:00Z")} stop",
            "[keepalive] app restarted: uptime 10s -> 5s, counters reset",
            $"[keepalive] previous run last ran {Local("2026-11-04T18:50:06Z")} engine=yes; its journal was empty",
            "[keepalive] app restarted: uptime 5s -> 4s, counters reset",
            $"[keepalive] previous run last ran {Local("2026-11-04T18:50:06Z")} engine=yes; its journal was empty",
        }, capture.Lines);
    }

    // Deliberate deviation: Python's isinstance(True, int) is True, so relay.py
    // would have read a bool as a counter of 0 or 1 and an uptime of 0 or 1 s.
    [Fact]
    public void Bools_are_not_counters_or_uptimes()
    {
        var diag = new PhoneDiagnostics();
        using (Log.Capture())
            diag.RecordDiag(Json("{\"app_uptime_s\": true, \"self_heals\": false, \"running\": true}"), 1);

        using var capture = Log.Capture();
        diag.RecordDiag(Json("{\"app_uptime_s\": 0, \"self_heals\": 3, \"running\": true}"), 2);

        // relay.py: "[keepalive] app restarted: uptime Trues -> 0s, counters reset".
        // relay.py without that: "[keepalive] self_heals +3 (now 3)".
        Assert.Empty(capture.Lines);
    }
}
