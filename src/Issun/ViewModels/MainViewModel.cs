using System.Windows.Media;
using System.Windows.Threading;
using Issun.Artwork;
using Issun.Core;
using Issun.Presentation;
using Issun.Threading;

namespace Issun.ViewModels;

/// <summary>
/// Everything the window and the tray show, read from <see cref="IIssunHost"/>.
///
/// Threading: the host raises Changed from whatever thread noticed the change —
/// Kestrel's, the presence worker's, a timer's. That signal is only ever used to
/// schedule a refresh on the dispatcher, coalesced to at most four a second, and
/// the refresh reads the host's immutable snapshot there. Nothing the host
/// might block on is called on the UI thread: file reads, the registry and the
/// host's async operations all go through Task.Run or are awaited.
///
/// This class is split by section: this file is the now-playing card, the
/// status rows and the tray tooltip; the other parts are MainViewModel.Pairing,
/// .Settings and .Diagnostics.
/// </summary>
public sealed partial class MainViewModel : Observable, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly IIssunHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly Coalescer _refresh;
    private readonly DispatcherTimer _ticker;
    private readonly CoverLoader _covers = new();
    private HostSnapshot _snapshot = new();
    private bool _disposed;

    public MainViewModel(IIssunHost host, Dispatcher dispatcher, IClock? clock = null)
    {
        _host = host;
        _dispatcher = dispatcher;
        _clock = clock ?? SystemClock.Instance;

        _refresh = new Coalescer(dispatcher, RefreshInterval, "window refresh", Refresh);

        // The progress bar and "last push 12s ago" move between pushes. The
        // phone reports once every thirty seconds; the window ticks every one,
        // extrapolating from the reading's arrival time (see Presentation.Playhead).
        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Tick();

        EndpointCopied = new Flash(dispatcher);
        KeyCopied = new Flash(dispatcher);
        LogCopied = new Flash(dispatcher);
        SettingsSaved = new Flash(dispatcher, TimeSpan.FromSeconds(4));

        InitTailscaleCommands();
        InitPairing();
        InitSettings();
        InitDiagnostics();

        _host.Changed += OnHostChanged;
        Refresh();
        _ = ReadStartWithWindowsAsync();
    }

    public string VersionText => $"Issun {IssunInfo.Display}";

    /// <summary>Set when the host failed to start; shown at the top of the window until the next successful start.</summary>
    public string? StartupError
    {
        get => _startupError;
        private set => Set(ref _startupError, value);
    }
    private string? _startupError;

    public void ReportStartupFailure(Exception ex) =>
        StartupError = $"Issun couldn't start: {ex.Message}";

    /// <summary>Tick the clock-driven text only while someone can see it. The tray tooltip still follows every change.</summary>
    public void SetWindowVisible(bool visible)
    {
        if (visible && !_disposed)
        {
            Refresh();
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
        }
    }

    /// <summary>Reads the host now rather than at the next coalesced tick. UI thread.</summary>
    public void RefreshNow() => Refresh();

    private void OnHostChanged() => _refresh.Signal();

    private void Refresh()
    {
        if (_disposed)
            return;

        var s = _host.Snapshot;
        _snapshot = s;

        // ── Now playing ──
        if (s.Track is { } track)
        {
            Title = NowPlayingText.Title(track);
            Artist = NowPlayingText.Artist(track);
        }
        HasTrack = s.Track is not null;
        NothingPlayingDetail = NowPlayingText.NothingPlayingDetail(s);
        SourceVersion = NowPlayingText.SourceVersion(s.PhoneVersion);
        OnDiscord = NowPlayingText.OnDiscord(s);
        UpdateCover(s);

        // ── Status ──
        DiscordStatus = StatusText.Discord(s.Discord);
        ReceiverStatus = StatusText.Receiver(s.Server);
        TailscaleStatus = StatusText.Tailscale(s.Tailscale, s.Server.Port);
        OfferFunnel = StatusText.OfferFunnel(s.Tailscale);
        if (s.Server.Listening)
            StartupError = null;

        RefreshPairing(s);
        RefreshSettings();
        RefreshDiagnostics(s);
        Tick();
    }

    /// <summary>The parts that depend on the clock as well as the snapshot.</summary>
    private void Tick()
    {
        var s = _snapshot;
        var now = _clock.Now;
        var nowLocal = DateTime.Now;

        if (s.Track is { } track)
        {
            var elapsed = Playhead.Elapsed(track, s.TrackObservedAt, now);
            var duration = Playhead.Duration(track);
            Progress = Playhead.Fraction(track, s.TrackObservedAt, now) ?? 0;
            HasProgress = duration is not null && elapsed is not null;
            ElapsedText = elapsed is double e ? TimeText.Clock(e) : "";
            DurationText = duration is double d ? TimeText.Clock(d) : "";
        }
        else
        {
            HasProgress = false;
        }

        PhoneStatus = StatusText.Phone(s, now, nowLocal);
        TrayTooltip = TrayText.Tooltip(s, nowLocal);
    }

    // ───────────────────────────── Now playing ─────────────────────────────

    public bool HasTrack { get => _hasTrack; private set => Set(ref _hasTrack, value); }
    private bool _hasTrack;

    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _title = "";

    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    private string _artist = "";

    public string NothingPlayingDetail { get => _nothingDetail; private set => Set(ref _nothingDetail, value); }
    private string _nothingDetail = "";

    public string? SourceVersion { get => _sourceVersion; private set => Set(ref _sourceVersion, value); }
    private string? _sourceVersion;

    public string? OnDiscord { get => _onDiscord; private set => Set(ref _onDiscord, value); }
    private string? _onDiscord;

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private double _progress;

    public bool HasProgress { get => _hasProgress; private set => Set(ref _hasProgress, value); }
    private bool _hasProgress;

    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    private string _elapsedText = "";

    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }
    private string _durationText = "";

    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            if (Set(ref _cover, value))
                Raise(nameof(HasCover));
        }
    }
    private ImageSource? _cover;

    public bool HasCover => _cover is not null;

    private string? _coverUrl;
    private Task _coverSettled = Task.CompletedTask;

    /// <summary>Completes when the cover for the current snapshot has loaded or failed. For --screenshot.</summary>
    public Task CoverSettled => _coverSettled;

    private void UpdateCover(HostSnapshot s)
    {
        // A cover belongs to a track: once the phone stops playing, the last
        // one must not linger on a card that says "Nothing playing".
        var url = s.Track is not null && s.ArtworkUrl is { Length: > 0 } art
            ? CoverUrl.ForWindow(art, s.PublicBase, s.Server.Port)
            : null;
        if (url == _coverUrl)
            return;
        _coverUrl = url;

        if (url is null)
        {
            Cover = null;
            _coverSettled = Task.CompletedTask;
            return;
        }

        var load = _covers.LoadAsync(url);
        // A cached cover is already there, and swapping straight to it avoids a
        // flash of the placeholder. An uncached one shows the placeholder
        // rather than the previous track's cover while it loads.
        if (!load.IsCompleted)
            Cover = null;
        _coverSettled = ApplyCoverAsync(url, load);
    }

    private async Task ApplyCoverAsync(string url, Task<ImageSource?> load)
    {
        var image = await load;         // never throws; failures are logged by CoverLoader
        if (_coverUrl == url && !_disposed)
            Cover = image;
    }

    // ─────────────────────────────── Status ───────────────────────────────

    public StatusLine PhoneStatus { get => _phoneStatus; private set => Set(ref _phoneStatus, value); }
    private StatusLine _phoneStatus = new("", StatusLevel.Neutral);

    public StatusLine DiscordStatus { get => _discordStatus; private set => Set(ref _discordStatus, value); }
    private StatusLine _discordStatus = new("", StatusLevel.Neutral);

    public StatusLine ReceiverStatus { get => _receiverStatus; private set => Set(ref _receiverStatus, value); }
    private StatusLine _receiverStatus = new("", StatusLevel.Neutral);

    public StatusLine TailscaleStatus { get => _tailscaleStatus; private set => Set(ref _tailscaleStatus, value); }
    private StatusLine _tailscaleStatus = new("", StatusLevel.Neutral);

    public bool OfferFunnel { get => _offerFunnel; private set => Set(ref _offerFunnel, value); }
    private bool _offerFunnel;

    /// <summary>What `tailscale funnel` printed the last time the button was pressed. May contain a link to click.</summary>
    public string? FunnelOutput { get => _funnelOutput; private set => Set(ref _funnelOutput, value); }
    private string? _funnelOutput;

    public bool FunnelFailed { get => _funnelFailed; private set => Set(ref _funnelFailed, value); }
    private bool _funnelFailed;

    public string? TailscaleError { get => _tailscaleError; private set => Set(ref _tailscaleError, value); }
    private string? _tailscaleError;

    public AsyncCommand RefreshTailscaleCommand { get; private set; } = null!;
    public AsyncCommand EnableFunnelCommand { get; private set; } = null!;

    private void InitTailscaleCommands()
    {
        RefreshTailscaleCommand = new AsyncCommand("Tailscale refresh", async () =>
        {
            TailscaleError = null;
            await _host.RefreshTailscaleAsync();
        }, ex => TailscaleError = $"Couldn't check Tailscale: {ex.Message}");

        EnableFunnelCommand = new AsyncCommand("Turn on Funnel", async () =>
        {
            TailscaleError = null;
            FunnelOutput = null;
            var (ok, output) = await _host.EnableFunnelAsync();
            FunnelFailed = !ok;
            var text = output.Trim();
            FunnelOutput = text.Length > 0 ? text : ok ? "Funnel is on." : "Tailscale didn't say why.";
            if (!ok)
                Log.Write($"[ui] Turn on Funnel didn't succeed: {FirstLine(text)}");
            else
                await _host.RefreshTailscaleAsync();
        }, ex =>
        {
            FunnelFailed = true;
            FunnelOutput = $"Couldn't run Tailscale: {ex.Message}";
        });
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line ?? "(no output)";
    }

    // ──────────────────────────────── Tray ────────────────────────────────

    public string TrayTooltip { get => _trayTooltip; private set => Set(ref _trayTooltip, value); }
    private string _trayTooltip = "Issun";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.Changed -= OnHostChanged;
        Log.Written -= OnLogWritten;
        _ticker.Stop();
    }
}
