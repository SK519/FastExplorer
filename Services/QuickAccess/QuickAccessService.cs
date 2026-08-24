using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static partial class QuickAccessService
    {
        public static event Action? PinnedItemsChanged;
        public static event Action? RecentItemsChanged;

        private const string Win11HomeShellNamespace = "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
        private const string Win10QuickAccessShellNamespace = "shell:::{679F8FB0-2DCD-40AE-8DDE-270223C79D7C}";

        private static FileSystemWatcher? _autoDestWatcher;
        private static FileSystemWatcher? _customDestWatcher;
        private static FileSystemWatcher? _recentWatcher;
        private static System.Threading.Timer? _debounceTimer;
        private static System.Threading.Timer? _recentDebounceTimer;

        static QuickAccessService()
        {
            InitializeWatchers();
        }

        public static void InitializeWatchers()
        {
            try
            {
                string recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                string autoDestPath = Path.Combine(recentPath, "AutomaticDestinations");
                string customDestPath = Path.Combine(recentPath, "CustomDestinations");

                if (Directory.Exists(autoDestPath))
                {
                    _autoDestWatcher?.Dispose();
                    _autoDestWatcher = new FileSystemWatcher(autoDestPath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        Filter = "*",
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };
                    _autoDestWatcher.Changed += OnDestinationChanged;
                    _autoDestWatcher.Created += OnDestinationChanged;
                    _autoDestWatcher.Deleted += OnDestinationChanged;
                    _autoDestWatcher.Renamed += OnDestinationChanged;
                }

                if (Directory.Exists(customDestPath))
                {
                    _customDestWatcher?.Dispose();
                    _customDestWatcher = new FileSystemWatcher(customDestPath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        Filter = "*",
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };
                    _customDestWatcher.Changed += OnDestinationChanged;
                    _customDestWatcher.Created += OnDestinationChanged;
                    _customDestWatcher.Deleted += OnDestinationChanged;
                    _customDestWatcher.Renamed += OnDestinationChanged;
                }

                if (Directory.Exists(recentPath))
                {
                    _recentWatcher?.Dispose();
                    _recentWatcher = new FileSystemWatcher(recentPath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        Filter = "*.lnk",
                        InternalBufferSize = 65536,
                        EnableRaisingEvents = true
                    };
                    _recentWatcher.Changed += OnRecentChanged;
                    _recentWatcher.Created += OnRecentChanged;
                    _recentWatcher.Deleted += OnRecentChanged;
                    _recentWatcher.Renamed += OnRecentChanged;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuickAccessService] InitializeWatchers error: {ex.Message}");
            }
        }

        private static void OnDestinationChanged(object sender, FileSystemEventArgs e)
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Threading.Timer(_ =>
                {
                    NotifyPinnedChanged();
                }, null, 150, System.Threading.Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(150, System.Threading.Timeout.Infinite);
            }
        }

        private static void OnRecentChanged(object sender, FileSystemEventArgs e)
        {
            if (_recentDebounceTimer == null)
            {
                _recentDebounceTimer = new System.Threading.Timer(_ =>
                {
                    NotifyRecentChanged();
                }, null, 250, System.Threading.Timeout.Infinite);
            }
            else
            {
                _recentDebounceTimer.Change(250, System.Threading.Timeout.Infinite);
            }
        }

        public static void NotifyRecentChanged()
        {
            try
            {
                RecentItemsChanged?.Invoke();
            }
            catch { }
        }

        /// <summary>
        /// 標準 Windows Explorer のクイックアクセス / ホームにピン留めされているフォルダー一覧を取得 (Native COM + Custom Pinned)
        /// </summary>
        public static List<QuickAccessFolderItem> GetPinnedFolders()
        {
            var results = new List<QuickAccessFolderItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                EnumerateHomeFolder((name, path, isFolder) =>
                {
                    if (isFolder && !string.IsNullOrWhiteSpace(path) && seenPaths.Add(path))
                    {
                        string glyph = ResolveFolderGlyph(name, path);
                        string displayName = FormatDisplayName(name, path);
                        results.Add(new QuickAccessFolderItem
                        {
                            Name = displayName,
                            Path = path,
                            GlyphIcon = glyph,
                            Subtitle = ResolveLocationSubtitle(path),
                            IsPinned = true
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuickAccessService] GetPinnedFolders Native COM error: {ex.Message}");
            }

            // Custom pinned folders (WSL 等の特殊パスのみ追加)
            try
            {
                var customPinned = ConfigService.Current.CustomPinnedFolders;
                if (customPinned != null)
                {
                    foreach (var customPath in customPinned)
                    {
                        if (IsWslPath(customPath) && !string.IsNullOrWhiteSpace(customPath) && seenPaths.Add(customPath))
                        {
                            string folderName = Path.GetFileName(customPath.TrimEnd('\\', '/'));
                            string displayName = FormatDisplayName(folderName, customPath);
                            results.Add(new QuickAccessFolderItem
                            {
                                Name = displayName,
                                Path = customPath,
                                GlyphIcon = "\uE74C",
                                Subtitle = "Linux (WSL)",
                                IsPinned = true
                            });
                        }
                    }
                }
            }
            catch { }

            // 万一 0 件の場合は標準特殊フォルダーをフォールバック
            if (results.Count == 0)
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                AddFallbackFolder(results, seenPaths, "Downloads", "\uE896", Path.Combine(userProfile, "Downloads"));
                AddFallbackFolder(results, seenPaths, "デスクトップ", "\uE8B7", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                AddFallbackFolder(results, seenPaths, "ドキュメント", "\uE8A5", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                AddFallbackFolder(results, seenPaths, "ピクチャ", "\uEB9F", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                AddFallbackFolder(results, seenPaths, "ミュージック", "\uE8D6", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                AddFallbackFolder(results, seenPaths, "ビデオ", "\uE714", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            }

            return results;
        }

        public static List<FileItem> GetPinnedFolderItems()
        {
            var pinned = GetPinnedFolders();
            var list = new List<FileItem>(pinned.Count);
            foreach (var p in pinned)
            {
                list.Add(new FileItem
                {
                    Name = p.Name,
                    FullPath = p.Path,
                    GlyphIcon = p.GlyphIcon,
                    Subtitle = p.Subtitle,
                    IsPinned = true,
                    IsDirectory = true,
                    FileType = "ピン留めフォルダー"
                });
            }
            return list;
        }

        private static void AddFallbackFolder(List<QuickAccessFolderItem> list, HashSet<string> seen, string label, string glyph, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
            {
                list.Add(new QuickAccessFolderItem
                {
                    Name = label,
                    Path = path,
                    GlyphIcon = glyph,
                    IsPinned = true
                });
            }
        }

        /// <summary>
        /// ホーム画面用の全アイテム (ピン留めフォルダー + 最近使った項目) を取得 (Native COM)
        /// </summary>
        public static List<FileItem> GetHomeItems()
        {
            var list = new List<FileItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                EnumerateHomeFolder((name, path, isFolder) =>
                {
                    if (!string.IsNullOrWhiteSpace(path) && seenPaths.Add(path))
                    {
                        var fileItem = new FileItem
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name,
                            FullPath = path,
                            IsDirectory = isFolder,
                            GlyphIcon = isFolder ? ResolveFolderGlyph(name, path) : Core.NativeFileScanner.GetGlyphIconForExtension(Path.GetExtension(path)),
                            FileType = isFolder ? "フォルダー" : Core.NativeFileScanner.GetFileTypeDescription(Path.GetExtension(path)),
                            IsPinned = isFolder
                        };

                        if (File.Exists(path))
                        {
                            var fi = new FileInfo(path);
                            fileItem.SizeInBytes = fi.Length;
                            fileItem.DateModified = fi.LastWriteTime;
                        }
                        else if (Directory.Exists(path))
                        {
                            var di = new DirectoryInfo(path);
                            fileItem.DateModified = di.LastWriteTime;
                        }

                        list.Add(fileItem);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuickAccessService] GetHomeItems error: {ex.Message}");
            }

            // フォールバック
            if (list.Count == 0)
            {
                foreach (var pinned in GetPinnedFolders())
                {
                    list.Add(new FileItem
                    {
                        Name = pinned.Name,
                        FullPath = pinned.Path,
                        IsDirectory = true,
                        GlyphIcon = pinned.GlyphIcon,
                        FileType = "フォルダー",
                        IsPinned = true
                    });
                }
            }

            return list;
        }

        private static void EnumerateHomeFolder(Action<string, string, bool> onItemCallback)
        {
            string[] namespaces = [Win11HomeShellNamespace, Win10QuickAccessShellNamespace];

            foreach (var ns in namespaces)
            {
                int hr = Win32Interop.SHParseDisplayName(ns, nint.Zero, out nint homePidl, 0, out _);
                if (hr != 0 || homePidl == nint.Zero) continue;

                nint desktopFolder = nint.Zero;
                nint homeFolder = nint.Zero;
                nint enumIdList = nint.Zero;

                try
                {
                    hr = Win32Interop.SHGetDesktopFolder(out desktopFolder);
                    if (hr != 0 || desktopFolder == nint.Zero) continue;

                    hr = Win32Interop.NativeCom.ShellFolder_BindToObject(desktopFolder, homePidl, in Win32Interop.IID_IShellFolder, out homeFolder);
                    if (hr != 0 || homeFolder == nint.Zero) continue;

                    uint flags = Win32Interop.SHCONTF_FOLDERS | Win32Interop.SHCONTF_NONFOLDERS;
                    hr = Win32Interop.NativeCom.ShellFolder_EnumObjects(homeFolder, nint.Zero, flags, out enumIdList);
                    if (hr != 0 || enumIdList == nint.Zero) continue;

                    var sbPath = new StringBuilder(512);
                    var sbName = new StringBuilder(260);

                    while (Win32Interop.NativeCom.EnumIDList_Next(enumIdList, 1, out nint childPidl, out uint fetched) == 0 && fetched == 1)
                    {
                        if (childPidl == nint.Zero) break;

                        nint fullPidl = Win32Interop.ILCombine(homePidl, childPidl);
                        try
                        {
                            sbPath.Clear();
                            bool hasPath = Win32Interop.SHGetPathFromIDListW(fullPidl, sbPath);
                            string path = sbPath.ToString();

                            sbName.Clear();
                            if (Win32Interop.NativeCom.ShellFolder_GetDisplayNameOf(homeFolder, childPidl, Win32Interop.SHGDN_NORMAL, out var strret) == 0)
                            {
                                Win32Interop.StrRetToBufW(ref strret, childPidl, sbName, (uint)sbName.Capacity);
                            }
                            string name = sbName.ToString();

                            bool isFolder = false;
                            if (!string.IsNullOrEmpty(path))
                            {
                                isFolder = Directory.Exists(path);
                            }

                            if (!string.IsNullOrEmpty(path))
                            {
                                onItemCallback(name, path, isFolder);
                            }
                        }
                        finally
                        {
                            if (fullPidl != nint.Zero) Win32Interop.ILFree(fullPidl);
                            Win32Interop.ILFree(childPidl);
                        }
                    }

                    // 1つ目の名前空間で正常に走査できたら終了
                    return;
                }
                finally
                {
                    if (enumIdList != nint.Zero) Win32Interop.NativeCom.Release(enumIdList);
                    if (homeFolder != nint.Zero) Win32Interop.NativeCom.Release(homeFolder);
                    if (desktopFolder != nint.Zero) Win32Interop.NativeCom.Release(desktopFolder);
                    if (homePidl != nint.Zero) Win32Interop.ILFree(homePidl);
                }
            }
        }

        /// <summary>
        /// 指定されたパスがピン留めされているか確認
        /// </summary>
        public static bool IsPinned(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var pinned = GetPinnedFolders();
            return pinned.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }
}
