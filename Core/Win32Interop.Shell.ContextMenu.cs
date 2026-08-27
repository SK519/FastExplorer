using System;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region Shell Context Menu Interfaces & P/Invokes

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214e4-0000-0000-c000-000000000046")]
        public interface IContextMenu
        {
            [PreserveSig]
            int QueryContextMenu(nint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            [PreserveSig]
            int InvokeCommand(nint pici);

            [PreserveSig]
            unsafe int GetCommandString(nuint idcmd, uint uflags, uint reserved, byte* commandstring, uint cch);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214f4-0000-0000-c000-000000000046")]
        public interface IContextMenu2 : IContextMenu
        {
            [PreserveSig]
            new int QueryContextMenu(nint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            [PreserveSig]
            new int InvokeCommand(nint pici);

            [PreserveSig]
            new unsafe int GetCommandString(nuint idcmd, uint uflags, uint reserved, byte* commandstring, uint cch);

            [PreserveSig]
            int HandleMenuMsg(uint uMsg, nint wParam, nint lParam);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("bcfce0a0-ec14-11d0-b3c0-00a0c90aea82")]
        public interface IContextMenu3 : IContextMenu2
        {
            [PreserveSig]
            new int QueryContextMenu(nint hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

            [PreserveSig]
            new int InvokeCommand(nint pici);

            [PreserveSig]
            new unsafe int GetCommandString(nuint idcmd, uint uflags, uint reserved, byte* commandstring, uint cch);

            [PreserveSig]
            new int HandleMenuMsg(uint uMsg, nint wParam, nint lParam);

            [PreserveSig]
            int HandleMenuMsg2(uint uMsg, nint wParam, nint lParam, out nint plResult);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CMINVOKECOMMANDINFOEX
        {
            public uint cbSize;
            public uint fMask;
            public nint hwnd;
            public nint lpVerb;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public nint hIcon;
            public string? lpTitle;
            public nint lpVerbW;
            public string? lpParametersW;
            public string? lpDirectoryW;
            public string? lpTitleW;
            public POINT ptInvoke;
        }

        public const uint CMIC_MASK_UNICODE = 0x00004000;
        public const uint CMIC_MASK_PTINVOKE = 0x20000000;
        public const uint CMIC_MASK_FLAG_NO_UI = 0x00000400;

        public const uint CMF_NORMAL = 0x00000000;
        public const uint CMF_DEFAULTONLY = 0x00000001;
        public const uint CMF_VERBSONLY = 0x00000002;
        public const uint CMF_EXPLORE = 0x00000004;
        public const uint CMF_NOVERBS = 0x00000008;
        public const uint CMF_CANRENAME = 0x00000010;
        public const uint CMF_NODEFAULT = 0x00000020;
        public const uint CMF_INCLUDEBYNAME = 0x00000040;
        public const uint CMF_EXTENDEDVERBS = 0x00000100;

        public const uint TPM_LEFTBUTTON = 0x0000;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_LEFTALIGN = 0x0000;
        public const uint TPM_TOPALIGN = 0x0000;
        public const uint TPM_RETURNCMD = 0x0100;

        public const uint WM_INITMENUPOPUP = 0x0117;
        public const uint WM_MEASUREITEM = 0x002C;
        public const uint WM_DRAWITEM = 0x002B;
        public const uint WM_MENUCHAR = 0x0120;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEFCONTEXTMENU
        {
            public nint hwnd;
            public nint pcmcb;
            public nint pidlFolder;
            public nint psf;
            public uint cidl;
            public nint apidl;
            public nint punkAssociationInfo;
            public uint cKeys;
            public nint aKeys;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int SHCreateDefaultContextMenu(
            ref DEFCONTEXTMENU pdcm,
            [In] in Guid riid,
            out nint ppv);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(nint hMenu);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            nint hWndParent,
            nint hMenu,
            nint hInstance,
            nint lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lpTPMParams);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

        public const uint MF_BYPOSITION = 0x0400;

        [DllImport("user32.dll")]
        public static extern int GetMenuItemCount(nint hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetMenuString(nint hMenu, uint uIDItem, [Out] System.Text.StringBuilder lpString, int nMaxCount, uint uFlag);

        [DllImport("user32.dll")]
        public static extern uint GetMenuItemID(nint hMenu, int nPos);

        [DllImport("user32.dll")]
        public static extern nint GetSubMenu(nint hMenu, int nPos);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern nint WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern nint GetAncestor(nint hwnd, uint gaFlags);

        [DllImport("shell32.dll", ExactSpelling = true)]
        public static extern void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);

        public static readonly Guid IID_IDataTransferManagerInterop = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
        public static readonly Guid IID_DataTransferManager = new("a5caee9b-8708-49d1-8d36-67d25a8da00e");

        [ComImport]
        [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDataTransferManagerInterop
        {
            [PreserveSig]
            int GetForWindow(nint appWindow, [In] in Guid riid, out nint dataTransferManager);

            [PreserveSig]
            int ShowShareUIForWindow(nint appWindow);
        }

        #endregion
    }
}
