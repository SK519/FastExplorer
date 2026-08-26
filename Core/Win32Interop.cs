using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region File System Attributes & Find Files

        public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
        public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_ARCHIVE = 0x00000020;
        public const uint FILE_ATTRIBUTE_DEVICE = 0x00000040;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        public const uint FILE_ATTRIBUTE_TEMPORARY = 0x00000100;
        public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        public const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
        public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        public const uint FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 0x00002000;
        public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x00004000;

        public const int FIND_FIRST_EX_CASE_SENSITIVE = 1;
        public const int FIND_FIRST_EX_LARGE_FETCH = 2;
        public const int FIND_FIRST_EX_ON_DISK_ENTRIES_ONLY = 4;

        public enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard = 0,
            FindExInfoBasic = 1,
            FindExInfoMaxInfoLevel
        }

        public enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch = 0,
            FindExSearchLimitToDirectories = 1,
            FindExSearchLimitToDevices = 2,
            FindExSearchMaxSearchOp
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public unsafe struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            public fixed char cFileNameBuffer[260];
            public fixed char cAlternateFileNameBuffer[14];

            public readonly ReadOnlySpan<char> FileNameSpan
            {
                get
                {
                    fixed (char* ptr = cFileNameBuffer)
                    {
                        int len = 0;
                        while (len < 260 && ptr[len] != '\0') len++;
                        return new ReadOnlySpan<char>(ptr, len);
                    }
                }
            }

            public readonly string cFileName => FileNameSpan.ToString();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint FindFirstFileExW(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATAW lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            nint lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(nint hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(nint hFindFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetLogicalDriveStringsW(uint nBufferLength, [Out] char[] lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetDriveTypeW(string lpRootPathName);

        public const uint DRIVE_UNKNOWN = 0;
        public const uint DRIVE_NO_ROOT_DIR = 1;
        public const uint DRIVE_REMOVABLE = 2;
        public const uint DRIVE_FIXED = 3;
        public const uint DRIVE_REMOTE = 4;
        public const uint DRIVE_CDROM = 5;
        public const uint DRIVE_RAMDISK = 6;

        #endregion

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

        #region Shell Execution & File Operations

        public const string CLSID_RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        public const string Shell_RecycleBinFolder = "shell:RecycleBinFolder";

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        public const uint SHERB_NOCONFIRMATION = 0x00000001;
        public const uint SHERB_NOPROGRESSUI = 0x00000002;
        public const uint SHERB_NOSOUND = 0x00000004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFOW
        {
            public int cbSize;
            public uint fMask;
            public nint hwnd;
            public string? lpVerb;
            public string? lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public nint hInstApp;
            public nint lpIDList;
            public string? lpClass;
            public nint hkeyClass;
            public uint dwHotKey;
            public nint hIconOrMonitor;
            public nint hProcess;
        }

        public const int SW_SHOWNORMAL = 1;
        public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW pExecInfo);

        public const uint FO_MOVE = 0x0001;
        public const uint FO_COPY = 0x0002;
        public const uint FO_DELETE = 0x0003;
        public const uint FO_RENAME = 0x0004;

        public const ushort FOF_MULTIDESTFILES = 0x0001;
        public const ushort FOF_CONFIRMMOUSE = 0x0002;
        public const ushort FOF_SILENT = 0x0004;
        public const ushort FOF_RENAMEONCOLLISION = 0x0008;
        public const ushort FOF_NOCONFIRMATION = 0x0010;
        public const ushort FOF_WANTMAPPINGHANDLE = 0x0020;
        public const ushort FOF_ALLOWUNDO = 0x0040;
        public const ushort FOF_FILESONLY = 0x0080;
        public const ushort FOF_SIMPLEPROGRESS = 0x0100;
        public const ushort FOF_NOCONFIRMMKDIR = 0x0200;
        public const ushort FOF_NOERRORUI = 0x0400;
        public const ushort FOF_NOCOPYSECURITYATTRIBS = 0x0800;
        public const ushort FOF_NORECURSION = 0x1000;
        public const ushort FOF_NO_CONNECTED_ELEMENTS = 0x2000;
        public const ushort FOF_WANTNUKEWARNING = 0x4000;
        public const ushort FOF_NORECURSEREPARSE = 0x8000;

        public const uint FOFX_NOSKIPJUNCTIONS = 0x00010000;
        public const uint FOFX_PREFERHARDLINK = 0x00020000;
        public const uint FOFX_SHOWELEVATIONPROMPT = 0x00040000;
        public const uint FOFX_RECYCLEONDELETE = 0x00080000;
        public const uint FOFX_EARLYFAILURE = 0x00100000;
        public const uint FOFX_PRESERVEFILEEXTENSIONS = 0x00200000;
        public const uint FOFX_KEEPNEWERFILE = 0x00400000;
        public const uint FOFX_NOCOPYHOOKS = 0x00800000;
        public const uint FOFX_NOMINIMIZEBOX = 0x01000000;
        public const uint FOFX_MOVEACROSSVOLUMES = 0x02000000;
        public const uint FOFX_DONTRENAMEEXISTING = 0x04000000;

        [ComImport]
        [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
        [ClassInterface(ClassInterfaceType.None)]
        [TypeLibType(TypeLibTypeFlags.FCanCreate)]
        public class FileOperationClass { }

        [ComImport]
        [Guid("947aab5f-0a4c-449b-ac4b-92b60705e26b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFileOperation
        {
            uint Advise(nint pfops, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOperationFlags(uint dwOperationFlags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
            void SetProgressDialog(nint popd);
            void SetProperties(nint pproparray);
            void SetOwnerWindow(nint hwndOwner);
            void ApplyPropertiesToItem(nint psi);
            void ApplyPropertiesToItems(nint punkItems);
            void RenameItem(nint psi, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, nint pfopsItem);
            void RenameItems(nint pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            void MoveItem(nint psi, nint psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, nint pfopsItem);
            void MoveItems(nint punkItems, nint psiDestinationFolder);
            void CopyItem(nint psi, nint psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, nint pfopsItem);
            void CopyItems(nint punkItems, nint psiDestinationFolder);
            void DeleteItem(nint psi, nint pfopsItem);
            void DeleteItems(nint punkItems);
            void NewItem(nint psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, nint pfopsItem);
            void PerformOperations();
            [return: MarshalAs(UnmanagedType.Bool)]
            bool GetAnyOperationsAborted();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCTW
        {
            public nint hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public nint hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public nint hwnd;
            public string? lpVerb;
            public string? lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public nint hInstApp;
            public nint lpIDList;
            public string? lpClass;
            public nint hkeyClass;
            public uint dwHotKey;
            public nint hIcon;
            public nint hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            nint pbc,
            out nint ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int SHBindToParent(
            nint pidl,
            [In] in Guid riid,
            out nint ppv,
            out nint ppidlLast);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int SHGetDesktopFolder(out nint ppshf);

        [DllImport("shell32.dll")]
        public static extern void ILFree(nint pidl);

        [DllImport("shell32.dll")]
        public static extern nint ILCombine(nint pidl1, nint pidl2);

        [DllImport("shell32.dll")]
        public static extern nint ILFindLastID(nint pidl);

        #region Shell Item Image Factory (Thumbnails & High-DPI Icons)

        [Flags]
        public enum SIIGBF
        {
            SIIGBF_RESIZETOFIT = 0x00,
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_MEMORYONLY = 0x02,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_INCACHEONLY = 0x10,
            SIIGBF_CROPTOSQUARE = 0x20,
            SIIGBF_WIDETHUMBNAILS = 0x40,
            SIIGBF_ICONBACKGROUND = 0x80,
            SIIGBF_SCALEUP = 0x100
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;

            public SIZE(int cx, int cy)
            {
                this.cx = cx;
                this.cy = cy;
            }
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(
                [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
                [In] SIIGBF flags,
                [Out] out nint phbm);
        }

        public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            nint pbc,
            [In] in Guid riid,
            out nint ppv);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint PrivateExtractIcons(
            string szFileName,
            int nIconIndex,
            int cxIcon,
            int cyIcon,
            nint[] phicon,
            uint[] piconid,
            uint nIcons,
            uint flags);

        #endregion

        #endregion

        #region Clipboard, Memory, Subclassing & UxTheme

        public const uint CF_TEXT = 1;
        public const uint CF_UNICODETEXT = 13;
        public const uint CF_HDROP = 15;

        public const uint GMEM_MOVEABLE = 0x0002;
        public const uint GMEM_ZEROINIT = 0x0040;
        public const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

        [StructLayout(LayoutKind.Sequential)]
        public struct DROPFILES
        {
            public uint pFiles;
            public POINT pt;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fNC;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fWide;
        }

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

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

        [DllImport("combase.dll", SetLastError = true)]
        public static extern int RoGetActivationFactory(
            [MarshalAs(UnmanagedType.HString)] string activatableClassId,
            [In] in Guid iid,
            out nint factory);

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

        #region Window Subclassing & Windows Hooks

        public const int WH_MOUSE_LL = 14;
        public const uint WM_MOUSEWHEEL = 0x020A;
        public const uint WM_XBUTTONDOWN = 0x020B;
        public const uint WM_XBUTTONUP = 0x020C;
        public const uint WM_APPCOMMAND = 0x0319;
        public const int APPCOMMAND_BROWSER_BACKWARD = 1;
        public const int APPCOMMAND_BROWSER_FORWARD = 2;
        public const int XBUTTON1 = 0x0001;
        public const int XBUTTON2 = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public nuint dwExtraInfo;
        }

        public delegate nint LowLevelMouseProc(int nCode, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

        public delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

        [SuppressGCTransition]
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int StrCmpLogicalW(string psz1, string psz2);

        #region Global HotKeys & Low-Level Keyboard Hook

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint WM_HOTKEY = 0x0312;
        public const uint VK_E = 0x45;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        public const int WH_KEYBOARD_LL = 13;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_SYSKEYDOWN = 0x0104;
        public const uint WM_SYSKEYUP = 0x0105;

        public delegate nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public nuint dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(nint hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint GetModuleHandle(string? lpModuleName);

        #endregion

        #endregion

        #region Utilities

        public static DateTime ToDateTime(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
        {
            long high = fileTime.dwHighDateTime;
            long low = (uint)fileTime.dwLowDateTime;
            long fileTimeLong = (high << 32) | low;
            if (fileTimeLong <= 0) return DateTime.MinValue;
            try
            {
                return DateTime.FromFileTimeUtc(fileTimeLong).ToLocalTime();
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        #endregion
    }
}
