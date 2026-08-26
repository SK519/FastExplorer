using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using FastExplorer.Core;
using FastExplorer.Models;
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

        public static SoftwareBitmap? GetHomeSoftwareBitmap(int size = 32)
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

        public static SoftwareBitmap? ExtractIconFromResource(string dllOrExePath, int iconIndex, int size = 32)
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

        public static SoftwareBitmap? GetRecycleBinSoftwareBitmap(bool large = true)
        {
            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_RECYCLER, large);
        }

        public static SoftwareBitmap? GetPcSoftwareBitmap(bool large = true)
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

        public static SoftwareBitmap? GetNetworkSoftwareBitmap(bool large = true)
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

        public static SoftwareBitmap? GetWslSoftwareBitmap(int size = 32)
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

            return GetStockIconSoftwareBitmap(Win32Interop.SHSTOCKICONID.SIID_SERVER, true) ?? DefaultFolderBitmap;
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
                uint flags = Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_LARGEICON | Win32Interop.SHGFI_USEFILEATTRIBUTES;

                Win32Interop.SHGetFileInfoW(
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

        private static readonly HashSet<string> FastImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif", ".heic", ".raw", ".cr2", ".nef", ".arw", ".dng"
        };

        private static readonly HashSet<string> MediaPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // 画像
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico", ".svg", ".tiff", ".tif",
            ".heic", ".heif", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd",
            // 動画
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".3gp",
            // ドキュメント
            ".pdf"
        };

        private static readonly HashSet<string> SpecialImageFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Pictures", "ピクチャ", "Photos", "Screenshots", "スクリーンショット", "Camera Roll", "カメラ ロール"
        };

        private static bool FolderContainsImages(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return false;

                // 特殊な「ピクチャ」フォルダー等の場合はプレビュー許可
                string folderName = Path.GetFileName(folderPath);
                if (SpecialImageFolderNames.Contains(folderName))
                {
                    return true;
                }

                // 高速に最初の数ファイルを走査して画像が含まれているかチェック
                string searchPattern = Path.Combine(folderPath, "*.*");
                var findData = new Win32Interop.WIN32_FIND_DATAW();
                nint hFind = Win32Interop.FindFirstFileExW(
                    searchPattern,
                    Win32Interop.FINDEX_INFO_LEVELS.FindExInfoBasic,
                    out findData,
                    Win32Interop.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                    nint.Zero,
                    Win32Interop.FIND_FIRST_EX_LARGE_FETCH);

                if (hFind != nint.Zero && hFind != -1)
                {
                    try
                    {
                        int checkCount = 0;
                        do
                        {
                            ReadOnlySpan<char> nameSpan = findData.FileNameSpan;
                            if (nameSpan.Length == 1 && nameSpan[0] == '.') continue;
                            if (nameSpan.Length == 2 && nameSpan[0] == '.' && nameSpan[1] == '.') continue;

                            if ((findData.dwFileAttributes & Win32Interop.FILE_ATTRIBUTE_DIRECTORY) == 0)
                            {
                                ReadOnlySpan<char> extSpan = Path.GetExtension(nameSpan);
                                string ext = extSpan.ToString();
                                if (ImageExtensions.Contains(ext))
                                {
                                    return true;
                                }
                            }
                            checkCount++;
                            if (checkCount > 15) break;
                        } while (Win32Interop.FindNextFileW(hFind, out findData));
                    }
                    finally
                    {
                        Win32Interop.FindClose(hFind);
                    }
                }
            }
            catch
            {
                // ignored
            }
            return false;
        }

        private static readonly HashSet<string> CustomIconExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".ico", ".cur", ".ani", ".url", ".appref-ms"
        };

        private static SoftwareBitmap? ExtractIconAsSoftwareBitmap(FileItem item)
        {
            try
            {
                if (item.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    return GetHomeSoftwareBitmap(32);
                }

                if (RecycleBinService.IsRecycleBinPath(item.FullPath))
                {
                    return GetRecycleBinSoftwareBitmap(true);
                }

                if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    return GetPcSoftwareBitmap(true);
                }

                if (item.FullPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || item.FullPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
                {
                    return GetNetworkSoftwareBitmap(true);
                }

                if (IsWslRootPath(item.FullPath, item.Name))
                {
                    return GetWslSoftwareBitmap(32);
                }

                // 1. サムネイルプレビューの抽出（画像向きモード ＋ メディアファイル または 画像含有フォルダ）
                bool shouldTryThumbnail = false;
                if (!item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) && item.AllowThumbnail)
                {
                    if (item.IsDirectory)
                    {
                        shouldTryThumbnail = FolderContainsImages(item.FullPath);
                    }
                    else
                    {
                        shouldTryThumbnail = MediaPreviewExtensions.Contains(item.Extension);
                    }
                }

                if (shouldTryThumbnail)
                {
                    // 高速画像（JPG, PNG, BMP, WEBP, GIF）は SkiaSharp によるダイレクト高速デコードを最優先
                    if (!item.IsDirectory && FastImageExtensions.Contains(item.Extension))
                    {
                        var skiaThumb = ExtractSkiaImageThumbnail(item.FullPath, 96);
                        if (skiaThumb != null)
                        {
                            return skiaThumb;
                        }
                    }

                    // ShellItem ImageFactory による抽出
                    var thumb = ExtractThumbnailViaShellItem(item.FullPath, 96);
                    if (thumb != null)
                    {
                        return thumb;
                    }

                    // WIC フォールバック
                    if (!item.IsDirectory && ImageExtensions.Contains(item.Extension))
                    {
                        var directThumb = ExtractDirectImageThumbnail(item.FullPath, 96);
                        if (directThumb != null)
                        {
                            return directThumb;
                        }
                    }
                }

                // 2. 固有アイコン（フォルダー、ドライブ、.exe / .lnk / .ico 等）を SHGetFileInfo で抽出
                bool isSpecialOrCustomIcon = item.IsDirectory ||
                                            (item.FullPath.Length <= 3 && item.FullPath.Contains(':')) ||
                                            string.IsNullOrEmpty(item.Extension) ||
                                            CustomIconExtensions.Contains(item.Extension);

                if (isSpecialOrCustomIcon)
                {
                    var shinfo = new Win32Interop.SHFILEINFOW();
                    uint flags = Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_LARGEICON;
                    if (item.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        flags |= Win32Interop.SHGFI_LINKOVERLAY;
                    }

                    if (item.IsDirectory)
                    {
                        if (Directory.Exists(item.FullPath) || (item.FullPath.Length <= 3 && item.FullPath.Contains(':')))
                        {
                            Win32Interop.SHGetFileInfoW(
                                item.FullPath,
                                Win32Interop.FILE_ATTRIBUTE_DIRECTORY,
                                ref shinfo,
                                (uint)Marshal.SizeOf(shinfo),
                                flags);
                        }
                        else
                        {
                            flags |= Win32Interop.SHGFI_USEFILEATTRIBUTES;
                            Win32Interop.SHGetFileInfoW(
                                "folder",
                                Win32Interop.FILE_ATTRIBUTE_DIRECTORY,
                                ref shinfo,
                                (uint)Marshal.SizeOf(shinfo),
                                flags);
                        }
                    }
                    else
                    {
                        if (File.Exists(item.FullPath))
                        {
                            flags = Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_LARGEICON;
                            if (item.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                flags |= Win32Interop.SHGFI_LINKOVERLAY;
                            }
                        }
                        else
                        {
                            flags |= Win32Interop.SHGFI_USEFILEATTRIBUTES;
                        }

                        Win32Interop.SHGetFileInfoW(
                            item.FullPath,
                            Win32Interop.FILE_ATTRIBUTE_NORMAL,
                            ref shinfo,
                            (uint)Marshal.SizeOf(shinfo),
                            flags);
                    }

                    if (shinfo.hIcon != nint.Zero)
                    {
                        try
                        {
                            var bmp = ConvertHIconToSoftwareBitmap(shinfo.hIcon);
                            if (bmp != null) return bmp;
                        }
                        finally
                        {
                            Win32Interop.DestroyIcon(shinfo.hIcon);
                        }
                    }
                }

                // 3. 一般ファイルは拡張子キャッシュから即時返却（重いシェルCOM呼出を完全に回避）
                if (item.IsDirectory)
                {
                    return DefaultFolderBitmap;
                }
                else
                {
                    if (!string.IsNullOrEmpty(item.Extension))
                    {
                        var extIcon = GetSoftwareBitmapForExtension(item.Extension);
                        if (extIcon != null) return extIcon;
                    }
                    return DefaultFileBitmap;
                }
            }
            catch
            {
                return item.IsDirectory ? DefaultFolderBitmap : DefaultFileBitmap;
            }
        }

        public static SoftwareBitmap? GetSoftwareBitmapForPath(string fullPath, bool isDirectory, bool large = true)
        {
            try
            {
                if (fullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    return DefaultPcBitmap;
                }

                string ext = isDirectory ? string.Empty : Path.GetExtension(fullPath);
                bool isSpecialOrCustom = isDirectory ||
                                         (fullPath.Length <= 3 && fullPath.Contains(':')) ||
                                         string.IsNullOrEmpty(ext) ||
                                         CustomIconExtensions.Contains(ext);

                // 一般拡張子ファイルは拡張子キャッシュから即時返却
                if (!isSpecialOrCustom)
                {
                    var extIcon = GetSoftwareBitmapForExtension(ext);
                    if (extIcon != null) return extIcon;
                }

                var shinfo = new Win32Interop.SHFILEINFOW();
                uint flags = Win32Interop.SHGFI_ICON | (large ? Win32Interop.SHGFI_LARGEICON : Win32Interop.SHGFI_SMALLICON);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    flags |= Win32Interop.SHGFI_LINKOVERLAY;
                }

                if (isDirectory)
                {
                    if (Directory.Exists(fullPath) || (fullPath.Length <= 3 && fullPath.Contains(':')))
                    {
                        Win32Interop.SHGetFileInfoW(
                            fullPath,
                            Win32Interop.FILE_ATTRIBUTE_DIRECTORY,
                            ref shinfo,
                            (uint)Marshal.SizeOf(shinfo),
                            flags);
                    }
                    else
                    {
                        flags |= Win32Interop.SHGFI_USEFILEATTRIBUTES;
                        Win32Interop.SHGetFileInfoW(
                            "folder",
                            Win32Interop.FILE_ATTRIBUTE_DIRECTORY,
                            ref shinfo,
                            (uint)Marshal.SizeOf(shinfo),
                            flags);
                    }
                }
                else
                {
                    if (File.Exists(fullPath))
                    {
                        flags = Win32Interop.SHGFI_ICON | (large ? Win32Interop.SHGFI_LARGEICON : Win32Interop.SHGFI_SMALLICON);
                        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        {
                            flags |= Win32Interop.SHGFI_LINKOVERLAY;
                        }
                    }
                    else
                    {
                        flags |= Win32Interop.SHGFI_USEFILEATTRIBUTES;
                    }

                    Win32Interop.SHGetFileInfoW(
                        fullPath,
                        Win32Interop.FILE_ATTRIBUTE_NORMAL,
                        ref shinfo,
                        (uint)Marshal.SizeOf(shinfo),
                        flags);
                }

                if (shinfo.hIcon != nint.Zero)
                {
                    try
                    {
                        var bmp = ConvertHIconToSoftwareBitmap(shinfo.hIcon);
                        if (bmp != null) return bmp;
                    }
                    finally
                    {
                        Win32Interop.DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch
            {
                // ignored
            }

            if (isDirectory)
            {
                return DefaultFolderBitmap;
            }
            else
            {
                string ext = Path.GetExtension(fullPath);
                if (!string.IsNullOrEmpty(ext))
                {
                    var extIcon = GetSoftwareBitmapForExtension(ext);
                    if (extIcon != null) return extIcon;
                }
                return DefaultFileBitmap;
            }
        }

        private static SoftwareBitmap? ExtractThumbnailViaShellItem(
            string fullPath,
            int size = 128,
            Win32Interop.SIIGBF extraFlags = Win32Interop.SIIGBF.SIIGBF_BIGGERSIZEOK | Win32Interop.SIIGBF.SIIGBF_SCALEUP)
        {
            try
            {
                int hr = Win32Interop.SHCreateItemFromParsingName(
                    fullPath,
                    nint.Zero,
                    Win32Interop.IID_IShellItemImageFactory,
                    out var pUnknown);

                if (hr == 0 && pUnknown != nint.Zero)
                {
                    try
                    {
                        var factory = (Win32Interop.IShellItemImageFactory)Marshal.GetObjectForIUnknown(pUnknown);
                        var sz = new Win32Interop.SIZE(size, size);
                        hr = factory.GetImage(sz, extraFlags, out var hBitmap);
                        if (hr == 0 && hBitmap != nint.Zero)
                        {
                            try
                            {
                                bool isDir = Directory.Exists(fullPath);
                                return ConvertHBitmapToSoftwareBitmap(hBitmap, isDirectory: isDir);
                            }
                            finally
                            {
                                Win32Interop.DeleteObject(hBitmap);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(pUnknown);
                    }
                }
            }
            catch
            {
                // ignored
            }
            return null;
        }

        public static SoftwareBitmap? ExtractSkiaImageThumbnail(string fullPath, int targetSize = 96)
        {
            try
            {
                if (!File.Exists(fullPath)) return null;

                using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var codec = SkiaSharp.SKCodec.Create(stream);
                if (codec == null) return null;

                int origW = codec.Info.Width;
                int origH = codec.Info.Height;
                if (origW <= 0 || origH <= 0) return null;

                int targetW, targetH;
                if (origW >= origH)
                {
                    targetW = targetSize;
                    targetH = Math.Max(1, (int)((double)origH / origW * targetSize));
                }
                else
                {
                    targetH = targetSize;
                    targetW = Math.Max(1, (int)((double)origW / origH * targetSize));
                }

                var imageInfo = new SkiaSharp.SKImageInfo(targetW, targetH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var origBmp = SkiaSharp.SKBitmap.Decode(codec);
                if (origBmp == null) return null;

                using var resized = origBmp.Resize(imageInfo, SkiaSharp.SKSamplingOptions.Default);
                if (resized == null) return null;

                var pixelSpan = resized.GetPixelSpan();
                int byteCount = pixelSpan.Length;
                byte[] rentedArray = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    pixelSpan.CopyTo(rentedArray);
                    var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, targetW, targetH, BitmapAlphaMode.Premultiplied);
                    softwareBitmap.CopyFromBuffer(rentedArray.AsBuffer(0, byteCount));
                    return softwareBitmap;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedArray);
                }
            }
            catch
            {
                return null;
            }
        }

        private static SoftwareBitmap? ExtractDirectImageThumbnail(string fullPath, uint targetSize = 128)
        {
            try
            {
                if (!File.Exists(fullPath)) return null;

                using var stream = File.OpenRead(fullPath);
                using var randomStream = stream.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(randomStream).AsTask().GetAwaiter().GetResult();

                var transform = new BitmapTransform();
                uint origW = decoder.PixelWidth;
                uint origH = decoder.PixelHeight;
                if (origW > targetSize || origH > targetSize)
                {
                    if (origW >= origH)
                    {
                        transform.ScaledWidth = targetSize;
                        transform.ScaledHeight = Math.Max(1, (uint)((double)origH / origW * targetSize));
                    }
                    else
                    {
                        transform.ScaledHeight = targetSize;
                        transform.ScaledWidth = Math.Max(1, (uint)((double)origW / origH * targetSize));
                    }
                    transform.InterpolationMode = BitmapInterpolationMode.Linear;
                }

                return decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb
                ).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }
    }
}
