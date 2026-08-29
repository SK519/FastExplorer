using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FastExplorer.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FastExplorer.Services
{
    public partial class IconThumbnailService
    {
        private static readonly Lazy<IconThumbnailService> _instance = new(() => new IconThumbnailService());
        public static IconThumbnailService Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, SoftwareBitmap> _lruCache = new();
        private readonly ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.ImageSource> _imageSourceCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _lruKeys = new();
        private readonly object _lruLock = new();
        private const int MaxCacheEntries = 2000;

        private readonly BlockingCollection<FileItem> _workQueue = new();
        private readonly ConcurrentDictionary<string, byte> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Thread> _workerThreads = [];
        private readonly ManualResetEventSlim _initEvent = new(false);
        private DispatcherQueue? _dispatcherQueue;

        private static SoftwareBitmap? _defaultFolderBitmap;
        private static SoftwareBitmap? _defaultFileBitmap;
        private static SoftwareBitmap? _defaultPcBitmap;
        private static SoftwareBitmap? _defaultHomeBitmap;
        private static SoftwareBitmap? _defaultRecycleBinBitmap;
        private static SoftwareBitmap? _defaultNetworkBitmap;
        private static SoftwareBitmap? _defaultWslBitmap;
        private static SoftwareBitmap? _defaultDriveBitmap;

        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultFolderSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultFileSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultPcSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultHomeSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultRecycleBinSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultNetworkSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultWslSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultDriveSource;
        private static readonly ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.ImageSource> _extensionSourceCache = new(StringComparer.OrdinalIgnoreCase);

        public static SoftwareBitmap? DefaultFolderBitmap => _defaultFolderBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_FOLDER);
        public static SoftwareBitmap? DefaultFileBitmap => _defaultFileBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DOCNOTASSOC);
        public static SoftwareBitmap? DefaultPcBitmap => _defaultPcBitmap ??= GetPcSoftwareBitmap(true);
        public static SoftwareBitmap? DefaultHomeBitmap => _defaultHomeBitmap ??= GetHomeSoftwareBitmap(32);
        public static SoftwareBitmap? DefaultRecycleBinBitmap => _defaultRecycleBinBitmap ??= GetRecycleBinSoftwareBitmap(true);
        public static SoftwareBitmap? DefaultNetworkBitmap => _defaultNetworkBitmap ??= GetNetworkSoftwareBitmap(true);
        public static SoftwareBitmap? DefaultWslBitmap => _defaultWslBitmap ??= GetWslSoftwareBitmap(32);
        public static SoftwareBitmap? DefaultDriveBitmap => _defaultDriveBitmap ??= GetDriveSoftwareBitmap("C:\\", true);

        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultFolderSource => _defaultFolderSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultFileSource => _defaultFileSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultPcSource => _defaultPcSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultHomeSource => _defaultHomeSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultRecycleBinSource => _defaultRecycleBinSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultNetworkSource => _defaultNetworkSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultWslSource => _defaultWslSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultDriveSource => _defaultDriveSource;

        private bool _workersStarted = false;
        private readonly object _workerLock = new();

        public event Action? DefaultIconsInitialized;

        private IconThumbnailService()
        {
            // コンストラクタではスレッドを作らず、最初のEnqueue時にオンデマンドで起動して起動時CPU競合を防止
        }

        private void EnsureWorkersStarted()
        {
            if (_workersStarted) return;
            lock (_workerLock)
            {
                if (_workersStarted) return;
                int workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
                for (int i = 0; i < workerCount; i++)
                {
                    int workerIndex = i;
                    var workerThread = new Thread(ProcessWorkQueue)
                    {
                        IsBackground = true,
                        Name = $"IconThumbnailWorker_{workerIndex}",
                        Priority = ThreadPriority.BelowNormal
                    };
                    workerThread.SetApartmentState(ApartmentState.STA);
                    workerThread.Start();
                    _workerThreads.Add(workerThread);
                }
                _workersStarted = true;
            }
        }

        public void Initialize(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
            _initEvent.Set();

            // 重い Win32/COM/GDI+ ビットマップ抽出はバックグラウンドスレッドで実行し、UIスレッドのブロックを完全排除
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // 1. 最重要基本アイコンのビットマップをバックグラウンドで事前抽出
                    var folderBmp = GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_FOLDER);
                    var fileBmp = GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DOCNOTASSOC);
                    var homeBmp = GetHomeSoftwareBitmap(32);
                    var recycleBmp = GetRecycleBinSoftwareBitmap(true);
                    var pcBmp = GetPcSoftwareBitmap(true);
                    var netBmp = GetNetworkSoftwareBitmap(true);
                    var wslBmp = GetWslSoftwareBitmap(32);
                    var driveBmp = GetDriveSoftwareBitmap("C:\\", false);

                    _defaultFolderBitmap = folderBmp;
                    _defaultFileBitmap = fileBmp;
                    _defaultHomeBitmap = homeBmp;
                    _defaultRecycleBinBitmap = recycleBmp;
                    _defaultPcBitmap = pcBmp;
                    _defaultNetworkBitmap = netBmp;
                    _defaultWslBitmap = wslBmp;
                    _defaultDriveBitmap = driveBmp;

                    // 2. UIスレッド上で SoftwareBitmapSource に変換 (最優先バッチ)
                    _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, async () =>
                    {
                        try
                        {
                            if (folderBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(folderBmp);
                                _defaultFolderSource = src;
                            }
                            if (fileBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(fileBmp);
                                _defaultFileSource = src;
                            }
                            if (homeBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(homeBmp);
                                _defaultHomeSource = src;
                            }
                            if (recycleBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(recycleBmp);
                                _defaultRecycleBinSource = src;
                            }
                            if (pcBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(pcBmp);
                                _defaultPcSource = src;
                            }
                            if (netBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(netBmp);
                                _defaultNetworkSource = src;
                            }
                            if (wslBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(wslBmp);
                                _defaultWslSource = src;
                            }
                            if (driveBmp != null)
                            {
                                var src = new SoftwareBitmapSource();
                                await src.SetBitmapAsync(driveBmp);
                                _defaultDriveSource = src;
                            }

                            DefaultIconsInitialized?.Invoke();
                        }
                        catch { }
                    });

                    // 3. ドライブ固有アイコン・主要拡張子のウォームアップは初回UI描画が落ち着いた後にバックグラウンドで実行
                    await System.Threading.Tasks.Task.Delay(600);

                    // ドライブ一覧の取得とアイコンキャッシュ
                    try
                    {
                        var drives = DriveInfo.GetDrives();
                        foreach (var drive in drives)
                        {
                            try
                            {
                                string root = drive.RootDirectory.FullName;
                                var bmp = GetDriveSoftwareBitmap(root, false);
                                if (bmp != null)
                                {
                                    _dispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
                                    {
                                        try
                                        {
                                            var src = new SoftwareBitmapSource();
                                            await src.SetBitmapAsync(bmp);
                                            _driveSourceCache[root] = src;
                                            _driveSourceCache[root.TrimEnd('\\')] = src;
                                        }
                                        catch { }
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    // 主要拡張子の非同期ウォームアップ
                    string[] commonExtensions =
                    {
                        ".txt", ".pdf", ".zip", ".png", ".jpg", ".jpeg",
                        ".mp4", ".docx", ".xlsx", ".pptx", ".exe"
                    };
                    foreach (var ext in commonExtensions)
                    {
                        var bmp = GetSoftwareBitmapForExtension(ext);
                        if (bmp != null)
                        {
                            _dispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
                            {
                                try
                                {
                                    var src = new SoftwareBitmapSource();
                                    await src.SetBitmapAsync(bmp);
                                    _extensionSourceCache[ext] = src;
                                }
                                catch { }
                            });
                        }
                    }
                }
                catch { }
            });
        }

        private static readonly ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.ImageSource> _driveSourceCache = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsDriveRootPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && trimmed[1] == ':') return true;
            if (path.Length <= 3 && path.EndsWith(":\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsWslRootPath(string path, string name = "")
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (name.Equals("WSL", StringComparison.OrdinalIgnoreCase) || path.Equals("WSL", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("Linux", StringComparison.OrdinalIgnoreCase) || path.Equals("Linux", StringComparison.OrdinalIgnoreCase)) return true;

            string trimmed = path.TrimEnd('\\');
            if (trimmed.Equals(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(@"\\wsl$", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(@"\\wsl", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public static Microsoft.UI.Xaml.Controls.IconSource GetIconSourceForNavigationPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return _defaultFolderSource != null
                    ? new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultFolderSource }
                    : new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE8B7" };
            }

            if (path.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                if (_defaultHomeSource != null)
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultHomeSource };
                return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE80F" };
            }
            if (RecycleBinService.IsRecycleBinPath(path))
            {
                if (_defaultRecycleBinSource != null)
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultRecycleBinSource };
                return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE74D" };
            }
            if (path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                if (_defaultPcSource != null)
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultPcSource };
                return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE770" };
            }
            if (path.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || path.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                if (_defaultNetworkSource != null)
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultNetworkSource };
                return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE968" };
            }
            if (IsWslRootPath(path))
            {
                if (_defaultWslSource != null)
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultWslSource };
                return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE74C" };
            }
            if (IsDriveRootPath(path))
            {
                string root = path.TrimEnd('\\', '/').ToUpperInvariant() + "\\";
                if (_driveSourceCache.TryGetValue(root, out var driveSrc) || _driveSourceCache.TryGetValue(path, out driveSrc))
                {
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = driveSrc };
                }

                if (_defaultDriveSource != null)
                {
                    return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultDriveSource };
                }
                return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uEDA2" };
            }

            if (_defaultFolderSource != null)
            {
                return new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = _defaultFolderSource };
            }

            return new Microsoft.UI.Xaml.Controls.FontIconSource { Glyph = "\uE8B7" };
        }

        public void ApplyImmediateDefaultIcon(FileItem item)
        {
            if (item.Icon != null) return;

            if (item.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                if (_defaultHomeSource != null) item.Icon = _defaultHomeSource;
            }
            else if (RecycleBinService.IsRecycleBinPath(item.FullPath))
            {
                if (_defaultRecycleBinSource != null) item.Icon = _defaultRecycleBinSource;
            }
            else if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                if (_defaultPcSource != null) item.Icon = _defaultPcSource;
            }
            else if (item.FullPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || item.FullPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                if (_defaultNetworkSource != null) item.Icon = _defaultNetworkSource;
            }
            else if (IsWslRootPath(item.FullPath, item.Name))
            {
                if (_defaultWslSource != null) item.Icon = _defaultWslSource;
            }
            else if (item.IsDrive || item.FileType == "ドライブ" || item.FileType == "ローカル ディスク" || item.FileType == "USB ドライブ" || item.FileType == "ネットワーク ドライブ" || item.FileType == "CD/DVD ドライブ" || item.FileType == "RAM ディスク" || IsDriveRootPath(item.FullPath))
            {
                string root = item.FullPath.TrimEnd('\\', '/').ToUpperInvariant() + "\\";
                if (_driveSourceCache.TryGetValue(root, out var driveSrc) || _driveSourceCache.TryGetValue(item.FullPath, out driveSrc))
                {
                    item.Icon = driveSrc;
                }
                else if (_defaultDriveSource != null)
                {
                    item.Icon = _defaultDriveSource;
                }
            }
            else if (item.IsDirectory)
            {
                if (_defaultFolderSource != null) item.Icon = _defaultFolderSource;
            }
            else
            {
                string ext = item.Extension;
                if (!string.IsNullOrEmpty(ext) && _extensionSourceCache.TryGetValue(ext, out var extSrc))
                {
                    item.Icon = extSrc;
                }
                else if (_defaultFileSource != null)
                {
                    item.Icon = _defaultFileSource;
                }
            }
        }

        public void Enqueue(FileItem item, bool force = false)
        {
            EnsureWorkersStarted();

            string key = GetCacheKey(item);
            if (_imageSourceCache.TryGetValue(key, out var cachedSource))
            {
                item.Icon = cachedSource;
                return;
            }
            if (_lruCache.TryGetValue(key, out var cachedBitmap))
            {
                SetIconToItem(item, cachedBitmap, key);
                return;
            }

            // 読み込み待ちのチラつきを防ぐため、即座に事前作成済みデフォルトアイコン/拡張子別アイコンを同期適用
            ApplyImmediateDefaultIcon(item);

            // 一般ファイル（サムネイル対象外、かつ固有アイコン不要）で既に拡張子アイコンが当たっている場合は重い抽出をスキップ
            bool isThumbnailTarget = item.AllowThumbnail && (string.IsNullOrEmpty(item.Extension) || MediaPreviewExtensions.Contains(item.Extension));
            bool isCustomIconTarget = item.IsDirectory || (item.FullPath.Length <= 3 && item.FullPath.Contains(':')) || string.IsNullOrEmpty(item.Extension) || CustomIconExtensions.Contains(item.Extension);

            if (!force && !isThumbnailTarget && !isCustomIconTarget)
            {
                if (item.Icon != null && item.Icon != _defaultFolderSource && item.Icon != _defaultFileSource)
                {
                    return;
                }
            }

            // キュー内の重複排除
            if (_queuedPaths.TryAdd(item.FullPath, 0))
            {
                _workQueue.Add(item);
            }
        }

        public static bool IsImageOrientedMode(FolderViewMode mode)
        {
            return mode is FolderViewMode.ExtraLargeIcons
                or FolderViewMode.LargeIcons
                or FolderViewMode.MediumIcons
                or FolderViewMode.Tiles;
        }
    }
}
