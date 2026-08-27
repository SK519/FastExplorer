using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using FastExplorer.Core;
using Windows.Graphics.Imaging;

namespace FastExplorer.Services
{
    public partial class IconThumbnailService
    {
        private static readonly ConcurrentDictionary<string, SoftwareBitmap> _extensionIconCache = new(StringComparer.OrdinalIgnoreCase);

        public static SoftwareBitmap? GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID stockIconId, bool large = true)
        {
            try
            {
                var stockInfo = new Win32Interop.SHSTOCKICONINFO();
                stockInfo.cbSize = (uint)Marshal.SizeOf(stockInfo);
                int hr = Win32Interop.SHGetStockIconInfo(
                    stockIconId,
                    Win32Interop.SHGSI_ICON | (large ? Win32Interop.SHGSI_LARGEICON : Win32Interop.SHGSI_SMALLICON),
                    ref stockInfo);

                if (hr == 0 && stockInfo.hIcon != nint.Zero)
                {
                    try
                    {
                        return ConvertHIconToSoftwareBitmap(stockInfo.hIcon);
                    }
                    finally
                    {
                        Win32Interop.DestroyIcon(stockInfo.hIcon);
                    }
                }
            }
            catch { }
            return null;
        }

        public static SoftwareBitmap? GetHomeSoftwareBitmap(int size = 16)
        {
            try
            {
                // 1. imageres.dll,-5301 (Windows 11 Home/Quick Access アイコン)
                string imageres = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "imageres.dll");
                var hIcons = new nint[1];
                var ids = new uint[1];
                uint count = Win32Interop.PrivateExtractIcons(imageres, -5301, size, size, hIcons, ids, 1, 0);
                if (count > 0 && hIcons[0] != nint.Zero)
                {
                    try
                    {
                        return ConvertHIconToSoftwareBitmap(hIcons[0]);
                    }
                    finally
                    {
                        Win32Interop.DestroyIcon(hIcons[0]);
                    }
                }

                // 2. shell32.dll,-51380
                string shell32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
                count = Win32Interop.PrivateExtractIcons(shell32, -51380, size, size, hIcons, ids, 1, 0);
                if (count > 0 && hIcons[0] != nint.Zero)
                {
                    try
                    {
                        return ConvertHIconToSoftwareBitmap(hIcons[0]);
                    }
                    finally
                    {
                        Win32Interop.DestroyIcon(hIcons[0]);
                    }
                }
            }
            catch { }

            return DefaultFolderBitmap;
        }

        public static SoftwareBitmap? ExtractIconFromResource(string dllOrExePath, int iconIndex, int size = 16)
        {
            try
            {
                if (File.Exists(dllOrExePath))
                {
                    var hIcons = new nint[1];
                    var ids = new uint[1];
                    uint count = Win32Interop.PrivateExtractIcons(dllOrExePath, iconIndex, size, size, hIcons, ids, 1, 0);
                    if (count > 0 && hIcons[0] != nint.Zero)
                    {
                        try
                        {
                            return ConvertHIconToSoftwareBitmap(hIcons[0]);
                        }
                        finally
                        {
                            Win32Interop.DestroyIcon(hIcons[0]);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static SoftwareBitmap? GetRecycleBinSoftwareBitmap(bool large = false)
        {
            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_RECYCLER, large);
        }

        public static SoftwareBitmap? GetPcSoftwareBitmap(bool large = false)
        {
            int size = large ? 32 : 16;
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            // 1. imageres.dll,-109 (Windows 11 本物の PC / This PC アイコン)
            var bmp = ExtractIconFromResource(Path.Combine(sysDir, "imageres.dll"), -109, size);
            if (bmp != null) return bmp;

            // 2. imageres.dll,-5306
            bmp = ExtractIconFromResource(Path.Combine(sysDir, "imageres.dll"), -5306, size);
            if (bmp != null) return bmp;

            // 3. shell32.dll,-16
            bmp = ExtractIconFromResource(Path.Combine(sysDir, "shell32.dll"), -16, size);
            if (bmp != null) return bmp;

            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_DESKTOPPC, large) ?? DefaultFolderBitmap;
        }

        public static SoftwareBitmap? GetNetworkSoftwareBitmap(bool large = false)
        {
            int size = large ? 32 : 16;
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            // 1. imageres.dll,-25 (Windows 11 本物の ネットワーク アイコン: 地球儀 + PC)
            var bmp = ExtractIconFromResource(Path.Combine(sysDir, "imageres.dll"), -25, size);
            if (bmp != null) return bmp;

            // 2. imageres.dll,-5322
            bmp = ExtractIconFromResource(Path.Combine(sysDir, "imageres.dll"), -5322, size);
            if (bmp != null) return bmp;

            // 3. shell32.dll,-18
            bmp = ExtractIconFromResource(Path.Combine(sysDir, "shell32.dll"), -18, size);
            if (bmp != null) return bmp;

            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_MYNETWORK, large) ?? DefaultFolderBitmap;
        }

        public static SoftwareBitmap? GetWslSoftwareBitmap(int size = 16)
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            // 1. System32\wsl.exe から Windows 11 の本物の WSL / Linux アイコンを抽出
            var bmp = ExtractIconFromResource(Path.Combine(sysDir, "wsl.exe"), 0, size);
            if (bmp != null) return bmp;

            // 2. imageres.dll,-5324
            bmp = ExtractIconFromResource(Path.Combine(sysDir, "imageres.dll"), -5324, size);
            if (bmp != null) return bmp;

            // 3. shell32.dll,-322
            bmp = ExtractIconFromResource(Path.Combine(sysDir, "shell32.dll"), -322, size);
            if (bmp != null) return bmp;

            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_SERVER, false) ?? DefaultFolderBitmap;
        }

        public static SoftwareBitmap? GetSoftwareBitmapForExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return DefaultFileBitmap;

            if (_extensionIconCache.TryGetValue(extension, out var cached))
            {
                return cached;
            }

            try
            {
                var shinfo = new Win32Interop.SHFILEINFOW();
                uint flags = Win32Interop.SHGFI_USEFILEATTRIBUTES | Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_SMALLICON;
                nint hr = Win32Interop.SHGetFileInfoW(
                    extension,
                    Win32Interop.FILE_ATTRIBUTE_NORMAL,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    flags);

                if (shinfo.hIcon != nint.Zero)
                {
                    try
                    {
                        var bmp = ConvertHIconToSoftwareBitmap(shinfo.hIcon);
                        if (bmp != null)
                        {
                            _extensionIconCache[extension] = bmp;
                            return bmp;
                        }
                    }
                    finally
                    {
                        Win32Interop.DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }

            var fallback = DefaultFileBitmap;
            if (fallback != null)
            {
                _extensionIconCache[extension] = fallback;
            }
            return fallback;
        }

        public static SoftwareBitmap? GetDriveSoftwareBitmap(string drivePath = "C:\\", bool large = false)
        {
            try
            {
                string target = string.IsNullOrEmpty(drivePath) ? "C:\\" : drivePath;
                if (!target.EndsWith('\\') && !target.EndsWith('/')) target += "\\";

                // Windows 標準エクスプローラーと全く同一の 16x16 / 32x32 ピクセル完全一致アイコンを取得
                var shinfo = new Win32Interop.SHFILEINFOW();
                uint flags = Win32Interop.SHGFI_ICON | (large ? Win32Interop.SHGFI_LARGEICON : Win32Interop.SHGFI_SMALLICON);
                Win32Interop.SHGetFileInfoW(
                    target,
                    0,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    flags);

                if (shinfo.hIcon != nint.Zero)
                {
                    try
                    {
                        return ConvertHIconToSoftwareBitmap(shinfo.hIcon);
                    }
                    finally
                    {
                        Win32Interop.DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }

            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_DRIVEFIXED, large);
        }
    }
}
