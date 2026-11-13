using Issun.Core.Discord;
using Xunit;

namespace Issun.Core.Tests.Discord;

public sealed class DiscordIdCheckTests
{
    private static readonly CancellationToken Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private static Func<IDiscordClient> Client(string prefix) =>
        () => new DiscordIpcClient(prefix, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Ready_means_accepted()
    {
        string? sentId = null;
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            var hello = await FakeDiscordServer.AcceptAsync(c, ct);
            sentId = hello["client_id"]?.GetValue<string>();
            await c.ReadAsync(ct); // CLOSE: the check doesn't stay connected
        });

        var result = await DiscordIdCheck.RunAsync(Client(server.Prefix), "123456789012345678", Timeout);

        Assert.True(result.Accepted);
        Assert.Null(result.Problem);
        Assert.Equal("123456789012345678", sentId);
    }

    [Fact]
    public async Task A_4000_close_is_the_invalid_id_problem()
    {
        await using var server = new FakeDiscordServer(async (c, ct) =>
        {
            await c.ReadAsync(ct);
            await c.WriteAsync(2, """{"code":4000,"message":"Invalid Client ID"}""", ct);
            c.Drop();
        });

        var result = await DiscordIdCheck.RunAsync(Client(server.Prefix), "123456789012345678", Timeout);

        Assert.False(result.Accepted);
        Assert.Equal(PresenceWorker.InvalidIdDetail, result.Problem);
    }

    [Fact]
    public async Task No_Discord_is_the_not_running_problem()
    {
        var result = await DiscordIdCheck.RunAsync(Client(FakeDiscordServer.NewPrefix()), "123456789012345678", Timeout);

        Assert.False(result.Accepted);
        Assert.Equal(PresenceWorker.NotRunningDetail, result.Problem);
    }
}
