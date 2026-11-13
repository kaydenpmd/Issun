using Issun.Core;
using Issun.Platform;
using Issun.Presentation;
using Issun.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Issun.ViewModels;

/// <summary>
/// Connect your source: the QR code, the endpoint, the key and whether a
/// source is checking in. The setup window's page and the main window's
/// section are the same cards on this one view model (the owner, 12 Nov 2026:
/// "i want parity between the connect your source screen on setup and on the
/// main screen", "favor the setup screen"), so they can't drift apart.
///
/// The endpoint is a field: Tailscale's endpoint until edited, editable either
/// way, and applied by Regenerate or when the owner leaves (the setup page,
/// or the main window closing). Applying saves it as Settings' public address,
/// or clears that when it is Tailscale's own, so Issun keeps following
/// Tailscale. The QR code and the key are blurred until Show.
/// </summary>
public sealed class PairingViewModel : Observable
{
    private readonly IIssunHost _host;
    private readonly string _tag;

    /// <param name="tag">The log lines' tag: "setup" or "ui", the window it's in.</param>
    public PairingViewModel(IIssunHost host, DispatcherQueue dispatcher, string tag)
    {
        _host = host;
        _tag = tag;
        EndpointCopied = new Flash(dispatcher);
        KeyCopied = new Flash(dispatcher);

        CopyEndpointCommand = new AsyncCommand($"{tag}: copy endpoint", async () =>
        {
            if (_endpointText.Trim() is { Length: > 0 } url)
                EndpointCopied.Show(await DesktopShell.CopyAsync(url, "endpoint") ? "Copied" : "Couldn't copy — try again");
        }, _ => EndpointCopied.Show("Couldn't copy — try again"), () => _endpointText.Trim().Length > 0);

        ReloadQrCommand = new AsyncCommand($"{tag}: reload QR code", async () => { await ApplyEndpointAsync(); },
            ex => EndpointProblem = $"[text] {ex.Message}");

        // A new key straight away: the owner asked for the button, not a
        // confirmation (12 Nov 2026, for the setup page, and the main window
        // follows it).
        NewKeyCommand = new AsyncCommand($"{tag}: new key", async () =>
        {
            KeyProblem = null;
            Log.Write($"[{_tag}] making a new key");
            await _host.RegenerateKeyAsync();
            KeyShown = true;
            Raise(nameof(KeyText));
            Raise(nameof(KeyDisplay));
            KeyChanged();
        }, ex =>
        {
            Log.Write($"[{_tag}] couldn't make a new key: {ex.Message}");
            KeyProblem = $"[text] {ex.Message}";
        });

        CopyKeyCommand = new AsyncCommand($"{tag}: copy key", async () =>
        {
            KeyCopied.Show(await DesktopShell.CopyAsync(_host.Settings.Key, "key") ? "Copied" : "Couldn't copy — try again");
        }, _ => KeyCopied.Show("Couldn't copy — try again"));

        // Show and Hide, the owner's words; the same pair for the key and the code.
        ToggleKeyCommand = new Command(() => KeyShown = !KeyShown);
        ToggleQrCommand = new Command(() => QrShown = !QrShown, () => _qrPixels is not null, this);
    }

    public Flash EndpointCopied { get; }
    public Flash KeyCopied { get; }
    public AsyncCommand CopyEndpointCommand { get; }
    public AsyncCommand CopyKeyCommand { get; }
    public Command ToggleKeyCommand { get; }
    public Command ToggleQrCommand { get; }
    public AsyncCommand ReloadQrCommand { get; }
    public AsyncCommand NewKeyCommand { get; }

    /// <summary>Reads the host's state again. UI thread; the owning view model calls it on every refresh.</summary>
    public void Refresh(HostSnapshot s)
    {
        // Until the field is edited, it follows what Issun knows: Tailscale's
        // endpoint, or the public address already set.
        if (!_endpointTouched && s.EndpointUrl != _qrEndpoint)
            FillEndpoint(s.EndpointUrl);
        Raise(nameof(KeyText));
        Raise(nameof(KeyDisplay));
        KeyChanged();
        UpdateQr();
        Tick(s);
    }

    /// <summary>The clock-driven part: whether a source is checking in now. Every second.</summary>
    public void Tick(HostSnapshot s) => SourceConnected = s.LastCheckinAt > 0 && !s.PhoneSilent;

    /// <summary>
    /// A source is checking in now. Until it is, the last card shows a
    /// ProgressRing and the owner's "Waiting..."; then Windows' success mark
    /// and "Connected" (the owner, 12 Nov 2026).
    /// </summary>
    public bool SourceConnected { get => _sourceConnected; private set => Set(ref _sourceConnected, value); }
    private bool _sourceConnected;

    // ───────────────────────────── Endpoint ─────────────────────────────

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

    /// <summary>Edited at all: Tailscale no longer fills the field in.</summary>
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
                Log.Write(dirty ? $"[{_tag}] endpoint edited" : $"[{_tag}] endpoint edit undone");
            _endpointDirty = dirty;
            Raise(nameof(ShowReload));
            Raise(nameof(QrBlurAmount));
        Raise(nameof(QrOpacity));
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

    /// <summary>An edit, or a new key, is waiting to be applied.</summary>
    public bool HasPendingEdit => _endpointDirty || _qrKeyStale;

    /// <summary>Applies a waiting edit, as leaving the page or closing the window does. Never throws; logs.</summary>
    public async Task ApplyPendingAsync()
    {
        if (!HasPendingEdit)
            return;
        try
        {
            await ApplyEndpointAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"[{_tag}] couldn't apply the endpoint: {ex.Message}");
        }
    }

    /// <summary>
    /// The key changed (the New key button, in either window). Not mid-edit,
    /// the code is redrawn with it at once; mid-edit, the code waits for the
    /// reload button, since the endpoint may not be finished.
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
                Log.Write($"[{_tag}] new key during an endpoint edit: the QR code waits for the reload button");
            _qrKeyStale = true;
            Raise(nameof(ShowReload));
            Raise(nameof(QrBlurAmount));
        Raise(nameof(QrOpacity));
            return;
        }
        _qrKey = key;
        UpdateQr();
        Log.Write($"[{_tag}] QR code redrawn with the new key");
    }

    public string? EndpointProblem { get => _endpointProblem; private set => Set(ref _endpointProblem, value); }
    private string? _endpointProblem;

    public string? KeyProblem { get => _keyProblem; private set => Set(ref _keyProblem, value); }
    private string? _keyProblem;

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

    /// <summary>
    /// Applies the field to the QR code: the code is redrawn with it, and a
    /// blank field leaves the endpoint out of the code, so the source keeps its
    /// own. Nothing is saved. Issun's own address is always Tailscale's: the
    /// Public address override that this used to save went on 13 Nov 2026 (the
    /// owner: "clear that publicbase mechanic"), after the old Cloudflare
    /// address it held from relay.py's .env kept showing in setup.
    /// </summary>
    private Task<bool> ApplyEndpointAsync()
    {
        var typed = _endpointText;
        var address = NormaliseAddress(typed);
        if (address is null)
        {
            EndpointProblem = "[text]";
            Log.Write($"[{_tag}] not applying an endpoint of {typed.Trim().Length} characters: not an https address");
            return Task.FromResult(false);
        }
        var tailscale = _host.Snapshot.Tailscale?.PublicBase?.TrimEnd('/');
        Log.Write(address.Length == 0 ? $"[{_tag}] endpoint applied: none, so the code carries only the key"
            : address == tailscale ? $"[{_tag}] endpoint applied: Tailscale's address"
            : $"[{_tag}] endpoint applied: an address of {address.Length} characters, for the QR code only");
        _endpointDirty = false;
        _qrKeyStale = false;
        _qrKey = _host.Settings.Key;
        Raise(nameof(ShowReload));
        Raise(nameof(QrBlurAmount));
        Raise(nameof(QrOpacity));
        FillEndpoint(address.Length > 0 ? address + "/now-playing" : null);
        Log.Write($"[{_tag}] QR code redrawn {(address.Length > 0 ? "with" : "without")} an endpoint");
        return Task.FromResult(true);
    }

    // ───────────────────────────── Key ─────────────────────────────

    public bool KeyShown
    {
        get => _keyShown;
        private set
        {
            if (Set(ref _keyShown, value))
            {
                Raise(nameof(KeyDisplay));
                Raise(nameof(ToggleKeyLabel));
                Log.Write($"[{_tag}] key {(value ? "shown" : "hidden")}");
            }
        }
    }
    private bool _keyShown;

    /// <summary>The key itself, for Copy and the QR code.</summary>
    public string KeyText => _host.Settings.Key;

    /// <summary>
    /// The key as the card shows it: itself after Show, and until then one
    /// ● (U+25CF) per character, the character a Windows PasswordBox hides
    /// its text with (the owner, 13 Nov 2026: "change the key blur to dots";
    /// it was blurred). The QR code keeps its blur.
    /// </summary>
    public string KeyDisplay => _keyShown ? KeyText : new string('●', KeyText.Length);
    public string ToggleKeyLabel => _keyShown ? "Hide" : "Show";

    /// <summary>
    /// MetaMask's blurs, the owner's choice of 13 Nov 2026 ("go metamask
    /// method"), measured in its code (design library,
    /// cross-platform/secret-blur-values): a secret hidden until it's revealed
    /// gets <c>filter: blur(8px)</c> (its recovery phrase and private keys), and
    /// a QR code that has expired gets <c>blur(4px)</c> at 30% opacity. A CSS
    /// blur's length is the Gaussian's standard deviation, as WinUI's
    /// BlurAmount is, so these go in as they are, in DIPs. No system value
    /// exists for this: Windows itself doesn't blur secrets. A blur hides the
    /// code from a glance or a screenshot; it isn't encryption.
    /// </summary>
    private const double HiddenBlur = 8, StaleBlur = 4, StaleOpacity = 0.3;

    // ───────────────────────────── QR code ─────────────────────────────

    /// <summary>
    /// The QR code carries the key, so like the key it is blurred until Show:
    /// a screenshot of the window shouldn't give the key away.
    /// </summary>
    public bool QrShown
    {
        get => _qrShown;
        private set
        {
            if (!Set(ref _qrShown, value))
                return;
            Raise(nameof(ToggleQrLabel));
            Raise(nameof(QrBlurAmount));
        Raise(nameof(QrOpacity));
            Log.Write($"[{_tag}] QR code {(value ? "shown" : "hidden")}");
        }
    }
    private bool _qrShown;

    /// <summary>--screenshot --expanded: draw the QR code too. Only ever on the demo host's made-up key.</summary>
    public void ShowQrForScreenshot() => QrShown = true;

    public string ToggleQrLabel => _qrShown ? "Hide" : "Show";

    /// <summary>
    /// How blurred the code is drawn, as a Gaussian standard deviation in DIPs:
    /// MetaMask's 8 while hidden, its 4 for an expired code while the code
    /// needs regenerating (the owner, 12 Nov 2026: a code that's out of date
    /// shouldn't be scanned, and Regenerate stands where Show was), else
    /// sharp. PairingCards animates between them.
    /// </summary>
    public double QrBlurAmount => !_qrShown ? HiddenBlur : ShowReload ? StaleBlur : 0;

    /// <summary>MetaMask's 30% for an expired code, while the code needs regenerating and isn't hidden.</summary>
    public double QrOpacity => _qrShown && ShowReload ? StaleOpacity : 1;

    public bool HasQr => _qrPixels is not null;

    /// <summary>The code's pixels, always sharp: the blur is drawn over them by the compositor (PairingCards), so the picture is never a copy blurred in advance.</summary>
    public Pixels? QrPixels => _qrPixels;
    private Pixels? _qrPixels;

    /// <summary>
    /// The sharp code as an image, for the Image that holds the code's place
    /// at no opacity, so UI Automation sees a real image there (PairingCards).
    /// Never visible: what's seen is the compositor's blurred copy.
    /// </summary>
    public ImageSource? QrSource { get => _qrSource; private set => Set(ref _qrSource, value); }
    private ImageSource? _qrSource;

    private void SetQrPixels(Pixels? pixels)
    {
        _qrPixels = pixels;
        QrSource = pixels is null ? null : Images.FromPixels(pixels);
        Raise(nameof(HasQr));
        Raise(nameof(QrPixels));
    }

    private string? _qrFor;

    private void UpdateQr()
    {
        // Either half may be missing; the source fills in only what the code
        // carries (the owner, 12 Nov 2026).
        _qrKey ??= _host.Settings.Key;
        var key = _qrKey is { Length: > 0 } k ? k : null;
        if (_qrEndpoint is null && key is null)
        {
            SetQrPixels(null);
            _qrFor = null;
            return;
        }
        var payload = PairingCode.Payload(_qrEndpoint, key);
        // White on black in dark mode (the owner, 12 Nov 2026), so the drawn
        // code follows a theme switch too: Refresh runs on every change.
        var dark = AppTheme.IsDark;
        var drawn = (dark ? "dark:" : "light:") + payload;
        if (drawn == _qrFor)
            return;
        try
        {
            SetQrPixels(PairingCode.Render(payload, dark));
            if (_qrFor is not null && _qrFor[.._qrFor.IndexOf(':')] != drawn[..drawn.IndexOf(':')])
                Log.Write($"[{_tag}] QR code redrawn for the {(dark ? "dark" : "light")} theme");
            _qrFor = drawn;
        }
        catch (Exception ex)
        {
            Log.Write($"[{_tag}] couldn't draw the QR code: {ex.Message}");
            SetQrPixels(null);
        }
    }
}
