using System.Windows;
using Issun.ViewModels;

namespace Issun;

/// <summary>
/// The window is only a frame for <see cref="Views.MainView"/>. Closing it
/// hides it (App handles Closing); Issun keeps running in the tray.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // The clock-driven text (progress, "last push 12s ago") only ticks
        // while the window can be seen.
        IsVisibleChanged += (_, _) => vm.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) => vm.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
    }
}
