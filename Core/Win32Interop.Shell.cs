using System;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region Shell File Info & Associations

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_DISPLAYNAME = 0x000000200;
        public const uint SHGFI_TYPENAME = 0x000000400;
        public const uint SHGFI_ATTRIBUTES = 0x000000800;
        public const uint SHGFI_ICONLOCATION = 0x000001000;
        public const uint SHGFI_EXETYPE = 0x000002000;
        public const uint SHGFI_SYSICONINDEX = 0x000004000;
        public const uint SHGFI_LINKOVERLAY = 0x000008000;
        public const uint SHGFI_SELECTED = 0x000010000;
        public const uint SHGFI_ATTR_SPECIFIED = 0x000020000;
        public const uint SHGFI_LARGEICON = 0x000000000;
        public const uint SHGFI_SMALLICON = 0x000000001;
        public const uint SHGFI_OPENICON = 0x000000002;
        public const uint SHGFI_SHELLICONSIZE = 0x000000004;
        public const uint SHGFI_PIDL = 0x000000008;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFOW
        {
            public nint hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern nint SHGetFileInfoW(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFOW psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        public static extern nint SHGetFileInfoPidl(
            nint pszPath,
            uint dwFileAttributes,
            ref SHFILEINFOW psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int AssocQueryStringW(
            AssocF flags,
            AssocStr str,
            string pszAssoc,
            string? pszExtra,
            [Out] System.Text.StringBuilder pszOut,
            ref uint pcchOut);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHLoadIndirectString(
            string pszSource,
            [Out] System.Text.StringBuilder pszOutBuf,
            uint cchOutBuf,
            nint ppvReserved);

        [Flags]
        public enum AssocF : uint
        {
            None = 0,
            Init_NoRemapCLSID = 0x1,
            Init_ByExeName = 0x2,
            Open_ByExeName = 0x2,
            Init_DefaultToStar = 0x4,
            Init_DefaultToFolder = 0x8,
            NoUserSettings = 0x10,
            NoTruncate = 0x20,
            Verify = 0x40,
            RemapRunDll = 0x80,
            NoFixUps = 0x100,
            IgnoreBaseClass = 0x200,
            Init_IgnoreUnknown = 0x400,
        }

        public enum AssocStr
        {
            Command = 1,
            Executable = 2,
            FriendlyDocName = 3,
            FriendlyAppName = 4,
            NoOpen = 5,
            ShellNewValue = 6,
            DDEExec = 7,
            DDEApplication = 8,
            DDETopic = 9,
            InfoTip = 10,
            QuickTip = 11,
            TileInfo = 12,
            ContentType = 13,
            DefaultIcon = 14,
            ShellExtension = 15,
            DropTarget = 16,
            DelegateExecute = 17,
            SupportedUriProtocols = 18,
            ProgID = 19,
            AppID = 20,
            AppPublisher = 21,
            AppIconReference = 22,
            Max = 23
        }

        [Flags]
        public enum ASSOC_FILTER
        {
            ASSOC_FILTER_NONE = 0,
            ASSOC_FILTER_RECOMMENDED = 1
        }

        [ComImport]
        [Guid("F04061AC-1659-4BB2-AA14-AC87014A433C")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAssocHandler
        {
            [PreserveSig]
            int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);

            [PreserveSig]
            int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);

            [PreserveSig]
            int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);

            [PreserveSig]
            int IsRecommended();

            [PreserveSig]
            int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);

            [PreserveSig]
            int Invoke(nint pdo);

            [PreserveSig]
            int CreateInvoker(nint pdo, out nint ppInvoker);
        }

        [ComImport]
        [Guid("973810F5-9599-4B88-9E4D-6E02BD3104CE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IEnumAssocHandlers
        {
            [PreserveSig]
            int Next(
                uint celt,
                [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] rgelt,
                out uint pceltFetched);

            [PreserveSig]
            int Skip(uint celt);

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int Clone(out IEnumAssocHandlers ppenum);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        public static extern int SHAssocEnumHandlers(
            string pszExtra,
            ASSOC_FILTER afFilter,
            out nint ppenumHandler);

        #endregion

        #region Stock Icons & GDI Helpers

        public enum SHSTOCKICONID : uint
        {
            SIID_DOCNOTASSOC = 0,
            SIID_DOCASSOC = 1,
            SIID_APPLICATION = 2,
            SIID_FOLDER = 3,
            SIID_FOLDEROPEN = 4,
            SIID_DRIVE525 = 5,
            SIID_DRIVE35 = 6,
            SIID_DRIVEREMOVE = 7,
            SIID_DRIVEFIXED = 8,
            SIID_DRIVENET = 9,
            SIID_DRIVENETDISABLED = 10,
            SIID_DRIVECD = 11,
            SIID_DRIVERAM = 12,
            SIID_WORLD = 13,
            SIID_SERVER = 15,
            SIID_PRINTER = 16,
            SIID_MYNETWORK = 17,
            SIID_DESKTOPPC = 18,
            SIID_RECYCLER = 31,
            SIID_RECYCLERFULL = 32,
            SIID_DRIVEBD = 132,
            SIID_USERS = 139,
        }

        public const uint SHGSI_ICON = 0x000000100;
        public const uint SHGSI_LARGEICON = 0x000000000;
        public const uint SHGSI_SMALLICON = 0x000000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public nint hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPath;
        }

        [DllImport("shell32.dll", SetLastError = false)]
        public static extern int SHGetStockIconInfo(
            SHSTOCKICONID siid,
            uint uFlags,
            ref SHSTOCKICONINFO psii);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(nint hIcon);

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public nint hbmMask;
            public nint hbmColor;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public nint bmBits;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int GetObject(nint hgdiobj, int cbBuffer, out BITMAP lpvObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public uint[] bmiColors;
        }

        public const int DIB_RGB_COLORS = 0;
        public const int BI_RGB = 0;
        public const uint DI_NORMAL = 0x0003;

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int GetDIBits(nint hdc, nint hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint SelectObject(nint hdc, nint hgdiobj);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(nint hObject);

        #endregion

        #region Shell COM & Context Menu

        public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
        public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
        public static readonly Guid IID_IContextMenu2 = new("000214f4-0000-0000-c000-000000000046");
        public static readonly Guid IID_IContextMenu3 = new("bcfce0a0-ec14-11d0-b3c0-00a0c90aea82");

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        public interface IShellFolder
        {
            [PreserveSig]
            int ParseDisplayName(
                nint hwnd,
                nint pbc,
                [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten,
                out nint ppidl,
                ref uint pdwAttributes);

            [PreserveSig]
            int EnumObjects(nint hwnd, uint grfFlags, out nint ppenumIDList);

            [PreserveSig]
            int BindToObject(nint pidl, nint pbc, [In] in Guid riid, out nint ppv);

            [PreserveSig]
            int BindToStorage(nint pidl, nint pbc, [In] in Guid riid, out nint ppv);

            [PreserveSig]
            int CompareIDs(nint lParam, nint pidl1, nint pidl2);

            [PreserveSig]
            int CreateViewObject(nint hwndOwner, [In] in Guid riid, out nint ppv);

            [PreserveSig]
            unsafe int GetAttributesOf(uint cidl, nint* apidl, ref uint rgfInOut);

            [PreserveSig]
            unsafe int GetUIObjectOf(
                nint hwndOwner,
                uint cidl,
                nint* apidl,
                [In] in Guid riid,
                nint rgfReserved,
                out nint ppv);

            [PreserveSig]
            int GetDisplayNameOf(nint pidl, uint uFlags, out STRRET pName);

            [PreserveSig]
            int SetNameOf(nint hwnd, nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out nint ppidlOut);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F2-0000-0000-C000-000000000046")]
        public interface IEnumIDList
        {
            [PreserveSig]
            int Next(uint celt, out nint rgelt, out uint pceltFetched);
            [PreserveSig]
            int Skip(uint celt);
            [PreserveSig]
            int Reset();
            [PreserveSig]
            int Clone(out nint ppenum);
        }

        public const uint SHCONTF_FOLDERS = 0x0020;
        public const uint SHCONTF_NONFOLDERS = 0x0040;
        public const uint SHCONTF_INCLUDEHIDDEN = 0x0080;
        public const uint SHCONTF_FASTITEMS = 0x2000;
        public const uint SHCONTF_FLATLIST = 0x4000;

        public const uint SHGDN_NORMAL = 0x0000;
        public const uint SHGDN_INFOLDER = 0x0001;
        public const uint SHGDN_FORPARSING = 0x8000;
        public const uint SHGDN_FORADDRESSBAR = 0x4000;

        public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        public static readonly Guid IID_IDataObject = new("0000010e-0000-0000-c000-000000000046");
        public static readonly Guid BHID_SFUIObject = new("3981e224-f559-4139-b462-39a54045d810");
        public static readonly Guid BHID_SFObject = new("3981e225-f559-4139-b462-39a54045d810");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SHGetPathFromIDListW(nint pidl, [Out] System.Text.StringBuilder pszPath);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int SHBindToObject(
            nint psf,
            nint pidl,
            nint pbc,
            [In] in Guid riid,
            out nint ppv);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int StrRetToBufW(ref STRRET pstr, nint pidl, [Out] System.Text.StringBuilder pszBuf, uint cchBuf);

        [StructLayout(LayoutKind.Explicit, Size = 264)]
        public struct STRRET
        {
            [FieldOffset(0)]
            public uint uType;
            [FieldOffset(4)]
            public nint pOleStr;
            [FieldOffset(4)]
            public uint uOffset;
            [FieldOffset(4)]
            public nint cStr;
        }

        #endregion
    }
}
