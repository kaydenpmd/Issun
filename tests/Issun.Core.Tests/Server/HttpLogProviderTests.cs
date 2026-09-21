using Issun.Core;
using Issun.Core.Server;
using Microsoft.Extensions.Logging;

namespace Issun.Core.Tests.Server;

/// <summary>
/// Issun is a WinExe with no console, so anything ASP.NET or Kestrel reports
/// has to land in Issun's own log or it lands nowhere.
/// </summary>
public class HttpLogProviderTests
{
    [Fact]
    public void Warnings_and_errors_become_http_lines_and_chatter_is_dropped()
    {
        using var provider = new HttpLogProvider();
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");

        using var capture = Log.Capture();
        logger.LogDebug("Connection id \"0HN\" started.");
        logger.LogInformation("Connection id \"0HN\" stopped.");
        logger.LogWarning("Heartbeat took longer than \"00:00:01\".");
        logger.LogWarning(new IOException("pipe closed"), "Connection processing ended abnormally");
        logger.LogError(new InvalidOperationException("boom"), "Unhandled exception while processing");

        Assert.Equal(
        [
            "[http] Heartbeat took longer than \"00:00:01\".",
            "[http] Connection processing ended abnormally: IOException: pipe closed",
            "[http] Unhandled exception while processing: System.InvalidOperationException: boom",
        ], capture.Lines);
    }

    [Fact]
    public void None_is_never_enabled()
    {
        using var provider = new HttpLogProvider();
        var logger = provider.CreateLogger("any");
        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }
}
