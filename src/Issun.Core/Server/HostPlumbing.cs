using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IssunLog = Issun.Core.Log;

namespace Issun.Core.Server;

/// <summary>
/// Kestrel's own warnings and errors, into Issun's log as "[http] …" lines.
///
/// Issun is a WinExe: there is no console, so ASP.NET's default console logger
/// would write into nothing — the same trap as relay.py under pythonw.exe,
/// where sys.stdout was None until _ensure_output() pointed it at relay.log.
/// Information and below are dropped; Kestrel narrates every connection there.
/// </summary>
internal sealed class HttpLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new HttpLogger();

    public void Dispose() { }

    private sealed class HttpLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = formatter(state, exception);
            // A warning's exception is usually the whole story in one line; an
            // error gets its full trace, as relay.py's handle_error gave real errors.
            var line = exception switch
            {
                null => $"[http] {message}",
                _ when logLevel == LogLevel.Warning => $"[http] {message}: {exception.GetType().Name}: {exception.Message}",
                _ => $"[http] {message}: {exception}",
            };
            IssunLog.Write(line);
        }
    }
}

/// <summary>
/// Replaces the generic host's ConsoleLifetime, which hooks Ctrl+C and
/// AppDomain.ProcessExit. The server lives inside a desktop app that decides
/// for itself when to stop; under ConsoleLifetime a window closing without
/// disposing the server first would have its process exit held open for the
/// host's whole shutdown timeout.
/// </summary>
internal sealed class EmbeddedLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
