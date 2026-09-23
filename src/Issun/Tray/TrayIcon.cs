using System.ComponentModel;
using System.Windows;
using Issun.Core;
using Issun.ViewModels;
using Forms = System.Windows.Forms;

namespace Issun.Tray;

/// <summary>
/// Issun's notification-area icon: tooltip with the current track, left-click
/// to open, and a menu with Open, Start with Windows and Quit.
///
/// Built on WinForms' NotifyIcon rather than a WPF tray package, for three
/// reasons. It ships inside the Windows Desktop runtime Issun already runs on,
/// so there is no third-party dependency to keep current. It re-adds itself
/// when Explorer restarts (TaskbarCreated), which is the tray bug hand-rolled
/// icons usually have. And its ContextMenuStrip does the foreground-window
/// dance that makes a tray menu close when you click elsewhere — the other
/// thing hand-rolled tray menus get wrong. The cost is a menu drawn in WinForms
/// style rather than Fluent, and two global usings removed in Issun.csproj so
/// WPF and WinForms type names don't collide.
///
/// UI thread only. NotifyIcon's messages arrive through WPF's dispatcher, which
/// pumps them like any other window's.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startWithWindows;
    private readonly System.Drawing.Icon? _image;
    private bool _disposed;

    public TrayIcon(MainViewModel vm, Action open, Action quit)
    {
        _vm = vm;
        _image = LoadIcon();

        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open Issun", null, (_, _) => open())
        {
            // Bold marks the item a left-click does, the Windows convention.
            Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
        };
        _startWithWindows = new Forms.ToolStripMenuItem("Start with Windows", null,
            (_, _) => _vm.StartWithWindows = !_vm.StartWithWindows);
        menu.Items.Add(openItem);
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Issun", null, (_, _) => quit()));

        // Read at the moment the menu opens, so the tick is never stale.
        menu.Opening += (_, _) => _startWithWindows.Checked = _vm.StartWithWindows;

        _icon = new Forms.NotifyIcon
        {
            // NotifyIcon won't appear at all without an icon, so a missing
            // image falls back to the generic one rather than to no tray icon.
            Icon = _image ?? System.Drawing.SystemIcons.Application,
            Text = TrayText(vm.TrayTooltip),
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
        _vm.PropertyChanged += OnViewModelChanged;
        _icon.Visible = true;
    }

    /// <summary>A notification from the tray icon. Used once, ever: to say that closing the window left Issun running here.</summary>
    public void ShowHint(string title, string text)
    {
        if (_disposed)
            return;
        try
        {
            _icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't show the tray notification: {ex.Message}");
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;
        if (e.PropertyName == nameof(MainViewModel.TrayTooltip))
            _icon.Text = TrayText(_vm.TrayTooltip);
        else if (e.PropertyName == nameof(MainViewModel.StartWithWindows))
            _startWithWindows.Checked = _vm.StartWithWindows;
    }

    // Presentation.TrayText already clips to the shell's 127 characters; this
    // is the backstop, because NotifyIcon throws on anything longer.
    private static string TrayText(string text) =>
        text.Length <= Presentation.TrayText.MaxLength ? text : Presentation.TrayText.Clip(text);

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/issun.ico"));
            if (resource is null)
            {
                Log.Write("[ui] the tray icon image is missing from the build; using the default");
                return null;
            }
            using var stream = resource.Stream;
            // The frame nearest the small-icon size for this screen's scaling,
            // rather than the 256 px one scaled down to mush.
            return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] couldn't load the tray icon image: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _vm.PropertyChanged -= OnViewModelChanged;
        // Hidden before disposal so the icon leaves the tray now, not when the
        // pointer next passes over its ghost.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image?.Dispose();
    }
}
