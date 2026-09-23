using Issun.Core;
using Issun.Platform;

namespace Issun.ViewModels;

// The Pair Ammy section: the two values that go into Ammy's Endpoint and Key
// fields, and nothing else. Ammy's field is called Endpoint rather than Relay on
// purpose — the app doesn't care what receives its pushes — so this window
// uses the same word.
public sealed partial class MainViewModel
{
    private const string MaskedKey = "••••••••••••••••";

    public Flash EndpointCopied { get; }
    public Flash KeyCopied { get; }

    public AsyncCommand CopyEndpointCommand { get; private set; } = null!;
    public AsyncCommand CopyKeyCommand { get; private set; } = null!;
    public Command ToggleKeyCommand { get; private set; } = null!;
    public Command AskNewKeyCommand { get; private set; } = null!;
    public Command CancelNewKeyCommand { get; private set; } = null!;
    public AsyncCommand ConfirmNewKeyCommand { get; private set; } = null!;
    public Command DismissBannerCommand { get; private set; } = null!;

    private void InitPairing()
    {
        // A copy that fails says so: pasting whatever was on the clipboard
        // before into Ammy's Key field would look like a wrong key later.
        CopyEndpointCommand = new AsyncCommand("copy endpoint", async () =>
        {
            if (EndpointUrl is { } url)
                EndpointCopied.Show(await DesktopShell.CopyAsync(url, "endpoint") ? "Copied" : "Couldn't copy — try again");
        }, ex => EndpointCopied.Show("Couldn't copy — try again"), () => EndpointUrl is not null);

        CopyKeyCommand = new AsyncCommand("copy key", async () =>
        {
            if (_key.Length > 0)
                KeyCopied.Show(await DesktopShell.CopyAsync(_key, "key") ? "Copied" : "Couldn't copy — try again");
        }, ex => KeyCopied.Show("Couldn't copy — try again"), () => _key.Length > 0);

        ToggleKeyCommand = new Command(() => KeyShown = !KeyShown);
        AskNewKeyCommand = new Command(() => ConfirmingNewKey = true);
        CancelNewKeyCommand = new Command(() => ConfirmingNewKey = false);

        ConfirmNewKeyCommand = new AsyncCommand("New key", async () =>
        {
            KeyMessage = null;
            var key = await _host.RegenerateKeyAsync();
            ConfirmingNewKey = false;
            Key = key;
            // Shown, because the next thing the person does is type it into Ammy.
            KeyShown = true;
            KeyMessage = "New key made. Ammy's pushes are refused until its Key field has this one.";
        }, ex =>
        {
            ConfirmingNewKey = false;
            KeyMessage = $"Couldn't make a new key: {ex.Message}. The old key still works.";
        });

        DismissBannerCommand = new Command(() => ShowFirstRunBanner = false);
        _showFirstRunBanner = _host.FirstRun;
    }

    private void RefreshPairing(HostSnapshot s)
    {
        EndpointUrl = s.EndpointUrl;
        Key = _host.Settings.Key;
    }

    /// <summary>What goes in Ammy's Endpoint field; null until Tailscale (or a manual public address) says what this PC is called.</summary>
    public string? EndpointUrl
    {
        get => _endpointUrl;
        private set
        {
            if (Set(ref _endpointUrl, value))
            {
                Raise(nameof(HasEndpoint));
                CopyEndpointCommand.RaiseCanExecuteChanged();
            }
        }
    }
    private string? _endpointUrl;

    public bool HasEndpoint => _endpointUrl is not null;

    private string Key
    {
        get => _key;
        set
        {
            if (_key == value)
                return;
            _key = value;
            Raise(nameof(KeyDisplay));
            CopyKeyCommand.RaiseCanExecuteChanged();
        }
    }
    private string _key = "";

    /// <summary>
    /// The key, or a fixed row of dots. Fixed rather than one per character, so
    /// a screenshot of the window doesn't give away even the key's length.
    /// </summary>
    public string KeyDisplay => _key.Length == 0 ? "(none yet)" : KeyShown ? _key : MaskedKey;

    public bool KeyShown
    {
        get => _keyShown;
        private set
        {
            if (Set(ref _keyShown, value))
            {
                Raise(nameof(KeyDisplay));
                Raise(nameof(ToggleKeyLabel));
            }
        }
    }
    private bool _keyShown;

    public string ToggleKeyLabel => _keyShown ? "Hide" : "Show";

    public bool ConfirmingNewKey { get => _confirmingNewKey; private set => Set(ref _confirmingNewKey, value); }
    private bool _confirmingNewKey;

    public string? KeyMessage { get => _keyMessage; private set => Set(ref _keyMessage, value); }
    private string? _keyMessage;

    /// <summary>First run only: a key was just generated and nothing has been paired yet.</summary>
    public bool ShowFirstRunBanner { get => _showFirstRunBanner; private set => Set(ref _showFirstRunBanner, value); }
    private bool _showFirstRunBanner;
}
