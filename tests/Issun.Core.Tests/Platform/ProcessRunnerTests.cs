using Issun.Core.Platform;

namespace Issun.Core.Tests.Platform;

/// <summary>
/// Real child processes, all cmd.exe builtins or ping to 127.0.0.1 — nothing
/// that touches the network or Tailscale — and all started without a window.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly string Cmd =
        Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";

    private static Task<ProcessResult> RunCmd(string script, TimeSpan timeout, CancellationToken ct = default) =>
        ProcessRunner.Instance.RunAsync(Cmd, ["/d", "/c", script], timeout, ct);

    [Fact]
    public async Task Output_error_and_exit_code_are_all_captured()
    {
        var result = await RunCmd("echo to-stdout& echo to-stderr 1>&2& exit /b 3", TimeSpan.FromSeconds(10));

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("to-stdout", result.StdOut.Trim());
        Assert.Equal("to-stderr", result.StdErr.Trim());
    }

    [Fact]
    public async Task A_clean_exit_succeeds()
    {
        var result = await RunCmd("echo ok", TimeSpan.FromSeconds(10));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_missing_program_is_reported_not_thrown()
    {
        var result = await ProcessRunner.Instance.RunAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tailscale.exe"), [], TimeSpan.FromSeconds(5), default);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StartError));
    }

    [Fact]
    public async Task A_timeout_kills_the_process_and_keeps_what_it_printed_first()
    {
        // The shape of `tailscale funnel` waiting on its enable link: print, then wait.
        var started = DateTime.UtcNow;
        var result = await RunCmd("echo https://login.example/f/funnel& ping -n 30 127.0.0.1 >nul", TimeSpan.FromSeconds(1));

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("https://login.example/f/funnel", result.StdOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the wait was not cut short");
    }

    [Fact]
    public async Task Input_is_closed_so_a_prompt_cannot_wait_forever()
    {
        // `set /p` reads a line from stdin; with stdin closed it returns at once.
        var result = await RunCmd("set /p answer=question? & echo done", TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.Contains("done", result.StdOut);
    }

    [Fact]
    public async Task Cancellation_throws_rather_than_reporting_a_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunCmd("ping -n 30 127.0.0.1 >nul", TimeSpan.FromSeconds(20), cts.Token));
    }
}
