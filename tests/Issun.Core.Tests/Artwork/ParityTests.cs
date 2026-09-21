using System.Globalization;
using System.Text.Json;
using Issun.Core.Artwork;

namespace Issun.Core.Tests.Artwork;

/// <summary>
/// The pure functions of the artwork chain against values relay.py itself
/// computed (see Parity/generate_parity.py). Each test runs every case and
/// reports every mismatch at once, so one failure can't hide the rest.
/// </summary>
public class ParityTests
{
    [Fact]
    public void Album_url_drops_only_i_exactly_as_python_round_trips_the_query()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var c in Py.Cases(ParityData.AlbumUrl))
        {
            count++;
            var input = Py.Str(c.GetProperty("in"));
            try
            {
                var actual = PyUrl.AlbumUrl(input);
                if (c.TryGetProperty("error", out _))
                    failures.Add($"{Py.Show(input)}: expected ValueError, got {Py.Show(actual)}");
                else if (actual != Py.Str(c.GetProperty("out")))
                    failures.Add($"{Py.Show(input)}: expected {Py.Show(Py.Str(c.GetProperty("out")))}, got {Py.Show(actual)}");
            }
            catch (FormatException ex)
            {
                if (!c.TryGetProperty("error", out _))
                    failures.Add($"{Py.Show(input)}: unexpected {ex.Message}");
                else if (ex.Message != c.GetProperty("message").GetString())
                    failures.Add($"{Py.Show(input)}: message {Py.Show(ex.Message)}, Python said {Py.Show(c.GetProperty("message").GetString()!)}");
            }
        }
        Assert.True(count > 300);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("https://music.apple.com/us/album/i/1444846337?i=1444846349&uo=4", "https://music.apple.com/us/album/i/1444846337?uo=4")]
    [InlineData("https://music.apple.com/us/album/i/1444846337?i=1444846349", "https://music.apple.com/us/album/i/1444846337")]
    [InlineData("", "")]
    public void Album_url_on_real_apple_music_links(string input, string expected)
    {
        Assert.Equal(expected, PyUrl.AlbumUrl(input));
    }

    [Fact]
    public void Query_strings_are_urlencoded_with_quote_plus()
    {
        var failures = new List<string>();
        foreach (var c in Py.Cases(ParityData.Query))
        {
            var term = Py.Str(c.GetProperty("term"));
            var search = PyUrl.UrlEncode([("term", term), ("entity", "song"), ("limit", "12")]);
            var lookup = PyUrl.UrlEncode([("id", term), ("entity", "song")]);
            if (search != c.GetProperty("search").GetString())
                failures.Add($"search {Py.Show(term)}: {search} != {c.GetProperty("search").GetString()}");
            if (lookup != c.GetProperty("lookup").GetString())
                failures.Add($"lookup {Py.Show(term)}: {lookup} != {c.GetProperty("lookup").GetString()}");
        }
        Assert.Empty(failures);
        Assert.Equal("term=Kendrick+Lamar+i+i+-+Single&entity=song&limit=12",
            PyUrl.UrlEncode([("term", "Kendrick Lamar i i - Single"), ("entity", "song"), ("limit", "12")]));
    }

    [Fact]
    public void Normalize_matches_python_on_messy_titles()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var c in Py.Cases(ParityData.Normalize))
        {
            count++;
            var input = Py.Str(c.GetProperty("in"));
            var expected = c.GetProperty("out").GetString();
            var actual = TitleMatch.Normalize(input);
            if (actual != expected)
                failures.Add($"{Py.Show(input)}: expected {Py.Show(expected!)}, got {Py.Show(actual)}");
        }
        Assert.True(count > 400);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("Black and Yellow (feat. Juicy J, Snoop Dogg & T-Pain) [G-Mix]", "black and yellow")]
    [InlineData("i - Single", "i")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Without Me", "without me")]
    [InlineData("Dance with Me", "dance")]
    [InlineData("宇多田ヒカル", "")]
    public void Normalize_examples(string input, string expected)
    {
        Assert.Equal(expected, TitleMatch.Normalize(input));
    }

    [Fact]
    public void Normalize_ignores_the_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // Culture-sensitive lowering would give "tıtle" and "ıstanbul" here.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("title", TitleMatch.Normalize("TITLE WITH I"));
            Assert.Equal("istanbul", TitleMatch.Normalize("ISTANBUL"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Sequence_matcher_ratio_and_blocks_match_difflib()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var c in Py.Cases(ParityData.Ratio))
        {
            count++;
            var a = Py.Str(c.GetProperty("a"));
            var b = Py.Str(c.GetProperty("b"));
            var matcher = new SequenceMatcher(a, b);
            var expected = c.GetProperty("ratio").GetDouble();
            var ratio = matcher.Ratio();
            if (Math.Abs(ratio - expected) > 1e-12)
                failures.Add($"ratio({Py.Show(a)}, {Py.Show(b)}) = {ratio:R}, difflib says {expected:R}");

            var blocks = matcher.GetMatchingBlocks().Select(m => $"{m.A},{m.B},{m.Size}");
            var expectedBlocks = c.GetProperty("blocks").EnumerateArray()
                .Select(m => string.Join(",", m.EnumerateArray().Select(x => x.GetInt32())));
            if (!blocks.SequenceEqual(expectedBlocks))
                failures.Add($"blocks({Py.Show(a)}, {Py.Show(b)}) = [{string.Join(" ", blocks)}], difflib says [{string.Join(" ", expectedBlocks)}]");
        }
        Assert.True(count > 300);
        Assert.Empty(failures);
    }

    [Fact]
    public void Autojunk_applies_once_b_reaches_200_elements()
    {
        // 'x' is 1% popular in a 200-long b, so difflib stops indexing it and
        // the only anchor left is the single 'y'. Below 200 nothing is junk.
        var b = "y" + new string('x', 199);
        var a = new string('x', 10);
        Assert.Equal(0.0, new SequenceMatcher(a, b).Ratio());
        Assert.Equal(2.0 * 10 / (10 + 199), new SequenceMatcher(a, b[1..]).Ratio(), 12);
    }

    [Fact]
    public void Score_matches_relay_py()
    {
        var failures = new List<string>();
        foreach (var c in Py.Cases(ParityData.Score))
        {
            var r = c.GetProperty("result").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var t = c.GetProperty("track").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var expected = c.GetProperty("score").GetDouble();
            var actual = TitleMatch.Score(r[0], r[1], r[2], t[0], t[1], t[2]);
            if (Math.Abs(actual - expected) > 1e-12)
                failures.Add($"score([{string.Join("|", r)}] vs [{string.Join("|", t)}]) = {actual:R}, relay.py says {expected:R}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void Base64_is_decoded_with_python_strict_mode()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var c in Py.Cases(ParityData.Base64))
        {
            count++;
            var input = c.GetProperty("in").GetString()!;
            var blob = PyBase64.DecodeStrict(input, out var error);
            if (c.TryGetProperty("hex", out var hex))
            {
                if (blob is null)
                    failures.Add($"{Py.Show(input)}: rejected ({error}), Python decoded it");
                else if (Convert.ToHexStringLower(blob) != hex.GetString())
                    failures.Add($"{Py.Show(input)}: decoded {Convert.ToHexStringLower(blob)}, Python {hex.GetString()}");
            }
            else if (blob is not null)
            {
                failures.Add($"{Py.Show(input)}: decoded, Python refused ({c.GetProperty("message").GetString()})");
            }
            else if (error != c.GetProperty("message").GetString())
            {
                failures.Add($"{Py.Show(input)}: error {Py.Show(error!)}, Python {Py.Show(c.GetProperty("message").GetString()!)}");
            }
        }
        Assert.True(count > 300);
        Assert.Empty(failures);
    }

    [Fact]
    public void Uploaded_art_filenames_match_relay_py_byte_for_byte()
    {
        foreach (var c in Py.Cases(ParityData.FileName))
        {
            var t = c.GetProperty("track");
            var track = new TrackInfo
            {
                Title = Py.OptStr(t, "title"),
                Artist = Py.OptStr(t, "artist"),
                Album = Py.OptStr(t, "album"),
            };
            Assert.Equal(c.GetProperty("key").GetString(), TrackText.Key(track));
            Assert.Equal(c.GetProperty("name").GetString(), UploadedArt.FileName(TrackText.Key(track)));
        }
    }

    [Fact]
    public void Confidence_prints_like_python_format_2f()
    {
        var failures = new List<string>();
        foreach (var c in Py.Cases(ParityData.Fixed2))
        {
            var value = c.GetProperty("value").GetDouble();
            var actual = PyFormat.Fixed2(value);
            if (actual != c.GetProperty("text").GetString())
                failures.Add($"{value:R}: {actual}, Python {c.GetProperty("text").GetString()}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void Confidence_formatting_ignores_the_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.48", PyFormat.Fixed2(0.4812));
            Assert.Equal("0.12", PyFormat.Fixed2(0.125));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
