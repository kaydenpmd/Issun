using System.Text.Json.Nodes;
using Issun.Core;
using Issun.Core.Presence;

namespace Issun.Core.Tests.Presence;

/// <summary>
/// Everything in <see cref="ParityData"/>: the same inputs relay.py was given,
/// the same outputs and the same log lines, character for character.
/// </summary>
public class ParityTests
{
    private static readonly JsonObject Data = JsonNode.Parse(ParityData.Json)!.AsObject();

    public static TheoryData<string> ScenarioNames()
    {
        var names = new TheoryData<string>();
        foreach (var scenario in Data["scenarios"]!.AsArray())
            names.Add(scenario!["name"]!.GetValue<string>());
        return names;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Build_matches_relay_py(string name)
    {
        var scenario = Data["scenarios"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!;
        var config = new MutableConfig(new Settings
        {
            StatusLine = scenario["status_line"]!.GetValue<string>(),
            ShowAlbum = scenario["show_album"]!.GetValue<bool>(),
        });
        var builder = new ActivityBuilder(config);

        var index = 0;
        foreach (var step in scenario["steps"]!.AsArray())
        {
            index++;
            if (step!["op"]!.GetValue<string>() == "reset")
            {
                builder.Reset();
                continue;
            }

            var push = NowPlayingPush.Parse(step["body"]!.DeepClone().AsObject());
            Assert.NotNull(push.Track);

            using var capture = Log.Capture();
            var built = builder.Build(push.Track!, step["observed_at"]!.GetValue<double>(), Art(step["art"]), Links(step["links"]));

            var expected = Activity(step["expected"]!.AsObject());
            Assert.True(expected == built, $"step {index}: expected {expected}, built {built}");
            Assert.Equal(Strings(step["logs"]), capture.Lines);
        }
    }

    [Fact]
    public void Playhead_matches_relay_py()
    {
        var playhead = new Playhead();
        foreach (var step in Data["playhead_sequence"]!.AsArray())
        {
            if (step!["op"]!.GetValue<string>() == "reset")
            {
                playhead.Reset();
                Assert.Null(playhead.Start);
                continue;
            }

            using var capture = Log.Capture();
            var start = playhead.Anchor(
                step["key"]!.GetValue<string>(), step["elapsed"]!.GetValue<double>(), step["now"]!.GetValue<double>());

            // Exact: same IEEE arithmetic in the same order gives the same double.
            Assert.Equal(step["start"]!.GetValue<double>(), start);
            Assert.Equal(Strings(step["logs"]), capture.Lines);
        }
    }

    [Fact]
    public void Padding_matches_relay_py()
    {
        // One DiscordText for the whole sequence, as relay.py had one _padded_seen.
        var text = new DiscordText();
        foreach (var step in Data["pad_sequence"]!.AsArray())
        {
            using var capture = Log.Capture();
            var result = text.Pad(step!["text"]!.GetValue<string>(), step["label"]!.GetValue<string>());
            Assert.Equal(step["result"]!.GetValue<string>(), result);
            Assert.Equal(Strings(step["logs"]), capture.Lines);
        }
    }

    [Fact]
    public void Materially_different_matches_relay_py()
    {
        var builder = new ActivityBuilder(new MutableConfig());
        foreach (var pair in Data["materially_different"]!.AsArray())
        {
            var next = pair!["new"] is JsonObject n ? Activity(n) : null;
            var previous = pair["old"] is JsonObject o ? Activity(o) : null;
            Assert.True(
                pair["different"]!.GetValue<bool>() == builder.MateriallyDifferent(next, previous),
                $"{next} vs {previous}");
        }
    }

    [Fact]
    public void Repr_matches_python()
    {
        foreach (var item in Data["repr"]!.AsArray())
            Assert.Equal(item!["repr"]!.GetValue<string>(), PyText.Repr(item["text"]!.GetValue<string>()));
    }

    [Fact]
    public void Fixed_point_formatting_matches_python()
    {
        foreach (var item in Data["fixed"]!.AsArray())
        {
            var value = item!["value"]!.GetValue<double>();
            Assert.Equal(item["plus"]!.GetValue<string>(), PyText.Fixed(value, 1, plusSign: true));
            Assert.Equal(item["plain"]!.GetValue<string>(), PyText.Fixed(value, 1));
            Assert.Equal(item["zero"]!.GetValue<string>(), PyText.Fixed(value, 0));
        }
    }

    [Fact]
    public void Whitespace_is_pythons_whitespace()
    {
        var python = Data["python_spaces"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
            Assert.True(python.Contains(cp) == PyText.IsSpace(cp), $"U+{cp:X4}");
    }

    internal static DiscordActivity Activity(JsonObject o) => new()
    {
        Details = o["Details"]!.GetValue<string>(),
        State = o["State"]!.GetValue<string>(),
        Type = (int?)Long(o, "Type"),
        StatusDisplayType = (int?)Long(o, "StatusDisplayType"),
        Start = Long(o, "Start"),
        End = Long(o, "End"),
        LargeImage = Str(o, "LargeImage"),
        LargeText = Str(o, "LargeText"),
        LargeUrl = Str(o, "LargeUrl"),
        DetailsUrl = Str(o, "DetailsUrl"),
        StateUrl = Str(o, "StateUrl"),
    };

    private static ArtworkResult Art(JsonNode? node) => node is JsonObject a
        ? new ArtworkResult(
            a["url"]!.GetValue<string>(),
            a["matched"]!.GetValue<string>(),
            a["via"]!.GetValue<string>() switch
            {
                "store" => ArtworkSource.StoreId,
                "search" => ArtworkSource.Search,
                _ => ArtworkSource.Uploaded,
            })
        : ArtworkResult.None;

    private static CatalogLinks? Links(JsonNode? node) => node is JsonObject l
        ? new CatalogLinks(Str(l, "song"), Str(l, "artist"), Str(l, "album"))
        : null;

    private static string[] Strings(JsonNode? node) =>
        node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();

    private static long? Long(JsonObject o, string key) => o[key]?.GetValue<long>();
}
