using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Issun.Core;
using Issun.Platform;
using Issun.Presentation;
using Issun.Threading;
using Issun.Views;

namespace Issun.ViewModels;

public enum SetupPage { Discord = 0, Tailscale = 1, Source = 2, StartWithWindows = 3 }

/// <summary>
/// The setup window: four pages, each one live. Connect your source comes
/// straight after Tailscale (the owner, 12 Nov 2026), and its endpoint is a
/// field: filled from Tailscale when it has an address, editable either way,
/// and applied by the QR code's reload button or on leaving the page. The Discord application ID connects as soon as it looks like one,
/// the Tailscale buttons act when clicked, Start with Windows applies when
/// ticked, and the last page watches for the source's first push. So every page
/// shows the real state of what it set up. Next moves on, except that on the
/// Tailscale page it first checks Tailscale has an address for this PC with
/// Funnel on; Skip moves on without that check.
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
    private readonly DispatcherQueue _dispatcher;
    private readonly Coalescer _refresh;
    private readonly DispatcherQueueTimer _ticker;
    private readonly DispatcherQueueTimer _applyId;
    private HostSnapshot _snapshot = new();
    private bool _disposed;

    public SetupViewModel(IIssunHost host, DispatcherQueue dispatcher, SetupPage startPage = SetupPage.Discord)
    {
        _host = host;
        _dispatcher = dispatcher;
        _page = startPage;
        _refresh = new Coalescer(dispatcher, TimeSpan.FromMilliseconds(250), "setup refresh", Refresh);
        _ticker = dispatcher.CreateTimer();
        _ticker.Interval = TimeSpan.FromSeconds(1);
        _ticker.Tick += (_, _) => Tick();
        _applyId = dispatcher.CreateTimer();
        _applyId.Interval = ApplyDelay;
        _applyId.IsRepeating = false;
        _applyId.Tick += (_, _) =>
        {
            _applyId.Stop();
            _ = ApplyClientIdAsync();
        };

        Pairing = new PairingViewModel(host, dispatcher, "setup");
        Pairing.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PairingViewModel.SourceConnected))
                SourceConnectedChanged();
        };

        BackCommand = new Command(() => Go(_page - 1), () => _page > SetupPage.Discord, this);
        NextCommand = new AsyncCommand("setup: next", NextAsync, ex =>
        {
            Log.Write($"[setup] Next failed on the {Describe(_page)} page: {ex.Message}");
            if (_page == SetupPage.Tailscale) TailscaleError = $"[text] {ex.Message}";
        }, () => _page switch
        {
            SetupPage.Discord => DiscordAccepted,
            SetupPage.Source => Pairing.SourceConnected,
            _ => true,
        });
        SkipCommand = new Command(() =>
        {
            Log.Write($"[setup] Skip on the {Describe(_page)} page");
            if (_page == SetupPage.StartWithWindows)
            {
                // Past the last page there's nothing to go to: Skip ends setup
                // as Finish does, keeping the startup choice as it stands.
                Log.Write("[setup] Skip: setup finished");
                CloseRequested?.Invoke();
            }
            else
                Go(_page + 1);
        });
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

    /// <summary>The last page's Yes and No radio buttons, both views of <see cref="StartWithWindows"/>.</summary>
    public bool LaunchYes
    {
        get => StartWithWindows;
        set { if (value && !StartWithWindows) { Log.Write("[setup] Launch on startup: Yes"); StartWithWindows = true; } }
    }

    public bool LaunchNo
    {
        get => !StartWithWindows;
        set { if (value && StartWithWindows) { Log.Write("[setup] Launch on startup: No"); StartWithWindows = false; } }
    }
    public bool OnSourcePage => _page == SetupPage.Source;

    /// <summary>"Cancel" until the last page, then "Close": Microsoft's wizard rule once the task is done.</summary>
    /// <summary>
    /// "Cancel" on every page: Next finishes setup on the last one (the owner,
    /// 12 Nov 2026: "make sure Next is still there"), so no page is a finished
    /// one, which is when Microsoft's guidance renames Cancel to Close.
    /// </summary>
    public string CloseLabel => "Cancel";

    public bool ShowNext => true;

    /// <summary>Finish on the last page, where it ends setup (the owner's yes, 12 Nov 2026, after Microsoft's wizard guidance); Next elsewhere.</summary>
    public string NextLabel => _page == SetupPage.StartWithWindows ? "Finish" : "Next";

    public Command BackCommand { get; }
    public AsyncCommand NextCommand { get; }
    public Command SkipCommand { get; }

    /// <summary>
    /// Next, as the owner set it out on 12 Nov 2026. On the Tailscale page it
    /// asks Tailscale for this PC's address and stays put with an error if
    /// there is none, or when Funnel isn't on (Skip is the way past without
    /// Tailscale). Elsewhere it moves on.
    /// </summary>
    private async Task NextAsync()
    {
        switch (_page)
        {
            case SetupPage.Tailscale:
                TailscaleError = null;
                await _host.RefreshTailscaleAsync();
                if (_host.Snapshot.Tailscale?.PublicBase is null)
                {
                    Log.Write("[setup] Next on the Tailscale page: Tailscale has no address for this PC yet, staying");
                    TailscaleError = "[text]";
                    return;
                }
                // Funnel too, the owner's ask the same day: an address with
                // Funnel off is one the phone can't reach.
                if (_host.Snapshot.Tailscale is not { FunnelOn: true, FunnelTargetsPort: true })
                {
                    Log.Write("[setup] Next on the Tailscale page: Tailscale has an address but Funnel isn't on for Issun's port, staying");
                    TailscaleError = "[text]";
                    return;
                }
                Log.Write("[setup] Next on the Tailscale page: Tailscale has this PC's address and Funnel is on");
                Go(SetupPage.Source);
                break;
            case SetupPage.StartWithWindows:
                Log.Write("[setup] Finish: setup finished");
                CloseRequested?.Invoke();
                break;
            default:
                Go(_page + 1);
                break;
        }
    }
    public Command CloseCommand { get; }

    private void Go(SetupPage page)
    {
        if (page < SetupPage.Discord || page > SetupPage.StartWithWindows || page == _page)
            return;
        // Leaving Connect your source with an edited endpoint still applies it.
        if (_page == SetupPage.Source)
            _ = Pairing.ApplyPendingAsync();
        // Leaving the Discord page with a half-typed ID still applies what is there.
        if (_page == SetupPage.Discord && _applyId.IsRunning)
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
        Raise(nameof(NextLabel));
        if (page == SetupPage.Tailscale)
            CheckTailscaleCommand.Execute(null);
        // Without this Next stayed disabled after Back from the last page
        // (sandbox, 12 Nov 2026, in the WPF build).
        NextCommand.RaiseCanExecuteChanged();
    }

    internal static string Describe(SetupPage page) => page switch
    {
        SetupPage.Discord => "Discord",
        SetupPage.Tailscale => "Tailscale",
        SetupPage.StartWithWindows => "Start with Windows",
        _ => "Connect your source",
    };

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
            DiscordAcceptedChanged();
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

    public string? ClientIdProblem
    {
        get => _clientIdProblem;
        private set
        {
            if (Set(ref _clientIdProblem, value))
                DiscordAcceptedChanged();
        }
    }
    private string? _clientIdProblem;

    /// <summary>
    /// Whether Discord has accepted the application ID in the box: Discord's
    /// handshake with it came back READY (the owner, 13 Nov 2026: "can the
    /// first setup page actually check if you did the discord thing
    /// correctly"). Next waits for it, as Connect your source's Next waits for
    /// a source, and the status line under the ID says why it's waiting:
    /// no ID, an ID Discord refuses (4000), or Discord not running. Skip is the
    /// way past. An ID still being typed, or one that differs from the ID
    /// Issun is using, isn't accepted yet, whatever the connection says.
    /// </summary>
    public bool DiscordAccepted =>
        _snapshot.Discord.State == DiscordLinkState.Connected
        && ClientIdProblem is null
        && _clientId.Trim() == _host.Settings.DiscordClientId;

    private bool? _discordAcceptedLogged;

    private void DiscordAcceptedChanged()
    {
        var accepted = DiscordAccepted;
        if (_page == SetupPage.Discord && accepted != _discordAcceptedLogged)
        {
            _discordAcceptedLogged = accepted;
            Log.Write(accepted
                ? "[setup] Discord accepted the application ID, Next available"
                : "[setup] Discord hasn't accepted an application ID, Next greyed out");
        }
        NextCommand?.RaiseCanExecuteChanged();
    }

    public StatusLine DiscordStatus { get => _discordStatus; private set => Set(ref _discordStatus, value); }
    private StatusLine _discordStatus = new("Not connected", StatusLevel.Neutral);

    // ───────────────────────────── Tailscale ─────────────────────────────



    public AsyncCommand GetTailscaleCommand { get; }

    /// <summary>The command the install link runs, word for word, shown as its note.</summary>
    public string InstallCommandText { get; } = "winget " + string.Join(' ', Core.Platform.TailscaleCli.WingetInstallArguments);
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
            Raise(nameof(LaunchYes));
            Raise(nameof(LaunchNo));
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

    /// <summary>Connect your source's cards: the same ones the main window shows.</summary>
    public PairingViewModel Pairing { get; }

    /// <summary>--screenshot --expanded: draw the QR code too. Only ever on the demo host's made-up key.</summary>
    public void ShowQrForScreenshot() => Pairing.ShowQrForScreenshot();

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

    /// <summary>
    /// Next on Connect your source follows whether a source is checking in
    /// (the owner, 12 Nov 2026: the check runs by itself, so Next only follows
    /// it).
    /// </summary>
    private void SourceConnectedChanged()
    {
        Log.Write(Pairing.SourceConnected ? "[setup] Connect your source: connected, Next available" : "[setup] Connect your source: waiting, Next greyed out");
        NextCommand.RaiseCanExecuteChanged();
    }
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
        DiscordAcceptedChanged();

        TailscaleStatus = StatusText.Tailscale(s.Tailscale, s.Server.Port);
        OfferInstall = s.Tailscale is { Installed: false };
        OfferFunnel = StatusText.OfferFunnel(s.Tailscale);
        ShowUnattended = s.Tailscale is { Running: true };
        UnattendedStatus = Unattended(s.Tailscale?.Unattended);
        OfferUnattended = s.Tailscale is { Running: true, Unattended: false };

        Pairing.Refresh(s);
        Tick();
    }

    private void Tick()
    {
        var s = _snapshot;
        SourceStatus = StatusText.Phone(s, SystemClock.Instance.Now, DateTime.Now);
        Pairing.Tick(s);
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
        _ = Pairing.ApplyPendingAsync();
        Log.Write($"[setup] closed on the {Describe(_page)} page");
    }
}
