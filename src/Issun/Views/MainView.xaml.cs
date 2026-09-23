using System.Windows;
using System.Windows.Controls;
using Issun.ViewModels;
using Microsoft.Win32;

namespace Issun.Views;

/// <summary>
/// The window's content, as a control of its own so --screenshot can lay it
/// out and render it without a window ever existing.
///
/// Code-behind holds only what bindings can't do: appending to the log box
/// without losing its scroll position, and the file picker.
/// </summary>
public partial class MainView : UserControl
{
    private MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        LogBox.IsVisibleChanged += (_, _) =>
        {
            // Opening the section lands on the newest line, like a terminal.
            if (LogBox.IsVisible)
                LogBox.ScrollToEnd();
        };
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.LogUpdated -= OnLogUpdated;
            _vm.PickEnvFile = null;
        }
        _vm = vm;
        if (vm is null)
            return;
        vm.LogUpdated += OnLogUpdated;
        vm.PickEnvFile = PickEnvFile;
        LogBox.Text = vm.LogText;
        LogBox.ScrollToEnd();
    }

    private void OnLogUpdated(string appended, bool reload)
    {
        // Follow new lines only if the person was already at the bottom. If they
        // scrolled up to read something, new lines must not yank it away.
        var atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        if (reload)
        {
            var offset = LogBox.VerticalOffset;
            LogBox.Text = _vm?.LogText ?? "";
            if (!atEnd)
                LogBox.ScrollToVerticalOffset(offset);
        }
        else
        {
            LogBox.AppendText(appended);
        }
        if (atEnd)
            LogBox.ScrollToEnd();
    }

    private string? PickEnvFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import relay.py settings",
            // relay.py's .env has no name before the dot, which "*.env" still
            // matches; "All files" is there for a copy saved under another name.
            Filter = "Relay settings (.env)|*.env|All files|*.*",
            CheckFileExists = true,
        };
        var owner = Window.GetWindow(this);
        var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return picked == true ? dialog.FileName : null;
    }
}
