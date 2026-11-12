using Issun.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Issun.Views;

/// <summary>
/// The window's content, as a control of its own so --screenshot can lay it
/// out and render it in a window that never reaches the screen.
///
/// Code-behind holds only what bindings can't do: appending to the log box
/// without losing its scroll position.
/// </summary>
public sealed partial class MainView : UserControl
{
    private ScrollViewer? _logScroller;

    public MainView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        viewModel.LogUpdated += OnLogUpdated;
        LogBox.Text = viewModel.LogText;
        LogBox.Loaded += (_, _) =>
        {
            // Opening the section lands on the newest line, like a terminal.
            _logScroller = FindScroller(LogBox);
            ScrollLogToEnd();
        };
        Unloaded += (_, _) =>
        {
            viewModel.LogUpdated -= OnLogUpdated;
        };
    }

    public MainViewModel ViewModel { get; }

    private void OnLogUpdated(string appended, bool reload)
    {
        // Follow new lines only if the person was already at the bottom. If they
        // scrolled up to read something, new lines must not yank it away.
        var s = _logScroller;
        var atEnd = s is null || s.VerticalOffset + s.ViewportHeight >= s.ExtentHeight - 4;
        var offset = s?.VerticalOffset ?? 0;
        LogBox.Text = reload ? ViewModel.LogText : LogBox.Text + appended;
        if (atEnd)
            ScrollLogToEnd();
        else
            DispatcherQueue.TryEnqueue(() => s?.ChangeView(null, offset, null, disableAnimation: true));
    }

    private void ScrollLogToEnd() =>
        DispatcherQueue.TryEnqueue(() => _logScroller?.ChangeView(null, _logScroller.ScrollableHeight, null, disableAnimation: true));

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindScroller(child) is { } found)
                return found;
        }
        return null;
    }
}
