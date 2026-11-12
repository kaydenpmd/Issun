using System.Runtime.InteropServices;

namespace IssunSpike;

internal static unsafe class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
    public const int DWMWA_CLOAK = 13, DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const uint WM_USER = 0x400, WM_CONTEXTMENU = 0x7B, WM_LBUTTONUP = 0x202, WM_COMMAND = 0x111, WM_NULL = 0, WM_DESTROY = 2;
    public const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;
    public const int SM_CXSMICON = 49, SM_CYSMICON = 50;

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

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER { public uint cbSize; public nint hWnd; public uint uID; public Guid guidItem; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top} {Right - Left}x{Bottom - Top}"; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

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
    [DllImport("user32")] public static extern bool EndMenu();
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint LoadImageW(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? name);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] public static extern nint GetDC(nint hwnd);
    [DllImport("user32")] public static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("shell32")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // Undocumented but what Windows' own Win32 tray menus use to follow dark
    // mode: uxtheme ordinal 135 (SetPreferredAppMode, 1 = AllowDark) and 136
    // (FlushMenuThemes). Present since Windows 10 1903.
    [DllImport("uxtheme", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    [DllImport("gdi32")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] public static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] public static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] public static extern bool DeleteDC(nint dc);
    [DllImport("gdi32")] public static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte* bits, ref BITMAPINFOHEADER bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        public uint pad0, pad1, pad2; // room for a colour table GetDIBits might write
    }

    /// <summary>Screen pixels in a rectangle, as top-down BGRA.</summary>
    public static byte[] CaptureScreen(RECT r)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        SelectObject(mem, old);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bytes = new byte[w * h * 4];
        fixed (byte* p = bytes) GetDIBits(mem, bmp, 0, (uint)h, p, ref bi, 0);
        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(0, screen);
        return bytes;
    }
}
