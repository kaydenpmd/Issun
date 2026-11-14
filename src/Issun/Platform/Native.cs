using System.Runtime.InteropServices;

namespace Issun.Platform;

/// <summary>
/// The Win32 calls the WinUI build makes itself: the tray icon (WinUI has no
/// control for one), Windows' own message boxes, and
/// the window styles that keep a --screenshot window off the screen. Most of
/// it is the winui3-spike's Native.cs.
/// </summary>
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CLOAK = 13;
    public const uint WM_NULL = 0, WM_USER = 0x400, WM_CONTEXTMENU = 0x7B, WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16;
    public const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NIIF_NONE = 0;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;
    public const int SM_CXSMICON = 49, SM_CYSMICON = 50, SM_CXICON = 11, SM_CYICON = 12;
    public const long ENDSESSION_LOGOFF = 0x80000000;

    // MessageBox: the same flags WPF's MessageBox.Show passed for the same choices.
    public const uint MB_OK = 0, MB_YESNO = 4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30,
        MB_ICONINFORMATION = 0x40, MB_DEFBUTTON2 = 0x100, MB_SETFOREGROUND = 0x10000;
    public const int IDYES = 6;

    /// <summary>The .exe's icon: the SDK embeds ApplicationIcon as resource 32512.</summary>
    public const int AppIconResource = 32512;

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32")] public static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32")] public static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPos);
    [DllImport("user32")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32")] public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32")] public static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    public const uint WM_SETICON = 0x80;
    public const nint ICON_SMALL = 0, ICON_BIG = 1;
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint LoadImageW(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // Undocumented but what Windows' own Win32 tray menus use to follow dark
    // mode: uxtheme ordinal 135 (SetPreferredAppMode, 1 = AllowDark) and 136
    // (FlushMenuThemes). Present since Windows 10 1903.
    [DllImport("uxtheme", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    /// <summary>The .exe's own icon at a size, or 0. No file needed beside a single-file .exe.</summary>
    public static nint LoadAppIcon(int size) =>
        LoadImageW(GetModuleHandleW(null), AppIconResource, 1 /* IMAGE_ICON */, size, size, 0);

    [DllImport("user32")] private static extern unsafe nint CreateIconFromResourceEx(byte* data, uint size, bool icon, uint version, int cx, int cy, uint flags);

    /// <summary>
    /// The tray's own icon at a size, or 0: the frame of Assets\tray.ico that
    /// matches, else the smallest larger one, else the largest. Discord's tray
    /// icon is its logomark alone, not its app icon, and Issun's follows it.
    /// </summary>
    public static unsafe nint LoadTrayIcon(int size, out int framePx)
    {
        framePx = 0;
        using var s = typeof(Native).Assembly.GetManifestResourceStream("Issun.tray.ico");
        if (s is null)
            return 0;
        var b = new byte[s.Length];
        s.ReadExactly(b);
        int count = BitConverter.ToUInt16(b, 4), best = -1, bestRank = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var px = b[6 + 16 * i] == 0 ? 256 : b[6 + 16 * i];
            var rank = px >= size ? px - size : 1000 + size - px;
            if (rank < bestRank)
                (best, bestRank, framePx) = (i, rank, px);
        }
        if (best < 0)
            return 0;
        var length = BitConverter.ToInt32(b, 6 + 16 * best + 8);
        var offset = BitConverter.ToInt32(b, 6 + 16 * best + 12);
        fixed (byte* p = b)
            return CreateIconFromResourceEx(p + offset, (uint)length, true, 0x00030000, size, size, 0);
    }

    /// <summary>
    /// SM_CXICON or SM_CXSMICON at the window's own DPI. Plain GetSystemMetrics
    /// gave Issun 32 at 150%, the 100% size, and the taskbar then drew the 32 px
    /// icon as it was, 4 px smaller each way than Apple Music's 36 beside it
    /// (seen 12 Nov 2026).
    /// </summary>
    public static int IconSize(nint hwnd, int metric)
    {
        var dpi = hwnd != 0 ? GetDpiForWindow(hwnd) : 0;
        return dpi != 0 ? GetSystemMetricsForDpi(metric, dpi) : GetSystemMetrics(metric);
    }
}
