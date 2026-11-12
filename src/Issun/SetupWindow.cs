using Issun.Presentation;
using Issun.ViewModels;
using Issun.Views;
using Microsoft.UI.Xaml;

namespace Issun;

/// <summary>
/// The setup window. Opens by itself on a first run, and from the main
/// window's Setup row after that. Closing it, by Cancel, Finish, Escape or the
/// title bar, leaves everything it already applied in place: every page
/// applies as it goes, so there is nothing to roll back.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly SetupViewModel _vm;

    public SetupWindow(SetupViewModel vm)
    {
        _vm = vm;
        Content = new SetupView(vm);
        WindowFit.Prepare(this, "Set up Issun", 600, 600, 520, 480, "setup");
        vm.CloseRequested += Close;
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange || e.DidPresenterChange)
                _vm.SetVisible(WindowFit.IsShowing(this));
        };
        Closed += (_, _) =>
        {
            _vm.CloseRequested -= Close;
            _vm.Dispose();
        };
    }
}
