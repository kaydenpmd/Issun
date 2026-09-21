using System.Text.Json.Nodes;
using Issun.Core.State;

namespace Issun.Core.Tests.State;

public class CheckinProcessorTests : IDisposable
{
    private const double Base = 1_750_000_000.0;
    private const long BaseMs = 1_750_000_000_000;

    private readonly TempFolder _temp = new();
    private readonly ManualClock _clock = new(Base);
    private readonly PhoneState _state = new();
    private readonly PhoneDiagnostics _diag = new();
    private readonly UptimeLog _uptime;
    private readonly CheckinProcessor _checkins;

    public CheckinProcessorTests()
    {
        // Issun has been up far longer than any gap here, so gaps are the phone's.
        _uptime = new UptimeLog(_temp.File("ammy-uptime.log"), _diag, _clock, Base - 1_000_000);
        _checkins = new CheckinProcessor(_state, _diag, _uptime, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static NowPlayingPush Push(bool playing, long? seq = null, long? uptime = null, string? extraDiag = null,
        string? title = "Song", bool diag = true)
    {
        var body = new JsonObject
        {
            ["playing"] = playing,
            ["app_version"] = "1.0 (80)",
        };
        if (seq is not null)
            body["seq"] = seq;
        if (playing)
        {
            body["title"] = title;
            body["artist"] = "Artist";
            body["elapsed"] = 12;
        }
        if (diag)
        {
            var d = JsonNode.Parse("{" + (extraDiag ?? "") + "}")!.AsObject();
            if (uptime is not null)
                d["app_uptime_s"] = uptime;
            body["diag"] = d;
        }
        return NowPlayingPush.Parse(JsonNode.Parse(body.ToJsonString())!.AsObject());
    }

    private string[] UptimeLines() =>
        File.Exists(_uptime.FilePath) ? File.ReadAllLines(_uptime.FilePath) : [];

    private string[] GapLines() => UptimeLines().Where(l => l.Contains("  gap ", StringComparison.Ordinal)).ToArray();

    // ───────────────────────── the 14 Sept 2026 bug ─────────────────────────

    [Fact]
    public void Two_pushes_ending_one_gap_log_it_once_and_say_the_app_stayed_up()
    {
        using var capture = Log.Capture();

        _checkins.Accept(Push(true, BaseMs, 1120));
        _clock.Advance(2270);   // 00:37:50, the length of the real gap

        // The push that ends the silence, then Ammy's correction push right
        // behind it, a second later by the phone's uptime.
        _checkins.Accept(Push(true, BaseMs + 2_270_000, 3390));
        _clock.Advance(0.05);
        _checkins.Accept(Push(true, BaseMs + 2_270_050, 3391));

        var gap = Assert.Single(GapLines());
        Assert.Contains("gap 00:37:50  phone silent, relay up throughout, app stayed up 1120s -> 3390s \x2014 the path failed, not the app", gap);
        Assert.DoesNotContain("it died", string.Join("\n", UptimeLines()));
    }

    [Fact]
    public void Two_pushes_racing_to_end_one_gap_log_it_once()
    {
        // The same scenario with the two pushes genuinely concurrent, as they
        // were on Kestrel's threads. relay.py let both read the pre-gap check-in.
        for (var round = 0; round < 25; round++)
        {
            using var temp = new TempFolder();
            var clock = new ManualClock(Base);
            var state = new PhoneState();
            var diag = new PhoneDiagnostics();
            var uptime = new UptimeLog(temp.File("u.log"), diag, clock, Base - 1_000_000);
            var checkins = new CheckinProcessor(state, diag, uptime, clock);

            using var capture = Log.Capture();
            checkins.Accept(Push(true, BaseMs, 1120));
            clock.Advance(2270);

            var pushes = new[] { Push(true, BaseMs + 2_270_000, 3390), Push(true, BaseMs + 2_270_001, 3390) };
            using var start = new Barrier(pushes.Length);
            var threads = pushes.Select(p => new Thread(() =>
            {
                start.SignalAndWait();
                checkins.Accept(p);
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            var lines = File.ReadAllLines(uptime.FilePath).Where(l => l.Contains("  gap ", StringComparison.Ordinal)).ToArray();
            var gap = Assert.Single(lines);
            Assert.Contains("app stayed up 1120s -> 3390s", gap);
        }
    }

    // ───────────────────────────── seq, from relay.py ─────────────────────────────

    [Fact]
    public void An_older_seq_is_dropped_but_still_counts_as_a_check_in()
    {
        using var capture = Log.Capture();

        Assert.True(_checkins.Accept(Push(false, BaseMs + 500, 100)).Applied);   // the farewell lands first
        var (track, updatedAt) = _state.Get();
        Assert.Null(track);
        Assert.Equal(Base, updatedAt);

        _clock.Advance(0.2);
        var late = _checkins.Accept(Push(true, BaseMs + 300, 100));              // the in-flight push, late

        Assert.False(late.Applied);
        Assert.Equal($"dropped out-of-order push (seq={BaseMs + 300}, last={BaseMs + 500})", late.DropReason);
        Assert.Contains($"[state] dropped out-of-order push (seq={BaseMs + 300}, last={BaseMs + 500})", capture.Lines);
        Assert.Null(_state.Get().Track);
        Assert.Equal(Base, _state.Get().UpdatedAt);
        Assert.Equal(Base + 0.2, _state.LastCheckinAt);
        Assert.Equal(BaseMs + 500, _state.LastSeq);
    }

    [Fact]
    public void An_equal_seq_is_dropped()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100));
        Assert.False(_checkins.Accept(Push(false, BaseMs, 100)).Applied);
        Assert.NotNull(_state.Get().Track);
    }

    [Fact]
    public void A_push_without_seq_always_applies_and_leaves_the_high_water_mark()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100));
        _clock.Advance(1);

        Assert.True(_checkins.Accept(Push(false, seq: null, uptime: 100)).Applied);
        Assert.Null(_state.Get().Track);
        Assert.Equal(Base + 1, _state.Get().UpdatedAt);
        Assert.Equal(BaseMs, _state.LastSeq);
    }

    [Fact]
    public void Newer_seqs_apply_and_playing_false_is_stamped_too()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100, title: "i"));
        Assert.Equal("i", _state.Get().Track!.Title);
        Assert.Equal(12, _state.Get().Track!.Elapsed);

        _clock.Advance(30);
        Assert.True(_checkins.Accept(Push(false, BaseMs + 30_000, 130)).Applied);
        var (track, updatedAt) = _state.Get();
        Assert.Null(track);
        Assert.Equal(Base + 30, updatedAt);
        Assert.Equal(Base + 30, _state.LastCheckinAt);
    }

    [Fact]
    public void Nothing_has_checked_in_before_the_first_push()
    {
        Assert.Equal(0, _state.LastCheckinAt);
        var (track, updatedAt) = _state.Get();
        Assert.Null(track);
        Assert.Equal(0, updatedAt);
        Assert.Null(_state.LastSeq);
    }

    // ─────────────────────── seq, the two relay.py hazards ───────────────────────

    [Fact]
    public void A_clock_set_back_more_than_a_minute_is_accepted_and_resets_the_mark()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100));

        // relay.py dropped this and every push after it until the clock caught up.
        _clock.Advance(30);
        var back = BaseMs - 3_600_000 + 30_000;
        var result = _checkins.Accept(Push(false, back, 130));

        Assert.True(result.Applied);
        Assert.Null(_state.Get().Track);
        Assert.Equal(back, _state.LastSeq);
        Assert.Contains(capture.Lines, l => l.StartsWith($"[state] accepted push despite older seq (seq={back}, last={BaseMs}): 3570s back", StringComparison.Ordinal)
                                            && l.Contains("clock moved backwards", StringComparison.Ordinal));

        _clock.Advance(30);
        Assert.True(_checkins.Accept(Push(true, back + 30_000, 160)).Applied);
    }

    [Fact]
    public void A_late_push_under_a_minute_old_is_still_dropped()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100));
        Assert.False(_checkins.Accept(Push(false, BaseMs - 59_000, 41)).Applied);
    }

    [Fact]
    public void A_new_app_process_supersedes_the_old_ones_seq()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 5000));

        // Relaunched, with the phone clock a little behind where it was: seq
        // back 10 s, uptime back 4997 s. Only a different process does that.
        _clock.Advance(20);
        var result = _checkins.Accept(Push(false, BaseMs - 10_000, 3));

        Assert.True(result.Applied);
        Assert.Null(_state.Get().Track);
        Assert.Equal(BaseMs - 10_000, _state.LastSeq);
        Assert.Contains($"[state] accepted push despite older seq (seq={BaseMs - 10_000}, last={BaseMs}): app uptime went 5000s -> 3s, so a new app process sent it and supersedes the old one", capture.Lines);
    }

    [Fact]
    public void A_late_push_whose_uptime_ticked_over_a_second_is_still_late()
    {
        // The case item 6 exists for, straddling a second boundary: uptime went
        // backwards by one, which a naive "uptime went backwards" rule would
        // take for a relaunch — and apply the stale playing:true push.
        using var capture = Log.Capture();
        _checkins.Accept(Push(false, BaseMs + 1_200, 1001));
        Assert.False(_checkins.Accept(Push(true, BaseMs + 400, 1000)).Applied);
        Assert.Null(_state.Get().Track);
    }

    [Fact]
    public void A_dead_process_s_late_push_does_not_supersede_its_successor()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 2));                       // the new process
        Assert.False(_checkins.Accept(Push(false, BaseMs - 500, 5000)).Applied);  // the old one, arriving late
        Assert.NotNull(_state.Get().Track);
    }

    // ─────────────────────────── diagnostics and gaps ───────────────────────────

    [Fact]
    public void The_version_and_diag_are_recorded_even_for_a_dropped_push()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100, "\"route\": \"Speaker\""));
        _checkins.Accept(Push(true, BaseMs - 1, 100, "\"route\": \"CarAudio\""));

        Assert.Equal("1.0 (80)", _diag.PhoneVersion);
        Assert.Equal("CarAudio", _diag.Latest!["route"]!.GetValue<string>());
        Assert.Equal(1, capture.Lines.Count(l => l == "[init] phone reports Ammy 1.0 (80)"));
        Assert.Contains("[keepalive] route 'Speaker' -> 'CarAudio'", capture.Lines);
    }

    [Fact]
    public void A_short_silence_logs_no_gap()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 100));
        _clock.Advance(89.9);
        _checkins.Accept(Push(true, BaseMs + 89_900, 189));
        Assert.Empty(GapLines());
    }

    [Fact]
    public void A_relaunch_during_the_gap_is_a_death()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 16048));
        _clock.Advance(415);
        _checkins.Accept(Push(true, BaseMs + 415_000, 0));

        var gap = Assert.Single(GapLines());
        Assert.Contains("gap 00:06:55  phone silent, relay up throughout, app restarted 16048s -> 0s \x2014 it died", gap);
        Assert.Contains("[keepalive] app restarted: uptime 16048s -> 0s, counters reset", capture.Lines);
    }

    [Fact]
    public void Failed_pushes_are_reported_on_the_gap_line()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 20695));
        _clock.Advance(157);
        _checkins.Accept(Push(true, BaseMs + 157_000, 20852, "\"push_fails\": 4, \"offline_s\": 156"));

        var gap = Assert.Single(GapLines());
        Assert.EndsWith(
            $"gap 00:02:37  phone silent, relay up throughout, app stayed up 20695s -> 20852s \x2014 the path failed, not the app, 4 pushes failed over 00:02:36  [{IssunInfo.Wire} / app 1.0 (80)]",
            gap);
    }

    [Fact]
    public void A_gap_ended_by_a_push_without_diag_has_no_verdict()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 1000));
        _clock.Advance(2270);
        _checkins.Accept(Push(true, BaseMs + 2_270_000, diag: false));

        var gap = Assert.Single(GapLines());
        Assert.EndsWith($"gap 00:37:50  phone silent, relay up throughout  [{IssunInfo.Wire} / app 1.0 (80)]", gap);
    }

    [Fact]
    public void A_dropped_push_still_ends_a_silence()
    {
        using var capture = Log.Capture();
        _checkins.Accept(Push(true, BaseMs, 1000));
        _clock.Advance(300);
        Assert.False(_checkins.Accept(Push(true, BaseMs, 1300)).Applied);

        Assert.Single(GapLines());
        Assert.Equal(Base + 300, _state.LastCheckinAt);
        Assert.Equal(Base, _state.Get().UpdatedAt);

        // And the worker's silence check measures check-ins, so it stays quiet.
        _clock.Advance(60);
        _uptime.NoteSilence(_state.LastCheckinAt);
        Assert.DoesNotContain(UptimeLines(), l => l.Contains("stopped checking in", StringComparison.Ordinal));
    }
}
