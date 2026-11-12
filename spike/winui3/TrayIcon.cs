using System.Runtime.InteropServices;
using static IssunSpike.Native;

namespace IssunSpike;

/// <summary>
/// A notification-area icon for a WinUI 3 app, which has no control for one.
/// Shell_NotifyIcon on a hidden top-level window of its own (not message-only:
/// the TaskbarCreated broadcast that re-adds the icon after Explorer restarts
/// only reaches top-level windows), NOTIFYICON_VERSION_4, and a Win32 popup
/// menu with the foreground-window dance that makes it close when you click
/// elsewhere. The same menu items as Issun's WinForms one. Runs on the UI
/// thread: WinUI's message loop pumps this window's messages too.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = WM_USER + 77;
    public const uint EndMenuMessage = WM_USER + 78;
    private const uint IconId = 1;
    private const int CmdOpen = 1, CmdStartWithWindows = 2, CmdQuit = 3;

    private readonly WndProc _proc; // kept alive: the window holds a pointer to it
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private readonly Action _open, _quit;
    private bool _startWithWindows;

    public nint Hwnd { get; }
    public event Action<string>? MenuOpened;

    public TrayIcon(string tooltip, Action open, Action quit)
    {
        _open = open;
        _quit = quit;
        _proc = WindowProc;
        var inst = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = inst,
            lpszClassName = "IssunSpikeTray",
        };
        if (RegisterClassExW(ref wc) == 0)
            SpikeLog.Write($"[tray] RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        Hwnd = CreateWindowExW(0, "IssunSpikeTray", "IssunSpikeTray", 0, 0, 0, 0, 0, 0, 0, inst, 0);
        SpikeLog.Write($"[tray] hidden window 0x{Hwnd:x}");
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // The .exe's own icon (the SDK embeds ApplicationIcon as resource
        // 32512), at the small-icon size for this screen, so no file is needed
        // beside a single-file .exe.
        _icon = LoadImageW(inst, 32512, 1, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
        SpikeLog.Write(_icon == 0 ? "[tray] the exe's icon resource didn't load" : $"[tray] icon loaded at {GetSystemMetrics(SM_CXSMICON)} px");

        try
        {
            SetPreferredAppMode(1);
            FlushMenuThemes();
            SpikeLog.Write("[tray] menus allowed to follow dark mode (uxtheme #135)");
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"[tray] SetPreferredAppMode unavailable: {ex.Message}");
        }

        Add(tooltip);
    }

    private NOTIFYICONDATAW Data(string tip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = Hwnd,
        uID = IconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = tip.Length > 127 ? tip[..127] : tip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = 4,
    };

    private string _tip = "";

    private void Add(string tip)
    {
        _tip = tip;
        var d = Data(tip);
        var added = Shell_NotifyIconW(NIM_ADD, ref d);
        var versioned = Shell_NotifyIconW(NIM_SETVERSION, ref d);
        SpikeLog.Write($"[tray] NIM_ADD {(added ? "ok" : "FAILED " + Marshal.GetLastWin32Error())}, version 4 {(versioned ? "ok" : "FAILED")}");
    }

    public Native.RECT? IconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = Hwnd, uID = IconId };
        var hr = Shell_NotifyIconGetRect(ref id, out var r);
        SpikeLog.Write(hr == 0 ? $"[tray] icon rect {r}" : $"[tray] Shell_NotifyIconGetRect hr=0x{hr:x8}");
        return hr == 0 ? r : null;
    }

    public void SetTooltip(string tip)
    {
        _tip = tip;
        var d = Data(tip);
        d.uFlags = NIF_TIP | NIF_SHOWTIP;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            SpikeLog.Write("[tray] TaskbarCreated: Explorer restarted, adding the icon again");
            Add(_tip);
            return 0;
        }
        if (msg == CallbackMessage)
        {
            var ev = (uint)(lParam & 0xFFFF);
            int x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF);
            switch (ev)
            {
                case WM_CONTEXTMENU:
                    SpikeLog.Write($"[tray] context menu at {x},{y}");
                    ShowMenu(x, y);
                    break;
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    SpikeLog.Write("[tray] icon clicked: open");
                    _open();
                    break;
            }
            return 0;
        }
        if (msg == EndMenuMessage)
        {
            EndMenu();
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdOpen, "Open Issun");
        SetMenuDefaultItem(menu, CmdOpen, 0); // bold, the Windows convention for what a click does
        AppendMenuW(menu, MF_STRING | (_startWithWindows ? MF_CHECKED : 0), CmdStartWithWindows, "Start with Windows");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdQuit, "Quit Issun");
        // Without this the menu stays open when you click elsewhere (KB135788).
        SetForegroundWindow(Hwnd);
        MenuOpened?.Invoke("menu");
        var cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN, x, y, Hwnd, 0);
        PostMessageW(Hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
        SpikeLog.Write($"[tray] menu closed, command {cmd}");
        switch (cmd)
        {
            case CmdOpen: _open(); break;
            case CmdStartWithWindows: _startWithWindows = !_startWithWindows; SpikeLog.Write($"[tray] start with windows -> {_startWithWindows}"); break;
            case CmdQuit: _quit(); break;
        }
    }

    public void Dispose()
    {
        var d = Data(_tip);
        Shell_NotifyIconW(NIM_DELETE, ref d);
        DestroyWindow(Hwnd);
        if (_icon != 0) DestroyIcon(_icon);
        SpikeLog.Write("[tray] removed");
    }
}
