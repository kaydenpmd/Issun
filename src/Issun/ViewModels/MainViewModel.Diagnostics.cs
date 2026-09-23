using System.Collections.Concurrent;
using Issun.Core;
using Issun.Platform;
using Issun.Presentation;
using Issun.Threading;

namespace Issun.ViewModels;

// The Diagnostics and Log sections, both collapsed until asked for. They are
// where the owner looks when something has gone quiet, so they show the same
// text the log file and the uptime log hold, in the same words.
public sealed partial class MainViewModel
{
    private readonly LogTail _logTail = new();
    private readonly ConcurrentQueue<LogEntry> _logPending = new();
    private Coalescer _logFlush = null!;
    private bool _logViewStale;

    public Flash LogCopied { get; }
    public AsyncCommand RefreshUptimeCommand { get; private set; } = null!;
    public Command OpenLogFolderCommand { get; private set; } = null!;
    public AsyncCommand CopyLogCommand { get; private set; } = null!;

    /// <summary>
    /// Raised on the UI thread with text to append, or with Reload set when the
    /// view should replace its text with <see cref="LogText"/>. An event rather
    /// than a bound property: rebinding 300 lines on every new one would make
    /// the text box lose its scroll position and its selection.
    /// </summary>
    public event Action<string, bool>? LogUpdated;

    public string LogText => _logTail.Text;

    private void InitDiagnostics()
    {
        _logFlush = new Coalescer(_dispatcher, TimeSpan.FromMilliseconds(200), "log refresh", FlushLog);

        // Subscribe first, then take the snapshot: a line written in between
        // arrives both ways and LogTail drops the duplicate, where the other
        // order would lose it.
        Log.Written += OnLogWritten;
        _logTail.Seed(Log.Recent());

        RefreshUptimeCommand = new AsyncCommand("uptime summary", LoadUptimeSummaryAsync,
            ex => UptimeSummary = $"Couldn't read the check-in history: {ex.Message}");
        OpenLogFolderCommand = new Command(() => DesktopShell.OpenFolder(_host.DataFolder));
        CopyLogCommand = new AsyncCommand("copy log", async () =>
            LogCopied.Show(await DesktopShell.CopyAsync(_logTail.Text, "log") ? "Copied" : "Couldn't copy — try again"),
            ex => LogCopied.Show("Couldn't copy — try again"));
    }

    private void RefreshDiagnostics(HostSnapshot s)
    {
        DiagSummary = string.IsNullOrWhiteSpace(s.DiagSummary)
            ? "The source hasn't sent a report yet."
            : s.DiagSummary;
    }

    /// <summary>The phone's last self-report, in relay.py's _diag_summary() format.</summary>
    public string DiagSummary { get => _diagSummary; private set => Set(ref _diagSummary, value); }
    private string _diagSummary = "";

    /// <summary>relay.py --summary's text: every gap in check-ins, grouped by Ammy build.</summary>
    public string? UptimeSummary { get => _uptimeSummary; private set => Set(ref _uptimeSummary, value); }
    private string? _uptimeSummary;

    public bool DiagnosticsExpanded
    {
        get => _diagnosticsExpanded;
        set
        {
            // The summary reads the whole uptime log, so it is read when the
            // section is opened rather than on every refresh.
            if (Set(ref _diagnosticsExpanded, value) && value)
                RefreshUptimeCommand.Execute(null);
        }
    }
    private bool _diagnosticsExpanded;

    public bool LogExpanded
    {
        get => _logExpanded;
        set
        {
            if (Set(ref _logExpanded, value) && value && _logViewStale)
            {
                _logViewStale = false;
                LogUpdated?.Invoke("", true);
            }
        }
    }
    private bool _logExpanded;

    private async Task LoadUptimeSummaryAsync()
    {
        var text = await Task.Run(() => _host.UptimeSummary());
        UptimeSummary = string.IsNullOrWhiteSpace(text) ? "No check-ins recorded yet." : text.TrimEnd();
    }

    // Log.Written fires on whichever thread wrote the line. It must not block,
    // so it only queues and schedules.
    private void OnLogWritten(LogEntry entry)
    {
        _logPending.Enqueue(entry);
        _logFlush.Signal();
    }

    private void FlushLog()
    {
        if (_disposed)
            return;
        var batch = new List<LogEntry>();
        while (_logPending.TryDequeue(out var entry))
            batch.Add(entry);
        if (batch.Count == 0)
            return;

        var (appended, reload) = _logTail.Add(batch);
        // While the section is closed the text box isn't updated at all; it
        // reloads once when opened.
        if (!_logExpanded)
        {
            _logViewStale = true;
            return;
        }
        if (reload || appended.Length > 0)
            LogUpdated?.Invoke(appended, reload);
    }

    /// <summary>For --screenshot --expanded: open everything that starts collapsed.</summary>
    public void ExpandAll()
    {
        AdvancedExpanded = true;
        LogExpanded = true;
        DiagnosticsExpanded = true;
    }
}
