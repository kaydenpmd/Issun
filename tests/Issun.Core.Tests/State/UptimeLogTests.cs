using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Issun.Core.State;

namespace Issun.Core.Tests.State;

/// <summary>An IPhoneDiagnostics that says whatever the test sets.</summary>
internal sealed class FakeDiagnostics : IPhoneDiagnostics
{
    public string PhoneVersion { get; set; } = "unknown";
    public JsonObject? Latest { get; set; }
    public double LatestAt { get; set; }
    public string SummaryText { get; set; } = "no diagnostics (app build predates them)";
    public string Summary() => SummaryText;
}

/// <summary>A folder under %TEMP% that disappears with the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "issun-state-tests", Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public class UptimeLogTests : IDisposable
{
    // The instant gen.py pinned relay.py's clock to; offsets in RelayParityData are from here.
    private const double Base = 1_750_000_000.0;

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Tags(string phoneVersion) => $"  [{IssunInfo.Wire} / app {phoneVersion}]";

    private static string Stamp(double at) =>
        UnixTime.ToLocal(at).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static string When(double at) => UnixTime.ToLocal(at).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The text each [uptime] line carried, with the stamp's tags removed.</summary>
    private static string[] UptimeTexts(IEnumerable<string> lines, string phoneVersion)
    {
        var tags = Tags(phoneVersion);
        return lines.Where(l => l.StartsWith("[uptime] ", StringComparison.Ordinal))
            .Select(l =>
            {
                Assert.EndsWith(tags, l);
                return l["[uptime] ".Length..^tags.Length];
            })
            .ToArray();
    }

    [Fact]
    public void Format_gap_matches_relay()
    {
        foreach (var (seconds, expected) in RelayParityData.FormatGaps)
            Assert.Equal(expected, UptimeLog.FormatGap(seconds));
    }

    [Fact]
    public void Gap_verdict_matches_relay()
    {
        foreach (var (gap, down, prevJson, diagJson, expected) in RelayParityData.Verdicts)
        {
            var prev = PyValue.FromJson(JsonNode.Parse(prevJson));
            var diag = PyDict.FromJson(JsonNode.Parse(diagJson)!.AsObject());
            Assert.Equal(expected, UptimeLog.GapVerdict(gap, down, prev, diag));
        }
    }

    [Fact]
    public void A_push_without_diag_gets_no_verdict()
    {
        // relay.py fell back on whatever snapshot it last held, which described
        // an earlier push rather than this one.
        Assert.Equal("", UptimeLog.GapVerdict(2270, false, PyValue.FromJson(JsonNode.Parse("1000")), null));
    }

    [Fact]
    public void Checkin_lines_match_relay()
    {
        var clock = new ManualClock(Base);
        var diag = new FakeDiagnostics { PhoneVersion = "1.0 (80)" };

        foreach (var (startedOffset, previousOffset, nowOffset, prevJson, diagJson, expected) in RelayParityData.Checkins)
        {
            var path = _temp.File($"checkin-{Guid.NewGuid():N}.log");
            var log = new UptimeLog(path, diag, clock, Base + startedOffset);
            var now = Base + nowOffset;
            clock.Now = now;

            using var capture = Log.Capture();
            log.NoteCheckin(previousOffset is { } p ? Base + p : 0, now,
                PyValue.FromJson(JsonNode.Parse(prevJson)),
                PyDict.FromJson(JsonNode.Parse(diagJson)!.AsObject()));

            Assert.Equal(expected, UptimeTexts(capture.Lines, "1.0 (80)"));
            if (expected.Length == 0)
                Assert.False(File.Exists(path));
            else
                Assert.Equal(expected.Select(t => Stamp(now) + "  " + t + Tags("1.0 (80)")), File.ReadAllLines(path));
        }
    }

    [Fact]
    public void Silence_lines_match_relay()
    {
        var clock = new ManualClock(Base);
        var failing = RelayParityData.Summaries.Single(s => s.Name == "failing");
        var diag = new FakeDiagnostics { PhoneVersion = "1.0 (80)", SummaryText = failing.Expected };
        var log = new UptimeLog(_temp.File("silence.log"), diag, clock, Base - 100_000);

        foreach (var (lastSeenOffset, nowOffset, expected) in RelayParityData.Silences)
        {
            var lastSeen = lastSeenOffset < 0 ? 0 : Base + lastSeenOffset;
            clock.Now = Base + nowOffset;

            using var capture = Log.Capture();
            log.NoteSilence(lastSeen);

            var when = When(lastSeen);
            Assert.Equal(expected.Select(e => e.Replace("{when}", when)), UptimeTexts(capture.Lines, "1.0 (80)"));
        }
    }

    [Fact]
    public void Silence_line_reads_the_real_diagnostics()
    {
        var clock = new ManualClock(Base);
        var diag = new PhoneDiagnostics();
        using (Log.Capture())
        {
            diag.RecordVersion(JsonNode.Parse("\"1.0 (80)\""));
            diag.RecordDiag(JsonNode.Parse(RelayParityData.Summaries.Single(s => s.Name == "failing").Json), Base);
        }
        var log = new UptimeLog(_temp.File("silence.log"), diag, clock, Base - 100_000);

        clock.Now = Base + 3000;
        using var capture = Log.Capture();
        log.NoteSilence(Base);

        var line = Assert.Single(UptimeTexts(capture.Lines, "1.0 (80)"));
        Assert.Equal(RelayParityData.Silences[2].Lines[0].Replace("{when}", When(Base)), line);
    }

    [Fact]
    public void Lines_are_stamped_with_issun_and_the_phone_build_and_end_in_crlf()
    {
        var clock = new ManualClock(Base);
        var diag = new FakeDiagnostics { PhoneVersion = "1.0 (80)" };
        var path = Path.Combine(_temp.Path, "nested", "folder", "ammy-uptime.log");
        var log = new UptimeLog(path, diag, clock, Base);

        using var capture = Log.Capture();
        log.Line("issun started");
        diag.PhoneVersion = "1.0 (81)";
        log.Line("second");

        Assert.Equal(new[]
        {
            $"[uptime] issun started  [issun {IssunInfo.Display} / app 1.0 (80)]",
            $"[uptime] second  [issun {IssunInfo.Display} / app 1.0 (81)]",
        }, capture.Lines);

        var expected = $"{Stamp(Base)}  issun started  [issun {IssunInfo.Display} / app 1.0 (80)]\r\n"
                     + $"{Stamp(Base)}  second  [issun {IssunInfo.Display} / app 1.0 (81)]\r\n";
        Assert.Equal(expected, File.ReadAllText(path));
        // No BOM: relay.py wrote plain UTF-8, and an imported history continues in place.
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void A_line_that_cannot_be_written_says_so()
    {
        // A directory where the file should be: AppendAllText throws.
        var log = new UptimeLog(_temp.Path, new FakeDiagnostics(), new ManualClock(Base), Base);

        using var capture = Log.Capture();
        log.Line("gap 00:01:30  phone silent, relay up throughout");

        Assert.Equal(2, capture.Lines.Count);
        Assert.StartsWith("[uptime] gap 00:01:30", capture.Lines[0]);
        Assert.StartsWith($"[uptime] could not write {_temp.Path} (", capture.Lines[1]);
    }

    public static TheoryData<string> SummaryNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in RelayParityData.UptimeSummaries)
            data.Add(s.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SummaryNames))]
    public void Summary_matches_relay_reading_the_trailing_stamp(string name)
    {
        var (_, content, expected, _) = RelayParityData.UptimeSummaries.Single(s => s.Name == name);
        var path = _temp.File($"{name}.log");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));

        var log = new UptimeLog(path, new FakeDiagnostics(), new ManualClock(), 0);
        Assert.Equal(expected, log.Summarize());
    }

    [Fact]
    public void Summary_files_verdict_lines_under_their_build_where_relay_did_not()
    {
        // relay.py's regex took the first "app ...]" on the line, and from 1.8.0
        // the verdict itself starts with "app": this is what it printed.
        var (_, content, expected, original) = RelayParityData.UptimeSummaries.Single(s => s.Name == "mixed_history");
        Assert.Contains("  restarted 16048s -> 0s \x2014 it died  [relay 1.8.0 / app 1.0 (38)\n", original);
        Assert.DoesNotContain("restarted", expected);
        Assert.Contains("\n  1.0 (38)\n    count    : 1\n", expected);
    }

    [Fact]
    public void Summary_without_a_log()
    {
        var log = new UptimeLog(_temp.File("missing.log"), new FakeDiagnostics(), new ManualClock(), 0);
        Assert.Equal(RelayParityData.SummaryWithoutLog, log.Summarize());
    }

    [Fact]
    public void Summary_reads_back_what_line_wrote()
    {
        var clock = new ManualClock(Base);
        var diag = new FakeDiagnostics { PhoneVersion = "1.0 (80)" };
        var path = _temp.File("roundtrip.log");
        var log = new UptimeLog(path, diag, clock, Base);

        using (Log.Capture())
        {
            log.Line("issun started");
            clock.Advance(4000);
            log.NoteSilence(Base + 100);
            log.NoteCheckin(Base + 100, clock.Now, PyValue.FromJson(JsonNode.Parse("100")),
                PyDict.FromJson(JsonNode.Parse("{\"app_uptime_s\": 4000}")!.AsObject()));
        }

        Assert.Equal(
            "relay starts            : 1\n" +
            "silences detected live  : 1\n" +
            "gaps including downtime : 0\n" +
            "\n" +
            "phone-only gaps, by app build\n" +
            "  1.0 (80)\n" +
            "    count    : 1\n" +
            "    longest  : 01:05:00\n" +
            "    median   : 01:05:00\n" +
            "    total    : 01:05:00",
            log.Summarize());
    }
}
