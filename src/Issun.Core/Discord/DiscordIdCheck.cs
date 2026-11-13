namespace Issun.Core.Discord;

/// <summary>What Discord said about an application ID: accepted, or why not, in the words the window's status line uses.</summary>
public sealed record DiscordIdCheckResult(bool Accepted, string? Problem);

/// <summary>
/// Asks Discord once whether it accepts an application ID: a connection of its
/// own, the handshake, and Discord's answer (READY, or a refusal such as 4000),
/// then it closes again. The presence connection is left alone. Setup's Next
/// on the Discord page runs it, once per click (the owner, 13 Nov 2026).
/// </summary>
public static class DiscordIdCheck
{
    public static async Task<DiscordIdCheckResult> RunAsync(Func<IDiscordClient> clientFactory, string clientId, CancellationToken ct)
    {
        var client = clientFactory();
        try
        {
            await client.ConnectAsync(clientId, ct).ConfigureAwait(false);
            Log.Write($"[rpc] checked an application ID ({clientId.Length} digits): Discord accepted it");
            return new DiscordIdCheckResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"[rpc] checked an application ID ({clientId.Length} digits): {ex.GetType().Name}: {ex.Message}");
            return new DiscordIdCheckResult(false, PresenceWorker.DetailFor(ex));
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
