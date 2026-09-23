using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Issun.Core.Tests.Discord;

/// <summary>
/// Plays Discord's side of the local IPC pipe, one scripted connection at a
/// time. Every instance invents its own pipe prefix, so tests never touch
/// \\.\pipe\discord-ipc-N — the real Discord's pipes — and can run in parallel.
/// </summary>
internal sealed class FakeDiscordServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    /// <param name="script">What Discord does once the client connects.</param>
    /// <param name="index">Which of prefix0..prefix9 to listen on.</param>
    /// <param name="prefix">Share another test pipe's prefix; by default a fresh one.</param>
    public FakeDiscordServer(Func<FakeConnection, CancellationToken, Task> script, int index = 0, string? prefix = null)
    {
        Prefix = prefix ?? NewPrefix();
        // Created here, synchronously, so the pipe exists before the client looks for it.
        _pipe = new NamedPipeServerStream(Prefix + index, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Completion = Task.Run(async () =>
        {
            await _pipe.WaitForConnectionAsync(_cts.Token);
            await script(new FakeConnection(_pipe), _cts.Token);
        });
    }

    public string Prefix { get; }

    /// <summary>A pipe prefix no other test — and no real Discord — is using.</summary>
    public static string NewPrefix() => $"issun-test-{Guid.NewGuid():N}-";

    /// <summary>The script's outcome; awaiting it surfaces a failed assertion inside the script.</summary>
    public Task Completion { get; }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Completion; } catch { /* the test has its own assertions */ }
        await _pipe.DisposeAsync();
        _cts.Dispose();
    }

    public const string ReadyUser = """{"id":"100000000000000001","username":"example","discriminator":"0","global_name":"Example Person","avatar":null,"bot":false,"flags":0,"premium_type":0}""";

    /// <summary>The READY dispatch Discord answers a good handshake with.</summary>
    public static string Ready(string user = ReadyUser) =>
        $$$"""{"cmd":"DISPATCH","data":{"v":1,"config":{"cdn_host":"cdn.discordapp.com","api_endpoint":"//discord.com/api","environment":"production"},"user":{{{user}}}},"evt":"READY","nonce":null}""";

    /// <summary>Handshake and READY, the part every scenario starts with. Returns the handshake body.</summary>
    public static async Task<JsonObject> AcceptAsync(FakeConnection c, CancellationToken ct)
    {
        var hello = await c.ReadAsync(ct);
        Assert.Equal(0, hello.Op);
        await c.WriteAsync(1, Ready(), ct);
        return hello.Json!;
    }

    /// <summary>The reply to a SET_ACTIVITY Discord accepted: the activity echoed back, same nonce.</summary>
    public static string Ack(JsonObject request) =>
        new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["data"] = request["args"]?["activity"]?.DeepClone(),
            ["evt"] = null,
            ["nonce"] = request["nonce"]?.DeepClone(),
        }.ToJsonString();

    /// <summary>
    /// The refusal Discord sent for "i" by Kendrick Lamar before padding existed.
    /// pypresence stripped the brackets and capitalised it; relay.log recorded
    /// what was left: Child "activity" fails because child "details" fails ...
    /// </summary>
    public const string ShortDetailsMessage =
        """child "activity" fails because [child "details" fails because ["details" length must be at least 2 characters long]]""";

    public static string Error(JsonObject request, int code = 4000, string message = ShortDetailsMessage) =>
        new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["data"] = new JsonObject { ["code"] = code, ["message"] = message },
            ["evt"] = "ERROR",
            ["nonce"] = request["nonce"]?.DeepClone(),
        }.ToJsonString();
}

internal sealed record FakeFrame(int Op, string Raw, JsonObject? Json);

internal sealed class FakeConnection(Stream pipe)
{
    public async Task<FakeFrame> ReadAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        var raw = Encoding.UTF8.GetString(body);
        return new FakeFrame(op, raw, JsonNode.Parse(raw) as JsonObject);
    }

    public async Task WriteAsync(int op, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, ct);
        await pipe.FlushAsync(ct);
    }

    /// <summary>Discord vanishing mid-conversation: the pipe just closes.</summary>
    public void Drop() => pipe.Dispose();
}
