using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FastExplorer.Models;

namespace FastExplorer.Core
{
    public static partial class NativeFileScanner
    {
        private static List<FileItem>? _cachedNetworkPlaces;
        private static DateTime _lastNetworkScanTime = DateTime.MinValue;
        private static readonly object _networkCacheLock = new();

        public static List<FileItem> GetNetworkPlacesFast()
        {
            var items = new List<FileItem>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 割り当て済みネットワークドライブ (Z:\ など) を即時追加 (0ms)
            var netDrives = GetDrivesInternal(true);
            foreach (var drive in netDrives)
            {
                if (addedPaths.Add(drive.FullPath))
                {
                    items.Add(drive);
                }
            }

            // 2. WSL / Linux ネットワークルート (\\wsl.localhost) (0ms)
            if (Directory.Exists(@"\\wsl.localhost") && addedPaths.Add(@"\\wsl.localhost"))
            {
                items.Add(new FileItem
                {
                    Name = "Linux (WSL)",
                    FullPath = @"\\wsl.localhost",
                    IsDirectory = true,
                    FileType = "Linux サブシステム",
                    GlyphIcon = "\uE74C"
                });
            }

            // 3. キャッシュ済みアイテムがあれば即座に返却 (0ms)
            lock (_networkCacheLock)
            {
                if (_cachedNetworkPlaces != null)
                {
                    foreach (var item in _cachedNetworkPlaces)
                    {
                        if (addedPaths.Add(item.FullPath))
                        {
                            items.Add(item);
                        }
                    }
                }
            }

            return items;
        }

        public static List<FileItem> GetNetworkPlaces()
        {
            return GetNetworkPlacesFast();
        }

        public static void ScanNetworkPlacesLive(Action<FileItem> onItemDiscovered)
        {
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<FileItem>();

            // 割り当て済みネットワークドライブ
            var netDrives = GetDrivesInternal(true);
            foreach (var drive in netDrives)
            {
                if (addedPaths.Add(drive.FullPath)) items.Add(drive);
            }

            // WSL
            if (Directory.Exists(@"\\wsl.localhost") && addedPaths.Add(@"\\wsl.localhost"))
            {
                items.Add(new FileItem
                {
                    Name = "Linux (WSL)",
                    FullPath = @"\\wsl.localhost",
                    IsDirectory = true,
                    FileType = "Linux サブシステム",
                    GlyphIcon = "\uE74C"
                });
            }

            EnumerateNetworkShellFolder(items, addedPaths, onItemDiscovered);

            lock (_networkCacheLock)
            {
                _cachedNetworkPlaces = new List<FileItem>(items);
                _lastNetworkScanTime = DateTime.UtcNow;
            }
        }

        private static void EnumerateNetworkShellFolder(List<FileItem> items, HashSet<string> addedPaths, Action<FileItem>? onItemDiscovered = null)
        {
            int hr = Win32Interop.SHParseDisplayName("shell:NetworkPlacesFolder", nint.Zero, out nint netPidl, 0, out _);
            if (hr != 0 || netPidl == nint.Zero)
            {
                hr = Win32Interop.SHParseDisplayName("::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", nint.Zero, out netPidl, 0, out _);
            }
            if (hr != 0 || netPidl == nint.Zero) return;

            nint desktopFolder = nint.Zero;
            nint netFolder = nint.Zero;
            nint enumIdList = nint.Zero;

            try
            {
                hr = Win32Interop.SHGetDesktopFolder(out desktopFolder);
                if (hr != 0 || desktopFolder == nint.Zero) return;

                hr = Win32Interop.NativeCom.ShellFolder_BindToObject(desktopFolder, netPidl, in Win32Interop.IID_IShellFolder, out netFolder);
                if (hr != 0 || netFolder == nint.Zero)
                {
                    hr = Win32Interop.SHBindToObject(nint.Zero, netPidl, nint.Zero, in Win32Interop.IID_IShellFolder, out netFolder);
                }
                if (hr != 0 || netFolder == nint.Zero) return;

                uint flags = Win32Interop.SHCONTF_FOLDERS | Win32Interop.SHCONTF_NONFOLDERS | Win32Interop.SHCONTF_INCLUDEHIDDEN;
                hr = Win32Interop.NativeCom.ShellFolder_EnumObjects(netFolder, nint.Zero, flags, out enumIdList);
                if (hr != 0 || enumIdList == nint.Zero) return;

                var sbParsing = new StringBuilder(512);
                var sbName = new StringBuilder(260);
                var uniqueDevices = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);

                while (Win32Interop.NativeCom.EnumIDList_Next(enumIdList, 1, out nint childPidl, out uint fetched) == 0 && fetched == 1)
                {
                    if (childPidl == nint.Zero) break;

                    nint fullPidl = Win32Interop.ILCombine(netPidl, childPidl);
                    try
                    {
                        sbName.Clear();
                        if (Win32Interop.NativeCom.ShellFolder_GetDisplayNameOf(netFolder, childPidl, Win32Interop.SHGDN_NORMAL, out var strretName) == 0)
                        {
                            Win32Interop.StrRetToBufW(ref strretName, childPidl, sbName, (uint)sbName.Capacity);
                        }
                        string displayName = sbName.ToString();

                        sbParsing.Clear();
                        if (Win32Interop.NativeCom.ShellFolder_GetDisplayNameOf(netFolder, childPidl, Win32Interop.SHGDN_FORPARSING, out var strretParsing) == 0)
                        {
                            Win32Interop.StrRetToBufW(ref strretParsing, childPidl, sbParsing, (uint)sbParsing.Capacity);
                        }
                        string parsingPath = sbParsing.ToString();

                        if (string.IsNullOrEmpty(parsingPath))
                        {
                            var sbPath = new StringBuilder(512);
                            if (Win32Interop.SHGetPathFromIDListW(fullPidl, sbPath))
                            {
                                parsingPath = sbPath.ToString();
                            }
                        }

                        if (string.IsNullOrEmpty(displayName) && string.IsNullOrEmpty(parsingPath))
                        {
                            continue;
                        }

                        string effectivePath = !string.IsNullOrEmpty(parsingPath) ? parsingPath : displayName;
                        string effectiveName = !string.IsNullOrEmpty(displayName) ? displayName : Path.GetFileName(parsingPath);

                        if (string.IsNullOrEmpty(effectiveName))
                        {
                            effectiveName = effectivePath;
                        }

                        if (!addedPaths.Add(effectivePath))
                        {
                            continue;
                        }

                        // 属性取得
                        uint attr = 0x20000000 | 0x40000000; // SFGAO_FOLDER | SFGAO_FILESYSTEM
                        unsafe
                        {
                            nint* pChild = &childPidl;
                            Win32Interop.NativeCom.ShellFolder_GetAttributesOf(netFolder, 1, pChild, ref attr);
                        }
                        bool isFolder = (attr & 0x20000000) != 0 || effectivePath.StartsWith(@"\\") || Directory.Exists(effectivePath);

                        // 機器タイプとグリフの推定
                        string fileType = "ネットワーク デバイス";
                        string glyph = "\uE7F8"; // Computer

                        string lowerName = effectiveName.ToLowerInvariant();
                        string lowerPath = effectivePath.ToLowerInvariant();

                        if (lowerName.Contains("tv") || lowerName.Contains("bwt") || lowerName.Contains("dmr") ||
                            lowerName.Contains("series") || lowerName.Contains("スカパー") || lowerName.Contains("rec") ||
                            lowerName.Contains("media") || lowerName.Contains("viera") || lowerName.Contains("bravia") ||
                            lowerName.Contains("regza") || lowerName.Contains("aquos") || lowerName.Contains("player") ||
                            (lowerName.Contains("server") && lowerName.Contains("dlna")))
                        {
                            fileType = "メディア機器";
                            glyph = "\uE7F4"; // Media device / TV
                        }
                        else if (effectivePath.StartsWith(@"\\") || lowerPath.Contains("workgroup") || lowerPath.Contains("domain"))
                        {
                            fileType = "コンピューター";
                            glyph = "\uE7F8"; // Server / PC
                        }
                        else if (isFolder)
                        {
                            fileType = "ネットワーク フォルダー";
                            glyph = "\uE8B7"; // Folder
                        }
                        else
                        {
                            fileType = "その他のデバイス";
                            glyph = "\uE950"; // Device
                        }

                        var newItem = new FileItem
                        {
                            Name = effectiveName,
                            FullPath = effectivePath,
                            IsDirectory = isFolder,
                            FileType = fileType,
                            GlyphIcon = glyph
                        };

                        // 同一物理機器 (同一表示名) の重複を排除 (UNCパスやWeb管理機能を持つアイテムを最優先)
                        if (uniqueDevices.TryGetValue(effectiveName, out var existingItem))
                        {
                            if (effectivePath.StartsWith(@"\\") || (!existingItem.FullPath.StartsWith(@"\\") && effectivePath.Contains("SSDP", StringComparison.OrdinalIgnoreCase)))
                            {
                                uniqueDevices[effectiveName] = newItem;
                                onItemDiscovered?.Invoke(newItem);
                            }
                        }
                        else
                        {
                            uniqueDevices[effectiveName] = newItem;
                            onItemDiscovered?.Invoke(newItem);
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                    finally
                    {
                        if (fullPidl != nint.Zero) Win32Interop.ILFree(fullPidl);
                        Win32Interop.ILFree(childPidl);
                    }
                }

                foreach (var dev in uniqueDevices.Values)
                {
                    items.Add(dev);
                }
            }
            finally
            {
                if (enumIdList != nint.Zero) Win32Interop.NativeCom.Release(enumIdList);
                if (netFolder != nint.Zero) Win32Interop.NativeCom.Release(netFolder);
                if (desktopFolder != nint.Zero) Win32Interop.NativeCom.Release(desktopFolder);
                if (netPidl != nint.Zero) Win32Interop.ILFree(netPidl);
            }
        }
    }
}
