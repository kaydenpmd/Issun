using System.ComponentModel;
using System.Runtime.InteropServices;
using Issun.Core;
using Issun.ViewModels;
using static Issun.Platform.Native;

namespace Issun.Tray;

/// <summary>
/// Issun's notification-area icon: tooltip with the current track, left-click
/// to open, and a menu with Open, Start with Windows and Quit.
///
/// WinUI 3 has no notification-area control, so this is Shell_NotifyIcon on a
/// hidden top-level window of its own, from the winui3-spike (its report,
/// question 2). Top-level rather than message-only, because the TaskbarCreated
/// broadcast that re-adds the icon after Explorer restarts only reaches
/// top-level windows, and so do WM_QUERYENDSESSION and WM_ENDSESSION, which
/// tell Issun that Windows is signing out or shutting down (WPF's
/// SessionEnding). The menu is a Win32 popup menu with the foreground-window
/// dance that makes it close when you click elsewhere (KB135788), and it
/// follows dark mode, which the WPF build's WinForms menu didn't.
///
/// UI thread only: WinUI's message loop pumps this window's messages too.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_USER + 77;
    private const uint IconId = 1;
    private const int CmdOpen = 1, CmdStartWithWindows = 2, CmdQuit = 3;
    private const string WindowClass = "IssunTray";

    private readonly MainViewModel _vm;
    private readonly WndProc _proc; // kept alive: the window holds a pointer to it
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private readonly Action _open, _quit;
    private readonly Action<bool> _sessionEnding;
    private readonly nint _hwnd;
    private string _tip;
    private bool _disposed;

    /// <param name="sessionEnding">Windows is signing out (false) or shutting down (true).</param>
    public TrayIcon(MainViewModel vm, Action open, Action quit, Action<bool> sessionEnding)
    {
        _vm = vm;
        _open = open;
        _quit = quit;
        _sessionEnding = sessionEnding;
        _proc = WindowProc;
        var inst = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = inst,
            lpszClassName = WindowClass,
        };
        if (RegisterClassExW(ref wc) == 0)
            Log.Write($"[ui] the tray icon's window class wasn't registered (error {Marshal.GetLastWin32Error()})");
        // No title: a window named Issun would be found in place of the main one.
        _hwnd = CreateWindowExW(0, WindowClass, "", 0, 0, 0, 0, 0, 0, 0, inst, 0);
        if (_hwnd == 0)
            Log.Write($"[ui] the tray icon's window wasn't created (error {Marshal.GetLastWin32Error()})");
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // The frame nearest the small-icon size for this screen's scaling,
        // rather than the 256 px one scaled down to mush.
        _icon = LoadAppIcon(GetSystemMetrics(SM_CXSMICON));
        if (_icon == 0)
            Log.Write("[ui] the tray icon image is missing from the build; using the default");

        try
        {
            SetPreferredAppMode(1);
            FlushMenuThemes();
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] the tray menu can't follow dark mode here: {ex.Message}");
        }

        _tip = Clip(vm.TrayTooltip);
        Add();
        _vm.PropertyChanged += OnViewModelChanged;
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        // Shell_NotifyIcon won't show an icon at all without an image, so a
        // missing one falls back to Windows' generic application icon.
        hIcon = _icon != 0 ? _icon : LoadImageW(0, 32512 /* IDI_APPLICATION */, 1, 0, 0, 0x8000 /* LR_SHARED */),
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = 4,
    };

    private void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref d))
        {
            Log.Write("[ui] the tray icon couldn't be added");
            return;
        }
        // Version 4: a click arrives as NIN_SELECT and the menu key as WM_CONTEXTMENU, with the point.
        if (!Shell_NotifyIconW(NIM_SETVERSION, ref d))
            Log.Write("[ui] the tray icon didn't take version 4");
    }

    /// <summary>A notification from the tray icon. Used once, ever: to say that closing the window left Issun running here.</summary>
    public void ShowHint(string title, string text)
    {
        if (_disposed)
            return;
        var d = Data(NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = NIIF_NONE;
        if (!Shell_NotifyIconW(NIM_MODIFY, ref d))
            Log.Write("[ui] couldn't show the tray notification");
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(MainViewModel.TrayTooltip))
            return;
        _tip = Clip(_vm.TrayTooltip);
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    // Presentation.TrayText already clips to the shell's 127 characters; this
    // is the backstop, because the shell's buffer is 128 including the end.
    private static string Clip(string text) =>
        text.Length <= Presentation.TrayText.MaxLength ? text : Presentation.TrayText.Clip(text);

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                Log.Write("[ui] Explorer restarted; adding the tray icon again");
                Add();
                return 0;
            }
            switch (msg)
            {
                case CallbackMessage:
                    var ev = (uint)(lParam & 0xFFFF);
                    int x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF);
                    if (ev == WM_CONTEXTMENU)
                        ShowMenu(x, y);
                    else if (ev is NIN_SELECT or NIN_KEYSELECT)
                        _open();
                    return 0;
                case WM_QUERYENDSESSION:
                    // As WPF's SessionEnding: stop cleanly while there's time,
                    // and let Windows go on (TRUE).
                    _sessionEnding(((long)lParam & ENDSESSION_LOGOFF) == 0);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[ui] tray icon error: {ex.GetType().Name}: {ex.Message}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdOpen, "Open Issun");
        // Bold marks the item a left-click does, the Windows convention.
        SetMenuDefaultItem(menu, CmdOpen, 0);
        // Read at the moment the menu opens, so the tick is never stale.
        AppendMenuW(menu, MF_STRING | (_vm.StartWithWindows ? MF_CHECKED : 0), CmdStartWithWindows, "Start with Windows");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdQuit, "Quit Issun");
        // Without this the menu stays open when you click elsewhere (KB135788).
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN, x, y, _hwnd, 0);
        PostMessageW(_hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
        switch (cmd)
        {
            case CmdOpen:
                _open();
                break;
            case CmdStartWithWindows:
                Log.Write($"[ui] tray menu: Start with Windows {(_vm.StartWithWindows ? "off" : "on")}");
                _vm.StartWithWindows = !_vm.StartWithWindows;
                break;
            case CmdQuit:
                Log.Write("[ui] tray menu: Quit Issun");
                _quit();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _vm.PropertyChanged -= OnViewModelChanged;
        // Removed now, not when the pointer next passes over its ghost.
        var d = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref d);
        DestroyWindow(_hwnd);
        if (_icon != 0)
            DestroyIcon(_icon);
    }
}
