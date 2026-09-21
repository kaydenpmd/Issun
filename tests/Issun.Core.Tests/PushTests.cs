using System.Text.Json.Nodes;
using Issun.Core;

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
}
