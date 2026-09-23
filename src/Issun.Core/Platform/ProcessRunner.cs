using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Issun.Core.Platform;

/// <param name="Started">False when the program could not be launched at all; see <see cref="StartError"/>.</param>
/// <param name="ExitCode">Null when it never finished — not started, or killed at the timeout.</param>
/// <param name="StdOut">Everything written before it exited or was killed.</param>
public sealed record ProcessResult(
    bool Started, int? ExitCode, string StdOut, string StdErr, bool TimedOut, string? StartError = null)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    public static ProcessResult NotStarted(string error) => new(false, null, "", "", false, error);
}

/// <summary>Runs a console program to completion. The seam tests use to stand in for tailscale.exe.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Never throws for the program's own failures — not found, non-zero exit,
    /// timeout — only <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    /// <summary>How long to wait for the pipes to drain once the process has gone.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(fileName)
        {
            // A console program started from a windowed app gets a console
            // window of its own unless told otherwise, and Issun runs these
            // from the tray, unprompted, on the owner's desktop.
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Closed straight away below, so anything that stops to ask a
            // question reads end-of-input and carries on or fails, instead of
            // waiting for an answer until the timeout.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                return ProcessResult.NotStarted("the process did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return ProcessResult.NotStarted(ex.Message);
        }

        try { process.StandardInput.Close(); }
        catch (IOException) { /* already gone; nothing was going to be read anyway */ }

        // Read by hand into buffers rather than ReadToEndAsync, so a process
        // killed at the timeout still hands back what it printed before then.
        // tailscale funnel prints the link that enables Funnel for the tailnet
        // and then waits for someone to open it — that link is the whole answer.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var pumps = Task.WhenAll(
            Pump(process.StandardOutput, stdout),
            Pump(process.StandardError, stderr));

        var timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                Kill(process);
            }
        }

        // Anything that inherited the pipes can keep them open after the
        // process itself is gone; don't let that hang the caller.
        await Task.WhenAny(pumps, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        int? exitCode = null;
        if (!timedOut)
        {
            try { exitCode = process.ExitCode; }
            catch (InvalidOperationException) { exitCode = null; }
        }
        return new ProcessResult(true, exitCode, Snapshot(stdout), Snapshot(stderr), timedOut);
    }

    private static async Task Pump(StreamReader reader, StringBuilder into)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                lock (into)
                    into.Append(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe went away with the process. What arrived is kept.
        }
    }

    private static string Snapshot(StringBuilder sb)
    {
        lock (sb)
            return sb.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Exited on its own between the timeout and the kill.
        }
    }
}
