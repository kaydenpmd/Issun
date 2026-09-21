namespace Issun.Core;

public sealed record LogEntry(DateTime At, string Text);

/// <summary>
/// Issun's equivalent of relay.py's <c>print()</c>. Every module writes
/// "[tag] message" lines through here — the same shape relay.log used, so a
/// line reads the same whichever program wrote it.
///
/// Silent failure is the Ammy project's recurring bug: three separate times a
/// failure path returned nothing and logged nothing, and the empty log read as
/// "working". Every failure path in Issun writes a line.
/// </summary>
public static class Log
{
    private const int RecentCapacity = 500;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> RecentEntries = new();
    private static readonly AsyncLocal<List<string>?> CaptureTarget = new();
    private static string? _filePath;

    /// <summary>Raised on the writing thread. Handlers must not block.</summary>
    public static event Action<LogEntry>? Written;

    public static string? FilePath
    {
        get { lock (Gate) return _filePath; }
    }

    /// <summary>Also append every line to <paramref name="path"/>, rotating to .old past 5 MB.</summary>
    public static void UseFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        lock (Gate) _filePath = path;
    }

    public static void Write(string text)
    {
        var entry = new LogEntry(DateTime.Now, text);

        lock (Gate)
        {
            RecentEntries.Enqueue(entry);
            while (RecentEntries.Count > RecentCapacity)
                RecentEntries.Dequeue();

            if (_filePath is not null)
            {
                try
                {
                    var info = new FileInfo(_filePath);
                    if (info.Exists && info.Length > MaxFileBytes)
                        File.Move(_filePath, _filePath + ".old", overwrite: true);
                    File.AppendAllText(_filePath, $"{entry.At:yyyy-MM-ddTHH:mm:ss}  {text}{Environment.NewLine}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nowhere left to report a failure to write the report. The
                    // in-memory buffer and the window still have the line.
                }
            }
        }

        if (CaptureTarget.Value is { } capture)
            lock (capture) capture.Add(text);
        System.Diagnostics.Debug.WriteLine(text);

        try { Written?.Invoke(entry); }
        catch { /* a broken subscriber must not break logging */ }
    }

    /// <summary>The most recent lines, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Recent()
    {
        lock (Gate) return RecentEntries.ToArray();
    }

    /// <summary>
    /// For tests: collects lines written from the current async flow (and
    /// anything it starts with Task.Run) until disposed. Lines from other tests
    /// running in parallel are not included, so "nothing was logged" is
    /// assertable. Lines written on threads the flow did not start — Kestrel's
    /// request threads, for instance — are not captured; use Recent() there.
    /// </summary>
    public static LogCapture Capture() => new();

    public sealed class LogCapture : IDisposable
    {
        private readonly List<string> _lines = new();
        private readonly List<string>? _previous;

        internal LogCapture()
        {
            _previous = CaptureTarget.Value;
            CaptureTarget.Value = _lines;
        }

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToArray(); }
        }

        public void Dispose() => CaptureTarget.Value = _previous;
    }
}
