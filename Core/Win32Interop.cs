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

        #region Shell Execution & File Operations

        #region Fast Kernel File Copy & Block Cloning

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate uint CopyProgressRoutine(
            long totalFileSize,
            long totalBytesTransferred,
            long streamSize,
            long streamBytesTransferred,
            uint dwStreamNumber,
            uint dwCallbackReason,
            nint hSourceFile,
            nint hDestinationFile,
            nint lpData);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CopyFileExW(
            string lpExistingFileName,
            string lpNewFileName,
            CopyProgressRoutine? lpProgressRoutine,
            nint lpData,
            ref int pbCancel,
            uint dwCopyFlags);

        public const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
        public const uint COPY_FILE_RESTARTABLE = 0x00000002;
        public const uint COPY_FILE_OPEN_AND_COPY_REPARSE_POINT = 0x00000004;
        public const uint COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x00000008;
        public const uint COPY_FILE_COPY_SYMLINK = 0x00000800;
        public const uint COPY_FILE_NO_BUFFERING = 0x00001000;

        public const uint PROGRESS_CONTINUE = 0;
        public const uint PROGRESS_CANCEL = 1;
        public const uint PROGRESS_STOP = 2;
        public const uint PROGRESS_QUIET = 3;

        public const uint CALLBACK_CHUNK_FINISHED = 0x00000000;
        public const uint CALLBACK_STREAM_SWITCH = 0x00000001;

        // ReFS / Dev Drive (Windows 11) Block Cloning (Copy-on-Write)
        [StructLayout(LayoutKind.Sequential)]
        public struct DUPLICATE_EXTENTS_DATA
        {
            public nint FileHandle;
            public long SourceFileOffset;
            public long TargetFileOffset;
            public long ByteCount;
        }

        public const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        public const uint CREATE_ALWAYS = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_BEGIN = 0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref DUPLICATE_EXTENTS_DATA lpInBuffer,
            uint nInBufferSize,
            nint lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            nint lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEndOfFile(Microsoft.Win32.SafeHandles.SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        #endregion

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
        public const uint SEE_MASK_IDLIST = 0x00000004;
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
        public const uint FOFX_REQUIREELEVATION = 0x10000000;
        public const uint CLSCTX_LOCAL_SERVER = 0x4;

        public static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
        public static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");

        [StructLayout(LayoutKind.Sequential)]
        public struct BIND_OPTS3
        {
            public uint cbStruct;
            public uint grfFlags;
            public uint grfMode;
            public uint dwTickCountDeadline;
            public uint dwTrackFlags;
            public uint dwClassContext;
            public uint locale;
            public nint pServerInfo;
            public nint hwnd;
        }

        [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int CoGetObject(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            [In] ref BIND_OPTS3 pBindOptions,
            [In] in Guid riid,
            out nint ppv);

        [ComImport]
        [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
        [ClassInterface(ClassInterfaceType.None)]
        [TypeLibType(TypeLibTypeFlags.FCanCreate)]
        public class FileOperationClass { }

        [ComImport]
        [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
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
        public struct POINT
        {
            public int X;
            public int Y;
        }

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

        #endregion

        [SuppressGCTransition]
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int StrCmpLogicalW(string psz1, string psz2);

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
