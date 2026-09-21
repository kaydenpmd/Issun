using System.Reflection;
using Issun.Core;
using Issun.Core.Presence;

namespace Issun.Core.Tests.Presence;

/// <summary>
/// What the parity data cannot show: behaviour over time, settings that change
/// between builds, concurrency, and the places Issun deliberately parts from
/// relay.py.
/// </summary>
public class ActivityBuilderTests
{
    // Well in the past, so any build that quietly consulted the real clock
    // would land nowhere near the expected anchor.
    private const double T0 = 1_750_000_000.25;

    private static readonly ArtworkResult Cover =
        new("https://is1-ssl.mzstatic.com/image/thumb/cover/512x512bb.jpg", "Catalog Album", ArtworkSource.StoreId);

    private static TrackInfo Track(string title = "Song", double? duration = 200, double? elapsed = 12, string album = "Album") => new()
    {
        Title = title,
        Artist = "Artist",
        Album = album,
        Duration = duration,
        Elapsed = elapsed,
    };

    [Fact]
    public void A_frozen_reading_holds_start_while_the_wall_clock_runs()
    {
        // Relay 1.2.1's fix, and the project's most expensive bug. The worker
        // builds once a second; the phone refreshes elapsed once every thirty.
        // For thirty builds the reading and its arrival time are frozen while
        // the clock moves on, and Start must not move with it. The builder is
        // given no clock at all — that is the fix: the only time it ever sees
        // is observedAt — so the clock here only drives the broken half below.
        var clock = new ManualClock(T0);
        var builder = new ActivityBuilder(new MutableConfig());
        var track = Track(elapsed: 12);
        var observedAt = T0;

        using var capture = Log.Capture();
        var starts = new List<long?>();
        var ends = new List<long?>();
        for (var tick = 0; tick < 30; tick++)
        {
            var built = builder.Build(track, observedAt, Cover, null);
            starts.Add(built.Start);
            ends.Add(built.End);
            clock.Advance(1);
        }

        Assert.All(starts, s => Assert.Equal((long)(T0 - 12), s));
        Assert.All(ends, e => Assert.Equal((long)(T0 - 12 + 200), e));
        Assert.DoesNotContain(capture.Lines, l => l.StartsWith("[playhead]", StringComparison.Ordinal));

        // The same loop fed the clock instead: what relay 1.2.0 did. The anchor
        // slides, and the tell in relay.log was the same re-anchor line over and
        // over — a noisy sensor gives varying numbers; a constant drift is a clock.
        var broken = new ActivityBuilder(new MutableConfig());
        using var brokenCapture = Log.Capture();
        var brokenStarts = new HashSet<long?>();
        for (var tick = 0; tick < 30; tick++)
        {
            brokenStarts.Add(broken.Build(track, clock.Now, Cover, null).Start);
            clock.Advance(1);
        }

        Assert.True(brokenStarts.Count > 1);
        var reanchors = brokenCapture.Lines.Where(l => l.StartsWith("[playhead] re-anchored +", StringComparison.Ordinal)).ToList();
        Assert.True(reanchors.Count >= 2, string.Join(" | ", brokenCapture.Lines));
        Assert.Single(reanchors.Distinct());
    }

    [Fact]
    public void Status_line_is_read_on_every_build()
    {
        var config = new MutableConfig();
        var builder = new ActivityBuilder(config);

        Assert.Equal(1, builder.Build(Track(), T0, Cover, null).StatusDisplayType);

        config.Settings = config.Settings with { StatusLine = "details" };
        Assert.Equal(2, builder.Build(Track(), T0, Cover, null).StatusDisplayType);

        config.Settings = config.Settings with { StatusLine = "name" };
        Assert.Equal(0, builder.Build(Track(), T0, Cover, null).StatusDisplayType);

        config.Settings = config.Settings with { StatusLine = "album" };
        Assert.Null(builder.Build(Track(), T0, Cover, null).StatusDisplayType);
    }

    [Theory]
    [InlineData("name", 0)]
    [InlineData("state", 1)]
    [InlineData("details", 2)]
    [InlineData("  NAME ", 0)]
    [InlineData("\u00A0Details\u3000", 2)]        // Python's strip() takes both
    [InlineData("\u001Cstate", 1)]                // ...and this, which char.IsWhiteSpace does not
    [InlineData("DETA\u0130LS", null)]            // .NET would lower-case İ to i; Python does not
    [InlineData("\uFF4E\uFF41\uFF4D\uFF45", null)] // fullwidth "name"
    [InlineData("", null)]
    [InlineData(null, 1)]                         // relay.py's default
    public void Status_line_normalises_like_relay_py(string? statusLine, int? expected)
    {
        Assert.Equal(expected, ActivityBuilder.StatusDisplayFor(statusLine));
    }

    [Fact]
    public void Show_album_is_read_on_every_build()
    {
        var config = new MutableConfig();
        var builder = new ActivityBuilder(config);

        Assert.Null(builder.Build(Track(), T0, Cover, null).LargeText);

        config.Settings = config.Settings with { ShowAlbum = true };
        Assert.Equal("Catalog Album", builder.Build(Track(), T0, Cover, null).LargeText);

        // No matched album: the phone's album instead.
        Assert.Equal("Album", builder.Build(Track(), T0, Cover with { MatchedAlbum = "" }, null).LargeText);

        // No cover, no tooltip — there is nothing to hover.
        Assert.Null(builder.Build(Track(), T0, ArtworkResult.None, null).LargeText);
    }

    [Fact]
    public void Reset_forgets_the_anchor()
    {
        var builder = new ActivityBuilder(new MutableConfig());
        Assert.Equal((long)T0, builder.Build(Track(elapsed: 0), T0, Cover, null).Start);

        // Past the settle window, a reading that makes the song look 5 s earlier
        // is staleness by the playhead's rule, and is ignored.
        Assert.Equal((long)T0, builder.Build(Track(elapsed: 25), T0 + 30, Cover, null).Start);

        // After a stop, the same reading is a fresh anchor, taken without comment.
        builder.Reset();
        using var capture = Log.Capture();
        Assert.Equal((long)(T0 + 5), builder.Build(Track(elapsed: 25), T0 + 30, Cover, null).Start);
        Assert.Empty(capture.Lines);
    }

    [Theory]
    [InlineData(100.0, double.NaN)]
    [InlineData(100.0, double.PositiveInfinity)]
    [InlineData(100.0, double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity, 10.0)]
    [InlineData(1e300, 0.0)]   // finite, but the end does not fit in a long
    [InlineData(100.0, -1e300)]
    public void An_unusable_position_costs_the_progress_bar_not_the_worker(double duration, double elapsed)
    {
        // relay.py raised here — OverflowError or ValueError from int() — outside
        // the worker's try, which ended the RPC thread for good.
        var builder = new ActivityBuilder(new MutableConfig());
        var track = Track(title: "Broken clock", duration: duration, elapsed: elapsed);

        using var capture = Log.Capture();
        var first = builder.Build(track, T0, Cover, null);
        var second = builder.Build(track, T0 + 1, Cover, null);

        Assert.Equal("Broken clock", first.Details);
        Assert.Null(first.Start);
        Assert.Null(first.End);
        Assert.Null(second.Start);
        var line = Assert.Single(capture.Lines);
        Assert.StartsWith("[playhead] unusable position for Broken clock (duration=", line);
        Assert.EndsWith(" — sending it without a progress bar", line);

        // The next track is unaffected.
        Assert.Equal((long)(T0 - 12), builder.Build(Track(title: "Fine"), T0, Cover, null).Start);
    }

    [Fact]
    public void A_nan_duration_is_no_duration_as_it_was_to_relay_py()
    {
        // float("nan") > 0 is False in Python too: no bar, and nothing to report.
        var builder = new ActivityBuilder(new MutableConfig());
        using var capture = Log.Capture();
        var built = builder.Build(Track(duration: double.NaN), T0, Cover, null);
        Assert.Null(built.Start);
        Assert.Empty(capture.Lines);
    }

    [Fact]
    public void Album_text_is_kept_within_what_discord_accepts()
    {
        var builder = new ActivityBuilder(new MutableConfig(new Settings { ShowAlbum = true }));

        using var capture = Log.Capture();
        var single = builder.Build(Track(), T0, Cover with { MatchedAlbum = "4" }, null);
        Assert.Equal("4\u2060", single.LargeText);
        Assert.Equal(["[rpc] padded album '4' — Discord requires 2+ characters"], capture.Lines);

        var longName = string.Concat(Enumerable.Repeat("Symphony No. 9 ", 20));
        var clipped = builder.Build(Track(), T0, Cover with { MatchedAlbum = longName }, null);
        Assert.Equal(longName[..128], clipped.LargeText);
    }

    [Fact]
    public void Links_come_through_as_given_and_the_cover_link_needs_a_cover()
    {
        var builder = new ActivityBuilder(new MutableConfig());
        var links = new CatalogLinks("https://music.apple.com/song", "https://music.apple.com/artist", "https://music.apple.com/album");

        var withCover = builder.Build(Track(), T0, Cover, links);
        Assert.Equal("https://music.apple.com/song", withCover.DetailsUrl);
        Assert.Equal("https://music.apple.com/artist", withCover.StateUrl);
        Assert.Equal("https://music.apple.com/album", withCover.LargeUrl);

        var bare = builder.Build(Track(), T0, ArtworkResult.None, links);
        Assert.Equal("https://music.apple.com/song", bare.DetailsUrl);
        Assert.Null(bare.LargeImage);
        Assert.Null(bare.LargeUrl);

        var none = builder.Build(Track(), T0, Cover, new CatalogLinks(null, "", null));
        Assert.Null(none.DetailsUrl);
        Assert.Null(none.StateUrl);
        Assert.Null(none.LargeUrl);
    }

    [Fact]
    public void Materially_different_compares_every_property()
    {
        // ActivityBuilder finds the fields to compare by reflection, so a
        // property added to DiscordActivity later is compared without anyone
        // remembering to list it. This walks the same list from the other side:
        // every property, changed alone, must count as a change.
        var baseline = new DiscordActivity
        {
            Details = "Song",
            State = "Artist",
            Type = 2,
            StatusDisplayType = 1,
            Start = 1_758_400_000,
            End = 1_758_400_200,
            LargeImage = "https://img/1.jpg",
            LargeText = "Album",
            LargeUrl = "https://a",
            DetailsUrl = "https://s",
            StateUrl = "https://t",
        };
        var builder = new ActivityBuilder(new MutableConfig());
        var clone = typeof(DiscordActivity).GetMethod("<Clone>$")!;

        foreach (var property in typeof(DiscordActivity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // A new property needs a value in the baseline above and, if it is
            // not a string, int or long, a decision about how it compares.
            object? Changed(object? value) => value switch
            {
                string s => s + "x",
                int i => i + 1,
                long l => l + 3,
                _ => throw new InvalidOperationException(
                    $"No test mutation for {property.Name} ({property.PropertyType}); decide how it compares."),
            };

            var altered = (DiscordActivity)clone.Invoke(baseline, null)!;
            property.SetValue(altered, Changed(property.GetValue(baseline)));
            Assert.True(builder.MateriallyDifferent(altered, baseline), property.Name);

            // Absent against present differs, in either direction. Details and
            // State are required and never absent.
            if (property.Name is nameof(DiscordActivity.Details) or nameof(DiscordActivity.State))
                continue;
            var cleared = (DiscordActivity)clone.Invoke(baseline, null)!;
            property.SetValue(cleared, null);
            Assert.True(builder.MateriallyDifferent(cleared, baseline), property.Name + " cleared");
            Assert.True(builder.MateriallyDifferent(baseline, cleared), property.Name + " set");
        }

        // Timestamps within 2 s are the same timestamp; 3 s is not.
        Assert.False(builder.MateriallyDifferent(baseline with { Start = baseline.Start - 2, End = baseline.End + 2 }, baseline));
        Assert.True(builder.MateriallyDifferent(baseline with { End = baseline.End - 3 }, baseline));
        Assert.False(builder.MateriallyDifferent(baseline with { }, baseline));
        Assert.False(builder.MateriallyDifferent(null, null));
        Assert.True(builder.MateriallyDifferent(baseline, null));
        Assert.True(builder.MateriallyDifferent(null, baseline));
    }

    [Fact]
    public void Concurrent_builds_log_each_line_once()
    {
        // Twenty one-character titles with no cover, built from eight threads at
        // once: each must be padded and reported exactly once, not once per
        // thread that got there first.
        var builder = new ActivityBuilder(new MutableConfig());
        var titles = Enumerable.Range(0, 20).Select(i => ((char)('a' + i)).ToString()).ToArray();

        using var capture = Log.Capture();
        Parallel.For(0, 800, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var title = titles[i % titles.Length];
            var built = builder.Build(Track(title: title, elapsed: i % 7), T0 + i, ArtworkResult.None, null);
            Assert.Equal(title + "\u2060", built.Details);
            Assert.NotNull(built.Start);
        });

        var lines = capture.Lines;
        Assert.Equal(20, lines.Count(l => l.StartsWith("[rpc] padded title ", StringComparison.Ordinal)));
        Assert.Equal(20, lines.Count(l => l.StartsWith("[art] UNRESOLVED ", StringComparison.Ordinal)));
        Assert.Equal(20, lines.Where(l => l.StartsWith("[art] UNRESOLVED ", StringComparison.Ordinal)).Distinct().Count());
    }

    [Fact]
    public void The_playhead_refuses_a_reading_with_no_finite_anchor()
    {
        // relay.py would have stored NaN, and every later comparison with NaN is
        // false, so no reading could ever have moved it again.
        var playhead = new Playhead();
        Assert.Equal(T0 - 3, playhead.Anchor("k", 3, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => playhead.Anchor("k", double.NaN, T0 + 1));
        Assert.Equal(T0 - 3, playhead.Start);
    }
}
