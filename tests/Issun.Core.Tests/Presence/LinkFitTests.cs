using Issun.Core.Presence;

namespace Issun.Core.Tests.Presence;

public class LinkFitTests
{
    private static string LongName(int n) => new string('a', n);

    [Fact]
    public void A_link_that_fits_is_left_alone()
    {
        const string url = "https://music.apple.com/us/album/eat-you-up/6778183551?i=6778183557&uo=4";
        Assert.Equal(url, LinkFit.For(url, 256));
    }

    [Fact]
    public void A_long_apple_music_link_loses_its_name_not_its_id()
    {
        var url = $"https://music.apple.com/us/album/{LongName(300)}/6778183551?i=6778183557&uo=4";

        Assert.Equal("https://music.apple.com/us/album/6778183551?i=6778183557&uo=4", LinkFit.For(url, 256));
    }

    [Fact]
    public void An_artist_link_is_shortened_the_same_way()
    {
        var url = $"https://music.apple.com/us/artist/{LongName(300)}/1211257849?uo=4";

        Assert.Equal("https://music.apple.com/us/artist/1211257849?uo=4", LinkFit.For(url, 256));
    }

    [Theory]
    [InlineData("https://example.com/")]                   // not Apple Music
    [InlineData("https://music.apple.com/us/album/")]      // no id to keep
    [InlineData("https://music.apple.com/us/album/name/notanid")]
    public void A_long_link_with_no_known_short_form_is_dropped_not_clipped(string prefix)
    {
        var url = prefix + LongName(300);
        Assert.Null(LinkFit.For(url, 256));
    }

    [Fact]
    public void The_builder_never_sends_a_clipped_link()
    {
        var builder = new ActivityBuilder(new MutableConfig());
        var links = new CatalogLinks(
            Song: $"https://music.apple.com/us/album/{LongName(300)}/1?i=2",
            Artist: "https://example.com/" + LongName(300),
            Album: $"https://music.apple.com/us/album/{LongName(300)}/1");
        var art = new ArtworkResult("https://is1-ssl.mzstatic.com/x/512x512bb.jpg", "", ArtworkSource.StoreId);

        var activity = builder.Build(new TrackInfo { Title = "Song", Artist = "Artist" }, 1_750_000_000, art, links);

        Assert.Equal("https://music.apple.com/us/album/1?i=2", activity.DetailsUrl);
        Assert.Null(activity.StateUrl);
        Assert.Equal("https://music.apple.com/us/album/1", activity.LargeUrl);
    }
}
