using System.ComponentModel;
using System.Windows;
using Issun.ViewModels;
using Issun.Views;

namespace Issun;

/// <summary>
/// The setup window. Opens by itself on a first run, and from the main
/// window's Setup row after that. Closing it, by Cancel, Close, Escape or the
/// title bar, leaves everything it already applied in place: every page applies
/// as it goes, so there is nothing to roll back.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly SetupViewModel _vm;

    public SetupWindow(SetupViewModel vm)
    {
        _vm = vm;
        Title = "Set up Issun";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Issun;component/Assets/issun.ico"));
        Width = 600;
        Height = 600;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new SetupView { DataContext = vm };
        vm.CloseRequested += Close;
        IsVisibleChanged += (_, _) => _vm.SetVisible(IsVisible);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _vm.CloseRequested -= Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
