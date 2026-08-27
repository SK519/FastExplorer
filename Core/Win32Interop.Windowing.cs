using System;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region Window Management & DWM

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll")]
        public static extern nint SetFocus(nint hWnd);

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;
        public const int SW_SHOWDEFAULT = 10;
        public static readonly nint HWND_TOP = nint.Zero;
        public static readonly nint HWND_TOPMOST = (nint)(-1);
        public static readonly nint HWND_NOTOPMOST = (nint)(-2);
        public const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>
        /// Windows 10/11 のフォアグラウンド制限を突破し、確実にウィンドウを最前面にアクティブ化する
        /// </summary>
        public static void ForceForegroundWindow(nint hWnd)
        {
            if (hWnd == nint.Zero) return;

            try
            {
                nint fgWnd = GetForegroundWindow();
                uint fgThread = fgWnd != nint.Zero ? GetWindowThreadProcessId(fgWnd, out _) : 0;
                uint curThread = GetCurrentThreadId();

                if (fgThread != 0 && fgThread != curThread)
                {
                    AttachThreadInput(curThread, fgThread, true);
                }

                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }
                else
                {
                    ShowWindow(hWnd, SW_SHOW);
                }

                // TOPMOST トグル技法 (Windows OS による最前面化ブロックを確実に回避)
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
                SetFocus(hWnd);

                if (fgThread != 0 && fgThread != curThread)
                {
                    AttachThreadInput(curThread, fgThread, false);
                }
            }
            catch { }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

        public const int DWMWA_TRANSITION_ONOFF = 3;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        public const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

        public static void ApplyImmersiveDarkMode(nint hWnd, bool isDark)
        {
            if (hWnd == nint.Zero) return;
            try
            {
                int darkMode = isDark ? 1 : 0;
                int hr = DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                if (hr != 0)
                {
                    DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }
            }
            catch { }
        }

        public const int GWL_STYLE = -16;
        public const uint WS_CAPTION = 0x00C00000;
        public const uint WS_THICKFRAME = 0x00040000;
        public const uint WS_MINIMIZEBOX = 0x00020000;
        public const uint WS_MAXIMIZEBOX = 0x00010000;
        public const uint WS_SYSMENU = 0x00080000;

        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public const uint WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLongW(nint hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern nint SendMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

        [DllImport("user32.dll")]
        public static extern nint SetCursor(nint hCursor);

        public const int IDC_ARROW = 32512;
        public const int IDC_SIZEWE = 32644;

        public const uint WM_NCLBUTTONDOWN = 0x00A1;
        public const nuint HTCAPTION = 2;
        public const uint WM_SYSCOMMAND = 0x0112;
        public const nuint SC_MOVE = 0xF010;
        public const nuint SC_RESTORE = 0xF120;

        #endregion
    }
}
