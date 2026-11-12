using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Issun.Core;
using Issun.Platform;
using Issun.Presentation;
using Issun.Threading;

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
        NextCommand = new AsyncCommand("setup: next", NextAsync, ex =>
        {
            Log.Write($"[setup] Next failed on the {Describe(_page)} page: {ex.Message}");
            if (_page == SetupPage.Tailscale) TailscaleError = $"[text] {ex.Message}";
        }, () => _page < SetupPage.StartWithWindows && (_page != SetupPage.Source || SourceConnected));
        SkipCommand = new Command(() =>
        {
            Log.Write(_page == SetupPage.Tailscale ? "[setup] skipped the Tailscale check" : "[setup] skipped connecting a source");
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

        CopyEndpointCommand = new AsyncCommand("setup: copy endpoint", async () =>
        {
            if (_endpointText.Trim() is { Length: > 0 } url)
                EndpointCopied.Show(await DesktopShell.CopyAsync(url, "endpoint") ? "Copied" : "Couldn't copy — try again");
        }, _ => EndpointCopied.Show("Couldn't copy — try again"), () => _endpointText.Trim().Length > 0);

        ReloadQrCommand = new AsyncCommand("setup: reload QR code", async () => { await ApplyEndpointAsync(); },
            ex => EndpointProblem = $"[text] {ex.Message}");

        // A new key straight away: the owner asked for the button, not a
        // confirmation (12 Nov 2026). The main window's New key… asks first.
        NewKeyCommand = new AsyncCommand("setup: new key", async () =>
        {
            KeyProblem = null;
            Log.Write("[setup] making a new key");
            await _host.RegenerateKeyAsync();
            KeyShown = true;
            Raise(nameof(KeyText));
            KeyChanged();
        }, ex =>
        {
            Log.Write($"[setup] couldn't make a new key: {ex.Message}");
            KeyProblem = $"[text] {ex.Message}";
        });

        CopyKeyCommand = new AsyncCommand("setup: copy key", async () =>
        {
            KeyCopied.Show(await DesktopShell.CopyAsync(_host.Settings.Key, "key") ? "Copied" : "Couldn't copy — try again");
        }, _ => KeyCopied.Show("Couldn't copy — try again"));

        ToggleKeyCommand = new Command(() => KeyShown = !KeyShown);
        ToggleQrCommand = new Command(() => QrShown = !QrShown, () => Qr is not null);
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
    public string CloseLabel => _page == SetupPage.StartWithWindows ? "Close" : "Cancel";

    public bool ShowNext => _page < SetupPage.StartWithWindows;

    /// <summary>
    /// Skip is always on the Tailscale page and on Connect your source (the
    /// owner, 12 Nov 2026). On the first it goes on without Next's check, for
    /// people who reach Issun some other way; on the second it goes on without
    /// a source having connected, for people who'd rather connect later. There
    /// it goes while a source is connected, since Next then does the same, and
    /// comes back if the connection drops (the owner, 12 Nov 2026).
    /// </summary>
    public bool ShowSkip => _page == SetupPage.Tailscale || (_page == SetupPage.Source && !_sourceConnected);

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
        if (_page == SetupPage.Source && (_endpointDirty || _qrKeyStale))
            _ = ApplyEndpointAsync();
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
        Raise(nameof(ShowSkip));
        if (page == SetupPage.Tailscale)
            CheckTailscaleCommand.Execute(null);
        // Next is an AsyncCommand, which WPF's requery doesn't reach: without
        // this it stayed disabled after Back from the last page (sandbox,
        // 12 Nov 2026).
        NextCommand.RaiseCanExecuteChanged();
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

    /// <summary>
    /// What the typed text means as a public base, or null when it can't be one.
    /// "https://" is assumed when no scheme is typed, and a pasted endpoint
    /// loses its "/now-playing", since that is added back for the source.
    /// </summary>
    internal static string? NormaliseAddress(string typed)
    {
        var text = typed.Trim().TrimEnd('/');
        if (text.Length == 0)
            return "";
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (text.EndsWith("/now-playing", StringComparison.OrdinalIgnoreCase))
            text = text[..^"/now-playing".Length].TrimEnd('/');
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Contains('.')
               && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? text
            : null;
    }


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

    public AsyncCommand ReloadQrCommand { get; }
    public AsyncCommand NewKeyCommand { get; }

    private string _endpointText = "";

    /// <summary>The endpoint the QR code carries, as last applied; null for none.</summary>
    private string? _qrEndpoint;

    /// <summary>
    /// The key the QR code carries. Not read live from the settings: a new key
    /// made while the endpoint is being edited waits for the reload button
    /// (the owner, 12 Nov 2026).
    /// </summary>
    private string? _qrKey;

    /// <summary>The field's text when it was last filled in or applied; an edit back to it is undone.</summary>
    private string _appliedEndpointText = "";

    /// <summary>The field differs from what was last applied.</summary>
    private bool _endpointDirty;

    /// <summary>
    /// A new key was made while the endpoint was being edited, so the code
    /// still carries the old one. Keeps the reload button up even if the edit
    /// is undone, until it is pressed.
    /// </summary>
    private bool _qrKeyStale;

    /// <summary>Edited at all in this window: Tailscale no longer fills the field in.</summary>
    private bool _endpointTouched;

    /// <summary>
    /// The endpoint field (the owner, 12 Nov 2026): Tailscale's endpoint when
    /// it has one, editable whether or not it does. An edit shows the QR
    /// code's reload button, as an edited field in Ammy shows Restart.
    /// </summary>
    public string EndpointText
    {
        get => _endpointText;
        set
        {
            if (!Set(ref _endpointText, value ?? ""))
                return;
            _endpointTouched = true;
            EndpointProblem = null;
            // As in Ammy: typing back what was there undoes the edit, and the
            // button goes, unless a new key is waiting to go into the code.
            var dirty = _endpointText != _appliedEndpointText;
            if (dirty != _endpointDirty)
                Log.Write(dirty ? "[setup] endpoint edited" : "[setup] endpoint edit undone");
            _endpointDirty = dirty;
            Raise(nameof(ShowReload));
            Raise(nameof(QrBlur));
            CopyEndpointCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Fills the field without counting it as an edit.</summary>
    private void FillEndpoint(string? endpoint)
    {
        _qrEndpoint = endpoint;
        _appliedEndpointText = endpoint ?? "";
        if (Set(ref _endpointText, _appliedEndpointText, nameof(EndpointText)))
            CopyEndpointCommand.RaiseCanExecuteChanged();
        UpdateQr();
    }

    public bool ShowReload => _endpointDirty || _qrKeyStale;

    /// <summary>
    /// The key changed (this page's button, or the main window's New key…).
    /// Not mid-edit, the code is redrawn with it at once; mid-edit, the code
    /// waits for the reload button, since the endpoint may not be finished.
    /// </summary>
    private void KeyChanged()
    {
        var key = _host.Settings.Key;
        if (key == _qrKey)
            return;
        if (_qrKey is null)
        {
            // The first look at the key, when the window opens: not a change.
            _qrKey = key;
            UpdateQr();
            return;
        }
        // Mid-edit, or after an edit that was undone while a new key waited:
        // the button stays the only way to redraw (the owner, 12 Nov 2026).
        if (_endpointDirty || _qrKeyStale)
        {
            if (!_qrKeyStale)
                Log.Write("[setup] new key during an endpoint edit: the QR code waits for the reload button");
            _qrKeyStale = true;
            Raise(nameof(ShowReload));
            Raise(nameof(QrBlur));
            return;
        }
        _qrKey = key;
        UpdateQr();
        Log.Write("[setup] QR code redrawn with the new key");
    }

    public string? EndpointProblem { get => _endpointProblem; private set => Set(ref _endpointProblem, value); }
    private string? _endpointProblem;

    public string? KeyProblem { get => _keyProblem; private set => Set(ref _keyProblem, value); }
    private string? _keyProblem;

    /// <summary>
    /// Applies the field: saves it as Settings' Public address (empty when it
    /// is Tailscale's own or blank, so Issun keeps following Tailscale) and
    /// redraws the QR code with it. A blank field leaves the endpoint out of
    /// the code, so the source keeps its own.
    /// </summary>
    private async Task<bool> ApplyEndpointAsync()
    {
        var typed = _endpointText;
        var address = NormaliseAddress(typed);
        if (address is null)
        {
            EndpointProblem = "[text]";
            Log.Write($"[setup] not applying an endpoint of {typed.Trim().Length} characters: not an https address");
            return false;
        }
        var tailscale = _host.Snapshot.Tailscale?.PublicBase?.TrimEnd('/');
        var setting = address == tailscale ? "" : address;
        if (setting != _host.Settings.PublicBase)
        {
            Log.Write(setting.Length == 0
                ? "[setup] endpoint applied: Issun follows Tailscale's address"
                : $"[setup] endpoint applied: a public address of {setting.Length} characters{(setting.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) ? ", a Tailscale name" : "")}");
            await _host.UpdateSettingsAsync(_host.Settings with { PublicBase = setting });
        }
        else
        {
            Log.Write("[setup] endpoint applied: the setting was already that");
        }
        _endpointDirty = false;
        _qrKeyStale = false;
        _qrKey = _host.Settings.Key;
        Raise(nameof(ShowReload));
            Raise(nameof(QrBlur));
        FillEndpoint(address.Length > 0 ? address + "/now-playing" : null);
        Log.Write($"[setup] QR code redrawn {(address.Length > 0 ? "with" : "without")} an endpoint");
        return true;
    }

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

    /// <summary>
    /// Blurred while hidden, and while it needs regenerating (the owner,
    /// 12 Nov 2026): a code that's out of date shouldn't be scanned, and
    /// Regenerate stands where Show was.
    /// </summary>
    public double QrBlur => _qrShown && !ShowReload ? 0 : BlurRadius;

    public BitmapSource? Qr { get => _qr; private set { if (Set(ref _qr, value)) Raise(nameof(HasQr)); } }

    public bool HasQr => _qr is not null;
    private BitmapSource? _qr;

    private string? _qrFor;

    private void UpdateQr()
    {
        // Either half may be missing; the source fills in only what the code
        // carries (the owner, 12 Nov 2026).
        _qrKey ??= _host.Settings.Key;
        var key = _qrKey is { Length: > 0 } k ? k : null;
        if (_qrEndpoint is null && key is null)
        {
            Qr = null;
            _qrFor = null;
            CommandManagerRequery();
            return;
        }
        var payload = PairingCode.Payload(_qrEndpoint, key);
        // White on black in dark mode (the owner, 12 Nov 2026), so the drawn
        // code follows a theme switch too: Refresh runs every second.
        var dark = PairingCode.ThemeIsDark();
        var drawn = (dark ? "dark:" : "light:") + payload;
        if (drawn == _qrFor)
            return;
        try
        {
            Qr = PairingCode.Render(payload, dark);
            if (_qrFor is not null && _qrFor[.._qrFor.IndexOf(':')] != drawn[..drawn.IndexOf(':')])
                Log.Write($"[setup] QR code redrawn for the {(dark ? "dark" : "light")} theme");
            _qrFor = drawn;
            CommandManagerRequery();
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

    /// <summary>
    /// A source is checking in now. Until it is, Connect your source shows a
    /// ProgressRing and the owner's "Waiting...", and Next stays greyed out; then
    /// Windows' success mark and "Connected", and Next works (the owner,
    /// 12 Nov 2026: the check runs by itself, so Next only follows it).
    /// </summary>
    public bool SourceConnected
    {
        get => _sourceConnected;
        private set
        {
            if (!Set(ref _sourceConnected, value))
                return;
            Log.Write(value ? "[setup] Connect your source: connected, Next available" : "[setup] Connect your source: waiting, Next greyed out");
            NextCommand.RaiseCanExecuteChanged();
            Raise(nameof(ShowSkip));
        }
    }
    private bool _sourceConnected;
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
        Raise(nameof(ShowSkip));
        OfferUnattended = s.Tailscale is { Running: true, Unattended: false };

        // Until the field is edited, it follows what Issun knows: Tailscale's
        // endpoint, or the public address already set.
        if (!_endpointTouched && s.EndpointUrl != _qrEndpoint)
            FillEndpoint(s.EndpointUrl);
        Raise(nameof(KeyText));
        KeyChanged();
        UpdateQr();
        Tick();
    }

    private void Tick()
    {
        var s = _snapshot;
        SourceStatus = StatusText.Phone(s, SystemClock.Instance.Now, DateTime.Now);
        SourceConnected = s.LastCheckinAt > 0 && !s.PhoneSilent;
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
        if (_endpointDirty || _qrKeyStale)
            _ = ApplyEndpointAsync();
        Log.Write($"[setup] closed on the {Describe(_page)} page");
    }
}
