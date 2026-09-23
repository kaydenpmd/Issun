using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Issun.Core.Discord;

namespace Issun.Core.Tests.Discord;

public class DiscordIpcClientTests
{
    private const string ClientId = "123456789012345678";

    private static readonly DiscordActivity Track = new()
    {
        Details = "Not Like Us",
        State = "Kendrick Lamar",
        Type = 2,
        StatusDisplayType = 1,
        Start = 1750000000,
        End = 1750000274,
        LargeImage = "https://is1-ssl.mzstatic.com/image/thumb/x/600x600bb.jpg",
    };

    private static DiscordIpcClient Client(FakeDiscordServer server, TimeSpan? responseTimeout = null) =>
        new(server.Prefix, TimeSpan.FromSeconds(2), responseTimeout ?? TimeSpan.FromSeconds(5));

    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Handshakes_with_the_first_pipe_that_exists_and_reads_the_user()
    {
        JsonObject? hello = null;
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            hello = await FakeDiscordServer.AcceptAsync(c, ct);
            await c.ReadAsync(ct); // CLOSE from DisposeAsync
        }, index: 3);

        var client = Client(server);
        var user = await client.ConnectAsync(ClientId, Timeout);

        Assert.True(client.IsConnected);
        Assert.Equal(new DiscordUser("100000000000000001", "example", "Example Person"), user);
        Assert.Equal("""{"v":1,"client_id":"123456789012345678"}""", hello!.ToJsonString());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Sends_set_activity_as_pypresence_did_and_returns_on_its_nonce()
    {
        var received = new List<FakeFrame>();
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            for (var i = 0; i < 2; i++)
            {
                var request = await c.ReadAsync(ct);
                received.Add(request);
                await c.WriteAsync(1, FakeDiscordServer.Ack(request.Json!), ct);
            }
            received.Add(await c.ReadAsync(ct)); // CLOSE
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);
        await client.SetActivityAsync(Track, Timeout);
        await client.SetActivityAsync(null, Timeout);
        await client.DisposeAsync();
        await server.Completion;

        var set = received[0];
        Assert.Equal(1, set.Op);
        var expected = DiscordWire.SetActivity(Track, Environment.ProcessId, set.Json!["nonce"]!.GetValue<string>());
        Assert.Equal(expected.ToJsonString(), set.Json!.ToJsonString());

        var clear = received[1];
        Assert.Equal(1, clear.Op);
        Assert.Null(clear.Json!["args"]!["activity"]);
        Assert.False(clear.Json!["args"]!.AsObject().ContainsKey("activity"));
        Assert.NotEqual(set.Json!["nonce"]!.GetValue<string>(), clear.Json!["nonce"]!.GetValue<string>());

        // pypresence's close(): opcode 2 with the handshake body.
        Assert.Equal(2, received[2].Op);
        Assert.Equal("""{"v":1,"client_id":"123456789012345678"}""", received[2].Json!.ToJsonString());
    }

    [Fact]
    public async Task An_error_reply_is_a_rejection_and_the_connection_survives_it()
    {
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            var refused = await c.ReadAsync(ct);
            await c.WriteAsync(1, FakeDiscordServer.Error(refused.Json!), ct);
            var next = await c.ReadAsync(ct);
            await c.WriteAsync(1, FakeDiscordServer.Ack(next.Json!), ct);
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);

        var ex = await Assert.ThrowsAsync<DiscordRejectedException>(
            () => client.SetActivityAsync(Track with { Details = "i" }, Timeout));
        Assert.Equal(4000, ex.Code);
        Assert.Equal(FakeDiscordServer.ShortDetailsMessage, ex.Message);
        Assert.True(client.IsConnected);

        await client.SetActivityAsync(Track, Timeout);
        await server.Completion;
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Close_4000_on_handshake_is_an_invalid_client_id_worded_as_pypresence_worded_it()
    {
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await c.ReadAsync(ct);
            await c.WriteAsync(2, """{"code":4000,"message":"Invalid Client ID"}""", ct);
            c.Drop();
        });

        var client = Client(server);
        var ex = await Assert.ThrowsAsync<DiscordHandshakeException>(() => client.ConnectAsync("1234567890", Timeout));

        Assert.Equal(4000, ex.Code);
        // relay.log: "[rpc] Discord not reachable (Error Code: 4000 Message: Client ID is Invalid); retrying in 10s"
        Assert.Equal("Error Code: 4000 Message: Client ID is Invalid", ex.Message);
        Assert.False(client.IsConnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Other_handshake_closes_carry_their_code()
    {
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await c.ReadAsync(ct);
            await c.WriteAsync(2, """{"code":4004,"message":"Invalid version"}""", ct);
            c.Drop();
        });

        var ex = await Assert.ThrowsAsync<DiscordHandshakeException>(() => Client(server).ConnectAsync(ClientId, Timeout));
        Assert.Equal(4004, ex.Code);
        Assert.Equal("Error Code: 4004 Message: Invalid version", ex.Message);
    }

    [Fact]
    public async Task Discord_vanishing_mid_request_is_a_lost_connection()
    {
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            await c.ReadAsync(ct);
            c.Drop();
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);

        await Assert.ThrowsAsync<DiscordConnectionLostException>(() => client.SetActivityAsync(Track, Timeout));
        Assert.False(client.IsConnected);

        // And stays lost, rather than pretending the next request might work.
        await Assert.ThrowsAsync<DiscordConnectionLostException>(() => client.SetActivityAsync(Track, Timeout));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task A_close_frame_mid_session_is_a_lost_connection_not_a_rejection()
    {
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            await c.ReadAsync(ct);
            await c.WriteAsync(2, """{"code":1000,"message":"Closing"}""", ct);
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);

        var ex = await Assert.ThrowsAsync<DiscordConnectionLostException>(() => client.SetActivityAsync(Track, Timeout));
        Assert.Contains("1000", ex.Message);
        Assert.False(client.IsConnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Pings_are_answered_and_unrelated_frames_skipped_while_waiting()
    {
        FakeFrame? pong = null;
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            var request = await c.ReadAsync(ct);

            await c.WriteAsync(3, """{"ping":17}""", ct);
            pong = await c.ReadAsync(ct);

            // Someone else's reply, and an event nobody subscribed to.
            await c.WriteAsync(1, """{"cmd":"SET_ACTIVITY","data":{},"evt":"ERROR","nonce":"not-yours"}""", ct);
            await c.WriteAsync(1, """{"cmd":"DISPATCH","data":{},"evt":"ACTIVITY_JOIN","nonce":null}""", ct);
            await c.WriteAsync(1, FakeDiscordServer.Ack(request.Json!), ct);
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);
        await client.SetActivityAsync(Track, Timeout);
        await server.Completion;

        Assert.Equal(4, pong!.Op);
        Assert.Equal("""{"ping":17}""", pong.Json!.ToJsonString());
        Assert.True(client.IsConnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task A_silent_discord_times_out_as_a_lost_connection()
    {
        var release = new TaskCompletionSource();
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            await c.ReadAsync(ct);
            await release.Task.WaitAsync(ct); // never answers
        });

        var client = Client(server, responseTimeout: TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync(ClientId, Timeout);

        var ex = await Assert.ThrowsAsync<DiscordConnectionLostException>(() => client.SetActivityAsync(Track, Timeout));
        Assert.Equal(DiscordIpcClient.NoResponseMessage, ex.Message);
        Assert.False(client.IsConnected);
        release.SetResult();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task A_handshake_nobody_answers_times_out_instead_of_hanging()
    {
        // pypresence waited on the handshake reply with no timeout at all.
        var release = new TaskCompletionSource();
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await c.ReadAsync(ct);
            await release.Task.WaitAsync(ct);
        });

        var client = Client(server, responseTimeout: TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<DiscordUnavailableException>(() => client.ConnectAsync(ClientId, Timeout));
        Assert.Equal("Discord opened the pipe but didn't finish the handshake: No response was received from the pipe in time", ex.Message);
        Assert.False(client.IsConnected);
        release.SetResult();
    }

    [Fact]
    public async Task A_pipe_that_closes_before_ready_is_unavailable_not_a_crash()
    {
        // pypresence's InvalidPipe: "this sometimes happens for some reason,
        // perhaps discord cannot always accept all the connections?"
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await c.ReadAsync(ct);
            c.Drop();
        });

        var client = Client(server);
        var ex = await Assert.ThrowsAsync<DiscordUnavailableException>(() => client.ConnectAsync(ClientId, Timeout));
        Assert.Contains("The pipe was closed.", ex.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task A_ping_during_the_handshake_is_answered()
    {
        FakeFrame? pong = null;
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await c.ReadAsync(ct);
            await c.WriteAsync(3, """{"ping":1}""", ct);
            pong = await c.ReadAsync(ct);
            await c.WriteAsync(1, FakeDiscordServer.Ready(), ct);
        });

        var client = Client(server);
        var user = await client.ConnectAsync(ClientId, Timeout);
        await server.Completion;

        Assert.Equal("Example Person", user!.GlobalName);
        Assert.Equal(4, pong!.Op);
        Assert.Equal("""{"ping":1}""", pong.Json!.ToJsonString());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task A_busy_pipe_is_passed_over_for_the_next_one()
    {
        // Every instance of prefix0 taken, as when Discord has no instance free:
        // .NET waits the connect timeout on it, then the client moves on.
        var prefix = FakeDiscordServer.NewPrefix();
        await using var busy = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = busy.WaitForConnectionAsync();
        await using var squatter = new NamedPipeClientStream(".", prefix + "0", PipeDirection.InOut, PipeOptions.Asynchronous);
        await squatter.ConnectAsync(5000);
        await waiting;

        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            await c.ReadAsync(ct); // CLOSE
        }, index: 1, prefix: prefix);

        var client = new DiscordIpcClient(prefix, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
        var user = await client.ConnectAsync(ClientId, Timeout);

        Assert.True(client.IsConnected);
        Assert.Equal("example", user!.Username);
        await client.DisposeAsync();
        await server.Completion;
    }

    [Fact]
    public async Task Discord_quitting_while_nothing_is_being_sent_is_noticed()
    {
        // relay.py only found out on its next push; for a paused phone that
        // could be hours of believing it was connected.
        var quit = new TaskCompletionSource();
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            await quit.Task.WaitAsync(ct);
            c.Drop();
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);

        // A healthy idle pipe must keep reading as connected — a false "closed"
        // here would reconnect a working connection every tick.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(client.IsConnected);
            await Task.Delay(20);
        }

        quit.SetResult();
        await server.Completion;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.IsConnected && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<DiscordConnectionLostException>(() => client.SetActivityAsync(Track, Timeout));

        // Nobody is left to send a CLOSE to, so disposing doesn't try.
        using var capture = Log.Capture();
        await client.DisposeAsync();
        Assert.Empty(capture.Lines);
    }

    [Fact]
    public async Task No_pipe_at_all_is_unavailable_and_fails_fast()
    {
        var client = new DiscordIpcClient($"issun-test-{Guid.NewGuid():N}-", TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<DiscordUnavailableException>(() => client.ConnectAsync(ClientId, Timeout));

        // The same words relay.log carries 25 times over.
        Assert.Equal("Could not find Discord installed and running on this machine.", ex.Message);
        // Absent pipes are skipped rather than each waited on for the connect timeout.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_is_passed_through_not_dressed_up_as_a_lost_connection()
    {
        var release = new TaskCompletionSource();
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await FakeDiscordServer.AcceptAsync(c, ct);
            await c.ReadAsync(ct);
            await release.Task.WaitAsync(ct);
        });

        var client = Client(server);
        await client.ConnectAsync(ClientId, Timeout);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetActivityAsync(Track, cts.Token));
        release.SetResult();
        await client.DisposeAsync();
    }
}
