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
