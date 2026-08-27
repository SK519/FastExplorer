using System;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region Clipboard & Ole

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(nint hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetClipboardData(uint uFormat, nint hMem);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint RegisterClipboardFormatW(string lpszFormat);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalLock(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(nint hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalFree(nint hMem);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint DragQueryFileW(nint hDrop, uint iFile, [Out] System.Text.StringBuilder? lpszFile, uint cch);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("ole32.dll")]
        public static extern int OleInitialize(nint pvReserved);

        [DllImport("ole32.dll")]
        public static extern void OleUninitialize();

        #endregion

        #region Theme & UxTheme

        public enum PreferredAppMode
        {
            Default = 0,
            AllowDark = 1,
            ForceDark = 2,
            ForceLight = 3,
            Max = 4
        }

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        public static extern PreferredAppMode SetPreferredAppMode(PreferredAppMode appMode);

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowDarkModeForWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
        public static extern void FlushMenuThemes();

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SetWindowTheme(nint hWnd, string pszSubAppName, string? pszSubIdList);

        #endregion
    }
}
