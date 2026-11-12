using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Issun.Core;
using Issun.Platform;
using Issun.Presentation;
using Issun.Threading;

namespace Issun.ViewModels;

public enum SetupPage { Discord = 0, Tailscale = 1, StartWithWindows = 2, Source = 3 }

/// <summary>
/// The setup window: four pages, each one live. Nothing waits for Next to be
/// applied. The Discord application ID connects as soon as it looks like one,
/// the Tailscale buttons act when clicked, Start with Windows applies when
/// ticked, and the last page watches for the source's first push. So every page
/// shows the real state of what it set up, and Next only moves on.
///
/// The structure is Microsoft's wizard guidance, read 11 Nov 2026 and saved in
/// the design-research library (microsoft/windows-setup-wizards): no welcome
/// page, a main instruction on every page, Back and Next, Cancel that becomes
/// Close on the last page. Everything else is the main window's own cards and
/// type, which follow Windows 11 Settings.
/// </summary>
public sealed class SetupViewModel : Observable, IDisposable
{
    public const string DeveloperPortal = "https://discord.com/developers/applications";
    public const string TailscaleDownload = "https://tailscale.com/download";

    private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(600);

    private readonly IIssunHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly Coalescer _refresh;
    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _applyId;
    private HostSnapshot _snapshot = new();
    private bool _disposed;

    public SetupViewModel(IIssunHost host, Dispatcher dispatcher, SetupPage startPage = SetupPage.Discord)
    {
        _host = host;
        _dispatcher = dispatcher;
        _page = startPage;
        _refresh = new Coalescer(dispatcher, TimeSpan.FromMilliseconds(250), "setup refresh", Refresh);
        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Tick();
        _applyId = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = ApplyDelay };
        _applyId.Tick += (_, _) =>
        {
            _applyId.Stop();
            _ = ApplyClientIdAsync();
        };

        EndpointCopied = new Flash(dispatcher);
        KeyCopied = new Flash(dispatcher);

        BackCommand = new Command(() => Go(_page - 1), () => _page > SetupPage.Discord);
        NextCommand = new Command(() => Go(_page + 1), () => _page < SetupPage.Source);
        CloseCommand = new Command(() => CloseRequested?.Invoke());
        OpenPortalCommand = new Command(() => Open(DeveloperPortal, "the Discord Developer Portal"));
        // Installs with winget, as the owner asked (12 Nov 2026). Where there is
        // no winget (some Windows 10 and LTSC installs, Windows Sandbox) it opens
        // the download page instead, as the button did before. The words are
        // the owner's to write: [text] marks each until they do.
        GetTailscaleCommand = new AsyncCommand("setup: install Tailscale", async () =>
        {
            TailscaleError = null;
            var result = await _host.InstallTailscaleAsync();
            if (result.WingetMissing)
            {
                Open(TailscaleDownload, "Tailscale's download page (no winget)");
                TailscaleError = "[text]";
            }
            else if (!result.Ok)
                TailscaleError = $"[text] {result.Output}";
        }, ex => TailscaleError = $"[text] {ex.Message}");

        CheckTailscaleCommand = new AsyncCommand("setup: check Tailscale", async () =>
        {
            TailscaleError = null;
            await _host.RefreshTailscaleAsync();
        }, ex => TailscaleError = $"Couldn't check Tailscale: {ex.Message}");

        EnableFunnelCommand = new AsyncCommand("setup: turn on Funnel", async () =>
        {
            TailscaleError = null;
            FunnelOutput = null;
            var (ok, output) = await _host.EnableFunnelAsync();
            var text = output.Trim();
            if (!ok)
                FunnelOutput = text.Length > 0 ? text : "Tailscale didn't say why.";
        }, ex => TailscaleError = $"Couldn't turn on Funnel: {ex.Message}");

        EnableUnattendedCommand = new AsyncCommand("setup: turn on Run unattended", async () =>
        {
            TailscaleError = null;
            var (ok, output) = await _host.EnableUnattendedAsync();
            if (!ok)
                TailscaleError = output;
        }, ex => TailscaleError = $"Couldn't turn on Run unattended: {ex.Message}");

        CopyEndpointCommand = new AsyncCommand("setup: copy endpoint", async () =>
        {
            if (EndpointUrl is { } url)
                EndpointCopied.Show(await DesktopShell.CopyAsync(url, "endpoint") ? "Copied" : "Couldn't copy — try again");
        }, _ => EndpointCopied.Show("Couldn't copy — try again"), () => EndpointUrl is not null);

        CopyKeyCommand = new AsyncCommand("setup: copy key", async () =>
        {
            KeyCopied.Show(await DesktopShell.CopyAsync(_host.Settings.Key, "key") ? "Copied" : "Couldn't copy — try again");
        }, _ => KeyCopied.Show("Couldn't copy — try again"));

        ToggleKeyCommand = new Command(() => KeyShown = !KeyShown);
        ToggleQrCommand = new Command(() => QrShown = !QrShown, () => EndpointUrl is not null);
        // Show and Hide, the owner's words; the same pair for the key and the code.

        _clientId = host.Settings.DiscordClientId;
        _host.Changed += OnHostChanged;
        Log.Write($"[setup] opened on the {Describe(_page)} page");
        Refresh();
        _ = ReadStartWithWindowsAsync();
    }

    /// <summary>The window closes itself when this fires: Cancel, Close, or Escape.</summary>
    public event Action? CloseRequested;

    // ───────────────────────────── Pages ─────────────────────────────

    private SetupPage _page;

    public SetupPage Page => _page;

    public bool OnDiscordPage => _page == SetupPage.Discord;
    public bool OnTailscalePage => _page == SetupPage.Tailscale;
    public bool OnStartPage => _page == SetupPage.StartWithWindows;
    public bool OnSourcePage => _page == SetupPage.Source;

    /// <summary>"Cancel" until the last page, then "Close": Microsoft's wizard rule once the task is done.</summary>
    public string CloseLabel => _page == SetupPage.Source ? "Close" : "Cancel";

    public bool ShowNext => _page < SetupPage.Source;

    public Command BackCommand { get; }
    public Command NextCommand { get; }
    public Command CloseCommand { get; }

    private void Go(SetupPage page)
    {
        if (page < SetupPage.Discord || page > SetupPage.Source || page == _page)
            return;
        // Leaving the Discord page with a half-typed ID still applies what is there.
        if (_page == SetupPage.Discord && _applyId.IsEnabled)
        {
            _applyId.Stop();
            _ = ApplyClientIdAsync();
        }
        Log.Write($"[setup] {Describe(_page)} -> {Describe(page)}");
        _page = page;
        Raise(nameof(Page));
        Raise(nameof(OnDiscordPage));
        Raise(nameof(OnTailscalePage));
        Raise(nameof(OnStartPage));
        Raise(nameof(OnSourcePage));
        Raise(nameof(CloseLabel));
        Raise(nameof(ShowNext));
        if (page == SetupPage.Tailscale)
            CheckTailscaleCommand.Execute(null);
        CommandManagerRequery();
    }

    internal static string Describe(SetupPage page) => page switch
    {
        SetupPage.Discord => "Discord",
        SetupPage.Tailscale => "Tailscale",
        SetupPage.StartWithWindows => "Start with Windows",
        _ => "Connect your source",
    };

    private static void CommandManagerRequery() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    // ───────────────────────────── Discord ─────────────────────────────

    public Command OpenPortalCommand { get; }

    private string _clientId;

    /// <summary>
    /// The ID as typed. Applied 0.6 s after it stops changing, once it looks
    /// like an ID, so pasting one connects straight away and the status line
    /// beneath answers whether Discord accepts it.
    /// </summary>
    public string ClientId
    {
        get => _clientId;
        set
        {
            if (!Set(ref _clientId, value))
                return;
            _applyId.Stop();
            ClientIdProblem = null;
            if (LooksLikeId(value.Trim()))
                _applyId.Start();
        }
    }

    /// <summary>Discord's IDs are snowflakes: 17 to 20 digits.</summary>
    internal static bool LooksLikeId(string text) =>
        text.Length is >= 17 and <= 20 && text.All(c => c is >= '0' and <= '9');

    private async Task ApplyClientIdAsync()
    {
        var id = _clientId.Trim();
        if (id == _host.Settings.DiscordClientId)
            return;
        if (!LooksLikeId(id))
        {
            ClientIdProblem = "An application ID is 17–20 digits long.";
            Log.Write($"[setup] not applying a Discord application ID of {id.Length} characters: not 17 to 20 digits");
            return;
        }
        try
        {
            Log.Write($"[setup] applying the Discord application ID ({id.Length} digits)");
            await _host.UpdateSettingsAsync(_host.Settings with { DiscordClientId = id });
        }
        catch (Exception ex)
        {
            Log.Write($"[setup] couldn't save the Discord application ID: {ex.Message}");
            ClientIdProblem = $"Couldn't save it: {ex.Message}";
        }
    }

    public string? ClientIdProblem { get => _clientIdProblem; private set => Set(ref _clientIdProblem, value); }
    private string? _clientIdProblem;

    public StatusLine DiscordStatus { get => _discordStatus; private set => Set(ref _discordStatus, value); }
    private StatusLine _discordStatus = new("Not connected", StatusLevel.Neutral);

    // ───────────────────────────── Tailscale ─────────────────────────────

    public AsyncCommand GetTailscaleCommand { get; }
    public AsyncCommand CheckTailscaleCommand { get; }
    public AsyncCommand EnableFunnelCommand { get; }
    public AsyncCommand EnableUnattendedCommand { get; }

    public StatusLine TailscaleStatus { get => _tailscaleStatus; private set => Set(ref _tailscaleStatus, value); }
    private StatusLine _tailscaleStatus = new("Checking…", StatusLevel.Neutral);

    /// <summary>Tailscale isn't on this PC: offer the download page.</summary>
    public bool OfferInstall { get => _offerInstall; private set => Set(ref _offerInstall, value); }
    private bool _offerInstall;

    public bool OfferFunnel { get => _offerFunnel; private set => Set(ref _offerFunnel, value); }
    private bool _offerFunnel;

    public StatusLine UnattendedStatus { get => _unattendedStatus; private set => Set(ref _unattendedStatus, value); }
    private StatusLine _unattendedStatus = new("Checking…", StatusLevel.Neutral);

    public bool ShowUnattended { get => _showUnattended; private set => Set(ref _showUnattended, value); }
    private bool _showUnattended;

    public bool OfferUnattended { get => _offerUnattended; private set => Set(ref _offerUnattended, value); }
    private bool _offerUnattended;

    public string? FunnelOutput { get => _funnelOutput; private set => Set(ref _funnelOutput, value); }
    private string? _funnelOutput;

    public string? TailscaleError { get => _tailscaleError; private set => Set(ref _tailscaleError, value); }
    private string? _tailscaleError;

    /// <summary>
    /// Run unattended, in words. Off, Tailscale waits for its tray app after a
    /// restart, which is what kept Funnel down for three hours after the
    /// 23 Sept 2026 reboot (Issun's CLAUDE.md, the owner's live test).
    /// </summary>
    internal static StatusLine Unattended(bool? on) => on switch
    {
        true => new("On: Tailscale connects when this PC starts, before you sign in", StatusLevel.Good),
        false => new("Off: after a restart, Tailscale waits until you sign in to Windows", StatusLevel.Warning),
        null => new("Couldn't tell whether it's on", StatusLevel.Neutral),
    };

    // ───────────────────────────── Start with Windows ─────────────────────────────

    private bool _startWithWindows;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value))
                return;
            Log.Write($"[setup] Start with Windows {(value ? "ticked" : "unticked")}");
            _ = Task.Run(() =>
            {
                try { _host.StartWithWindows = value; }
                catch (Exception ex) { Log.Write($"[setup] couldn't change Start with Windows: {ex.Message}"); }
            });
        }
    }

    private async Task ReadStartWithWindowsAsync()
    {
        try
        {
            var on = await Task.Run(() => _host.StartWithWindows);
            // A fresh install starts with Windows unless the person unticks it:
            // Issun does nothing for Discord while it isn't running.
            if (!on && _host.FirstRun)
            {
                StartWithWindows = true;
                return;
            }
            Set(ref _startWithWindows, on, nameof(StartWithWindows));
        }
        catch (Exception ex)
        {
            Log.Write($"[setup] couldn't read Start with Windows: {ex.Message}");
        }
    }

    // ───────────────────────────── Source ─────────────────────────────

    public Flash EndpointCopied { get; }
    public Flash KeyCopied { get; }
    public AsyncCommand CopyEndpointCommand { get; }
    public AsyncCommand CopyKeyCommand { get; }
    public Command ToggleKeyCommand { get; }
    public Command ToggleQrCommand { get; }

    public string? EndpointUrl
    {
        get => _endpointUrl;
        private set
        {
            if (Set(ref _endpointUrl, value))
            {
                Raise(nameof(HasEndpoint));
                CopyEndpointCommand.RaiseCanExecuteChanged();
                UpdateQr();
            }
        }
    }
    private string? _endpointUrl;

    public bool HasEndpoint => _endpointUrl is not null;

    public bool KeyShown
    {
        get => _keyShown;
        private set
        {
            if (Set(ref _keyShown, value))
            {
                Raise(nameof(KeyBlur));
                Raise(nameof(ToggleKeyLabel));
                Log.Write($"[setup] key {(value ? "shown" : "hidden")}");
            }
        }
    }
    private bool _keyShown;

    /// <summary>The key itself, drawn blurred until Show (<see cref="KeyBlur"/>), as the owner asked.</summary>
    public string KeyText => _host.Settings.Key;
    public double KeyBlur => _keyShown ? 0 : BlurRadius;
    public string ToggleKeyLabel => _keyShown ? "Hide" : "Show";

    /// <summary>
    /// A Gaussian blur this wide on 13 px text or a 200 px code leaves nothing
    /// legible or scannable. It hides the key from a glance or a screenshot;
    /// it isn't encryption.
    /// </summary>
    private const double BlurRadius = 14;

    /// <summary>
    /// The QR code carries the key, so like the key it is blurred until Show:
    /// a screenshot of this window shouldn't give the key away.
    /// </summary>
    public bool QrShown
    {
        get => _qrShown;
        private set
        {
            if (!Set(ref _qrShown, value))
                return;
            Raise(nameof(ToggleQrLabel));
            Raise(nameof(QrBlur));
            Log.Write($"[setup] QR code {(value ? "shown" : "hidden")}");
        }
    }
    private bool _qrShown;

    /// <summary>--screenshot --expanded: draw the QR code too. Only ever on the demo host's made-up key.</summary>
    public void ShowQrForScreenshot() => QrShown = true;

    public string ToggleQrLabel => _qrShown ? "Hide" : "Show";

    public double QrBlur => _qrShown ? 0 : BlurRadius;

    public BitmapSource? Qr { get => _qr; private set => Set(ref _qr, value); }
    private BitmapSource? _qr;

    private string? _qrFor;

    private void UpdateQr()
    {
        if (_endpointUrl is null)
        {
            Qr = null;
            _qrFor = null;
            return;
        }
        var payload = PairingCode.Payload(_endpointUrl, _host.Settings.Key);
        if (payload == _qrFor)
            return;
        try
        {
            Qr = PairingCode.Render(payload);
            _qrFor = payload;
        }
        catch (Exception ex)
        {
            Log.Write($"[setup] couldn't draw the QR code: {ex.Message}");
            Qr = null;
        }
    }

    public StatusLine SourceStatus { get => _sourceStatus; private set => Set(ref _sourceStatus, value); }
    private StatusLine _sourceStatus = new("Waiting for the first push", StatusLevel.Neutral);

    // ───────────────────────────── Refresh ─────────────────────────────

    private void OnHostChanged() => _refresh.Signal();

    public void SetVisible(bool visible)
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

    private bool _sourceSeenLogged;
    private string? _discordLogged;

    private void Refresh()
    {
        if (_disposed)
            return;
        var s = _snapshot = _host.Snapshot;

        DiscordStatus = StatusText.Discord(s.Discord);
        if (_page == SetupPage.Discord && DiscordStatus.Text != _discordLogged)
        {
            _discordLogged = DiscordStatus.Text;
            Log.Write($"[setup] Discord: {_discordLogged}");
        }

        TailscaleStatus = StatusText.Tailscale(s.Tailscale, s.Server.Port);
        OfferInstall = s.Tailscale is { Installed: false };
        OfferFunnel = StatusText.OfferFunnel(s.Tailscale);
        ShowUnattended = s.Tailscale is { Running: true };
        UnattendedStatus = Unattended(s.Tailscale?.Unattended);
        OfferUnattended = s.Tailscale is { Running: true, Unattended: false };

        EndpointUrl = s.EndpointUrl;
        Raise(nameof(KeyText));
        UpdateQr();
        Tick();
    }

    private void Tick()
    {
        var s = _snapshot;
        SourceStatus = StatusText.Phone(s, SystemClock.Instance.Now, DateTime.Now);
        if (!_sourceSeenLogged && s.LastCheckinAt > 0 && !s.PhoneSilent)
        {
            _sourceSeenLogged = true;
            Log.Write($"[setup] the source is checking in ({NowPlayingText.SourceVersion(s.SourceName, s.PhoneVersion) ?? "unnamed"})");
        }
    }

    private static void Open(string url, string what)
    {
        Log.Write($"[setup] opening {what}");
        DesktopShell.OpenLink(new Uri(url));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.Changed -= OnHostChanged;
        _ticker.Stop();
        _applyId.Stop();
        // An ID typed in the last 0.6 s before closing still counts.
        if (_clientId.Trim() != _host.Settings.DiscordClientId && LooksLikeId(_clientId.Trim()))
            _ = ApplyClientIdAsync();
        Log.Write($"[setup] closed on the {Describe(_page)} page");
    }
}
