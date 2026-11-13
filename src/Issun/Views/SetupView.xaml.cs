using System.ComponentModel;
using Issun.Core;
using Issun.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Issun.Views;

public sealed partial class SetupView : UserControl
{
    private SetupPage _shown;

    public SetupView(SetupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        PairingHost.Content = new PairingCards(viewModel.Pairing);
        // The pages are written in StepStore for x:Bind's sake; the Frame's
        // pages hold them from here on.
        StepStore.Children.Clear();

        // The window opens on its page at once, as windows do; only moving
        // between pages animates.
        _shown = viewModel.Page;
        Pages.Navigate(typeof(SetupStep), StepFor(_shown), new SuppressNavigationTransitionInfo());
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    public SetupViewModel ViewModel { get; }

    private readonly Dictionary<SetupPage, StepSlot> _slots = new();

    private StepSlot StepFor(SetupPage page)
    {
        if (!_slots.TryGetValue(page, out var slot))
        {
            slot = new StepSlot(page switch
            {
                SetupPage.Discord => DiscordStep,
                SetupPage.Tailscale => TailscaleStep,
                SetupPage.Source => SourceStep,
                _ => StartStep,
            });
            _slots[page] = slot;
        }
        return slot;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SetupViewModel.Page) || ViewModel.Page == _shown)
            return;
        // Microsoft's horizontal slide, "to show that sibling pages appear next
        // to each other" (Page transitions, learn.microsoft.com, updated
        // 2026-03-26; the design library's microsoft/windows-page-transitions):
        // a later page comes in from the right, an earlier one from the left.
        // WinUI's own transition, so its timing and curve are Windows', and it
        // stays still when Windows' animation effects are off.
        var forward = ViewModel.Page > _shown;
        var effect = forward ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft;
        Log.Write($"[setup] slide {(forward ? "from the right" : "from the left")} to the {SetupViewModel.Describe(ViewModel.Page)} page");
        _shown = ViewModel.Page;
        Pages.Navigate(typeof(SetupStep), StepFor(_shown), new SlideNavigationTransitionInfo { Effect = effect });
    }
}
