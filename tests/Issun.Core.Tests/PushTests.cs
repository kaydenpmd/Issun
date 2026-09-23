using System.Text.Json.Nodes;
using Issun.Core;
using Issun.Core.Tests.Server;

namespace Issun.Core.Tests;

public class PushTests
{
    private static NowPlayingPush Parse(string json) => NowPlayingPush.Parse(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void A_playing_push_carries_its_track()
    {
        var push = Parse("""
            {"playing": true, "app_version": "1.0 (80)", "seq": 1758400000123,
             "title": "i", "artist": "Kendrick Lamar", "album": "i - Single",
             "duration": 231.5, "elapsed": 12, "store_id": " 1440894925 "}
            """);

        Assert.True(push.Playing);
        Assert.Equal("1.0 (80)", push.AppVersion);
        Assert.Equal(1758400000123, push.Seq);
        Assert.NotNull(push.Track);
        Assert.Equal("i", push.Track!.Title);
        Assert.Equal(231.5, push.Track.Duration);
        Assert.Equal(12, push.Track.Elapsed);
        Assert.Equal("1440894925", TrackText.StoreId(push.Track));
        Assert.Equal("Kendrick Lamar|i|i - Single", TrackText.Key(push.Track));
    }

    [Fact]
    public void A_farewell_has_no_track()
    {
        var push = Parse("""{"playing": false, "seq": 5, "title": "ignored"}""");
        Assert.False(push.Playing);
        Assert.Null(push.Track);
        Assert.Equal(5, push.Seq);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("12.5")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    public void Seq_must_be_a_json_integer(string seq)
    {
        Assert.Null(Parse($$"""{"playing": false, "seq": {{seq}}}""").Seq);
    }

    [Fact]
    public void Missing_fields_take_relay_defaults()
    {
        var track = new TrackInfo();
        Assert.Equal("Unknown Artist|Unknown Track|", TrackText.Key(track));
        Assert.Null(TrackText.StoreId(track with { StoreId = "0" }));
        Assert.Null(TrackText.StoreId(track with { StoreId = "-1" }));
    }

    [Fact]
    public void Clip_counts_code_points_like_python()
    {
        var emoji = string.Concat(Enumerable.Repeat("🎵", 130));
        var clipped = TrackText.Clip(emoji);
        Assert.Equal(128, clipped.EnumerateRunes().Count());
        Assert.Equal(256, clipped.Length);
    }

    // ── Explicit ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("\"true\"", null)]   // a string is not an answer, and "false" would be truthy
    [InlineData("1", null)]
    public void Explicit_is_a_json_bool_or_no_answer(string value, bool? expected)
    {
        var push = Parse($$"""{"playing": true, "title": "T", "explicit": {{value}}}""");
        Assert.Equal(expected, push.Track!.Explicit);
    }

    [Fact]
    public void A_push_that_says_nothing_about_explicitness_leaves_it_unknown()
    {
        Assert.Null(Parse("""{"playing": true, "title": "T"}""").Track!.Explicit);
    }

    [Theory]
    [InlineData(true, false, true)]      // the source's word beats the catalog's
    [InlineData(false, true, false)]     // ...in both directions
    [InlineData(null, true, true)]       // silence falls back to the exact lookup
    [InlineData(null, false, false)]
    [InlineData(null, null, false)]      // no lookup yet, or it failed: not marked
    public void Explicit_takes_the_sources_word_then_the_lookups(bool? pushed, bool? catalog, bool expected)
    {
        var artwork = new FakeArtwork();
        if (catalog is { } answer)
            artwork.Explicit["42"] = answer;

        Assert.Equal(expected, TrackText.Explicit(new TrackInfo { Title = "T", StoreId = "42", Explicit = pushed }, artwork));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    public void With_no_usable_store_id_only_the_source_can_say(string? storeId)
    {
        var artwork = new FakeArtwork();
        artwork.Explicit["0"] = true;
        artwork.Explicit["-1"] = true;

        Assert.False(TrackText.Explicit(new TrackInfo { StoreId = storeId }, artwork));
        Assert.True(TrackText.Explicit(new TrackInfo { StoreId = storeId, Explicit = true }, artwork));
    }
}
