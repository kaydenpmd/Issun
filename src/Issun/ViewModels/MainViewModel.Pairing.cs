using Issun.Core;

namespace Issun.ViewModels;

// Connect your source: the same cards, on the same view model, as the setup
// window's page (PairingViewModel), and the Set up again row. Ammy's field is
// called Endpoint rather than Relay on purpose (the app doesn't care what
// receives its pushes), so this window uses the same word.
public sealed partial class MainViewModel
{
    /// <summary>Connect your source's cards: the setup page's own.</summary>
    public PairingViewModel Pairing { get; }

    public Command OpenSetupCommand { get; private set; } = null!;

    /// <summary>Set by the app: opens the setup window, or brings it forward.</summary>
    public Action? OpenSetup { get; set; }

    private void InitPairing()
    {
        OpenSetupCommand = new Command(() =>
        {
            Log.Write("[ui] Open setup clicked");
            OpenSetup?.Invoke();
        });
    }
}
