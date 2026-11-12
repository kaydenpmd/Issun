using Issun.Presentation;
using Issun.ViewModels;
using Issun.Views;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Issun;

/// <summary>
/// The window is only a frame for <see cref="MainView"/>. Closing it hides it
/// (App handles AppWindow.Closing); Issun keeps running in the tray.
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        var view = new MainView(vm);
        Content = view;
        view.OwnerWindow = WindowNative.GetWindowHandle(this);
        WindowFit.Prepare(this, "Issun", 560, 820, 520, 640, "main");

        // The clock-driven text ("last push 12s ago") only ticks while the
        // window can be seen.
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange || e.DidPresenterChange || e.DidSizeChange)
                vm.SetWindowVisible(WindowFit.IsShowing(this));
        };
    }
}
