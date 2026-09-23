using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Issun.Core.Discord;

namespace Issun.Core.Tests.Discord;

/// <summary>
/// The expected bodies below were produced by pypresence 4.6.2 itself: its real
/// Presence.update(), clear() and close() driven into a writer that captured
/// the frames, with relay.py's keyword arguments, pid 4242 and the nonce
/// blanked, then pasted in verbatim. Both sides are re-serialised before
/// comparing, which checks key order and nesting as well as values while
/// ignoring json.dumps' spacing and escaping style.
/// </summary>
public class DiscordWireTests
{
    private const string Nonce = "<nonce>";

    private static readonly DiscordActivity Full = new()
    {
        Details = "Not Like Us",
        State = "Kendrick Lamar",
        Type = 2,
        StatusDisplayType = 1,
        Start = 1750000000,
        End = 1750000274,
        LargeImage = "https://is1-ssl.mzstatic.com/image/thumb/x/600x600bb.jpg",
        LargeText = "GNX",
        LargeUrl = "https://music.apple.com/us/album/gnx/1781270319",
        DetailsUrl = "https://music.apple.com/us/song/1781270323",
        StateUrl = "https://music.apple.com/us/artist/368183298",
    };

    public static TheoryData<string, DiscordActivity?, string> Cases => new()
    {
        {
            "full", Full,
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 2, "status_display_type": 1, "state": "Kendrick Lamar", "state_url": "https://music.apple.com/us/artist/368183298", "details": "Not Like Us", "details_url": "https://music.apple.com/us/song/1781270323", "timestamps": {"start": 1750000000, "end": 1750000274}, "assets": {"large_image": "https://is1-ssl.mzstatic.com/image/thumb/x/600x600bb.jpg", "large_text": "GNX", "large_url": "https://music.apple.com/us/album/gnx/1781270319"}, "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            // "i" by Kendrick Lamar, padded with U+2060 by the builder. With no
            // type or display type given, pypresence still sends both as 0.
            "minimal", new DiscordActivity { Details = "i⁠", State = "Kendrick Lamar" },
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 0, "status_display_type": 0, "state": "Kendrick Lamar", "details": "i⁠", "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            // What relay.py actually sent for that track: LISTENING always, and
            // here STATUS_LINE=name.
            "relay_short_title", new DiscordActivity { Details = "i⁠", State = "Kendrick Lamar", Type = 2, StatusDisplayType = 0 },
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 2, "status_display_type": 0, "state": "Kendrick Lamar", "details": "i⁠", "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            // Outside ASCII and outside the BMP. json.dumps wrote the emoji as a
            // surrogate pair and left <&> alone; the values must still agree.
            "escaped", new DiscordActivity { Details = "Ø\U0001F525 <&>", State = "cd" },
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 0, "status_display_type": 0, "state": "cd", "details": "Ø🔥 <&>", "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            "name_display", new DiscordActivity { Details = "ab", State = "cd", Type = 2, StatusDisplayType = 0 },
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 2, "status_display_type": 0, "state": "cd", "details": "ab", "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            "start_only", new DiscordActivity { Details = "ab", State = "cd", Start = 1750000000 },
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 0, "status_display_type": 0, "state": "cd", "details": "ab", "timestamps": {"start": 1750000000}, "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            // A cover link with no cover: the assets object carries just the URL.
            "no_art_urls", new DiscordActivity { Details = "ab", State = "cd", DetailsUrl = "https://x/s", LargeUrl = "https://x/a" },
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242, "activity": {"type": 0, "status_display_type": 0, "state": "cd", "details": "ab", "details_url": "https://x/s", "assets": {"large_url": "https://x/a"}, "instance": true}}, "nonce": "<nonce>"}"""
        },
        {
            // clear(): "activity": None, then remove_none() deletes the key.
            "clear", null,
            """{"cmd": "SET_ACTIVITY", "args": {"pid": 4242}, "nonce": "<nonce>"}"""
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Set_activity_matches_pypresence(string name, DiscordActivity? activity, string pypresence)
    {
        _ = name;
        var ours = DiscordWire.SetActivity(activity, 4242, Nonce);
        Assert.Equal(JsonNode.Parse(pypresence)!.ToJsonString(), ours.ToJsonString());
    }

    [Fact]
    public void Handshake_and_close_match_pypresence()
    {
        // handshake() sends this with opcode 0, close() with opcode 2.
        Assert.Equal(JsonNode.Parse("""{"v": 1, "client_id": "123"}""")!.ToJsonString(), DiscordWire.Hello("123").ToJsonString());
    }

    [Fact]
    public void Frames_are_opcode_length_then_ascii_json()
    {
        var body = DiscordWire.SetActivity(new DiscordActivity { Details = "i⁠", State = "Ø" }, 4242, Nonce);
        var frame = DiscordWire.Encode(DiscordWire.OpFrame, body);

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(frame.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)));

        // json.dumps' ensure_ascii: the padding and the Ø travel escaped, so
        // the length pypresence wrote (characters) was also the byte count.
        var json = frame.AsSpan(8);
        Assert.True(json.ToArray().All(b => b < 0x80));
        var text = Encoding.ASCII.GetString(json);
        Assert.Contains("\\u2060", text);
        Assert.Contains("\\u00D8", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(body.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
    }

    [Fact]
    public void A_lone_surrogate_is_replaced_rather_than_failing_the_push()
    {
        var frame = DiscordWire.Encode(DiscordWire.OpFrame,
            DiscordWire.SetActivity(new DiscordActivity { Details = "a\ud800b", State = "cd" }, 4242, Nonce));
        var sent = JsonNode.Parse(frame.AsSpan(8))!;
        Assert.Equal("a�b", sent["args"]!["activity"]!["details"]!.GetValue<string>());
    }
}
