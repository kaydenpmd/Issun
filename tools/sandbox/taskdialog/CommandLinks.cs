// A Windows task dialog with command links that carry a note (a second line),
// for measuring how Windows draws them. Built and run inside Windows Sandbox by
// capture-taskdialog.ps1 with the .NET Framework compiler that ships in
// Windows; the manifest beside it turns on Common Controls 6, without which
// TaskDialogIndirect doesn't exist.
using System;
using System.Runtime.InteropServices;

static class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct TASKDIALOG_BUTTON { public int nButtonID; public IntPtr pszButtonText; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct TASKDIALOGCONFIG
    {
        public uint cbSize; public IntPtr hwndParent; public IntPtr hInstance; public int dwFlags;
        public int dwCommonButtons; public string pszWindowTitle; public IntPtr hMainIcon;
        public string pszMainInstruction; public string pszContent; public uint cButtons;
        public IntPtr pButtons; public int nDefaultButton; public uint cRadioButtons; public IntPtr pRadioButtons;
        public int nDefaultRadioButton; public string pszVerificationText; public string pszExpandedInformation;
        public string pszExpandedControlText; public string pszCollapsedControlText; public IntPtr hFooterIcon;
        public string pszFooter; public IntPtr pfCallback; public IntPtr lpCallbackData; public uint cxWidth;
    }

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button, out int radio, out bool verified);

    const int TDF_USE_COMMAND_LINKS = 0x10, TDF_ALLOW_DIALOG_CANCELLATION = 0x8, TDCBF_CANCEL_BUTTON = 0x8;

    [STAThread]
    static void Main()
    {
        string[] texts = {
            "First choice title\nThe note under it, in smaller text, which can run on to a second line when it is long enough to wrap",
            "Second choice title\nA shorter note",
        };
        int size = Marshal.SizeOf(typeof(TASKDIALOG_BUTTON));
        IntPtr buttons = Marshal.AllocHGlobal(size * texts.Length);
        for (int i = 0; i < texts.Length; i++)
        {
            var b = new TASKDIALOG_BUTTON { nButtonID = 100 + i, pszButtonText = Marshal.StringToHGlobalUni(texts[i]) };
            Marshal.StructureToPtr(b, buttons + i * size, false);
        }
        var c = new TASKDIALOGCONFIG
        {
            cbSize = (uint)Marshal.SizeOf(typeof(TASKDIALOGCONFIG)),
            dwFlags = TDF_USE_COMMAND_LINKS | TDF_ALLOW_DIALOG_CANCELLATION,
            dwCommonButtons = TDCBF_CANCEL_BUTTON,
            pszWindowTitle = "Command Links",
            pszMainInstruction = "Main instruction",
            pszContent = "Content text above the command links.",
            cButtons = (uint)texts.Length,
            pButtons = buttons,
        };
        int pressed, radio; bool verified;
        TaskDialogIndirect(ref c, out pressed, out radio, out verified);
    }
}
