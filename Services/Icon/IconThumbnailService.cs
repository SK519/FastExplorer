using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultFolderSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultFileSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultPcSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultHomeSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultRecycleBinSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultNetworkSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultWslSource;
        private static readonly ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.ImageSource> _extensionSourceCache = new(StringComparer.OrdinalIgnoreCase);

        public static SoftwareBitmap? DefaultFolderBitmap => _defaultFolderBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_FOLDER);
        public static SoftwareBitmap? DefaultFileBitmap => _defaultFileBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DOCNOTASSOC);
        public static SoftwareBitmap? DefaultPcBitmap => _defaultPcBitmap ??= GetPcSoftwareBitmap(true);
        public static SoftwareBitmap? DefaultHomeBitmap => _defaultHomeBitmap ??= GetHomeSoftwareBitmap(32);
        public static SoftwareBitmap? DefaultRecycleBinBitmap => _defaultRecycleBinBitmap ??= GetRecycleBinSoftwareBitmap(true);
        public static SoftwareBitmap? DefaultNetworkBitmap => _defaultNetworkBitmap ??= GetNetworkSoftwareBitmap(true);
        public static SoftwareBitmap? DefaultWslBitmap => _defaultWslBitmap ??= GetWslSoftwareBitmap(32);

        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultFolderSource => _defaultFolderSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultFileSource => _defaultFileSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultPcSource => _defaultPcSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultHomeSource => _defaultHomeSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultRecycleBinSource => _defaultRecycleBinSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultNetworkSource => _defaultNetworkSource;
        public static Microsoft.UI.Xaml.Media.ImageSource? DefaultWslSource => _defaultWslSource;

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

            // 基本デフォルトアイコンを最優先で即座に初期化
            _dispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () =>
            {
                try
                {
                    _defaultFolderBitmap = GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_FOLDER);
                    if (_defaultFolderBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultFolderBitmap);
                        _defaultFolderSource = src;
                    }

                    _defaultFileBitmap = GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DOCNOTASSOC);
                    if (_defaultFileBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultFileBitmap);
                        _defaultFileSource = src;
                    }

                    _defaultHomeBitmap = GetHomeSoftwareBitmap(32);
                    if (_defaultHomeBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultHomeBitmap);
                        _defaultHomeSource = src;
                    }

                    _defaultRecycleBinBitmap = GetRecycleBinSoftwareBitmap(true);
                    if (_defaultRecycleBinBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultRecycleBinBitmap);
                        _defaultRecycleBinSource = src;
                    }

                    _defaultPcBitmap = GetPcSoftwareBitmap(true);
                    if (_defaultPcBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultPcBitmap);
                        _defaultPcSource = src;
                    }

                    _defaultNetworkBitmap = GetNetworkSoftwareBitmap(true);
                    if (_defaultNetworkBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultNetworkBitmap);
                        _defaultNetworkSource = src;
                    }

                    _defaultWslBitmap = GetWslSoftwareBitmap(32);
                    if (_defaultWslBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultWslBitmap);
                        _defaultWslSource = src;
                    }

                    DefaultIconsInitialized?.Invoke();

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
                            var src = new SoftwareBitmapSource();
                            await src.SetBitmapAsync(bmp);
                            _extensionSourceCache[ext] = src;
                        }
                    }
                }
                catch { }
            });
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

        public void ClearCache()
        {
            lock (_lruLock)
            {
                foreach (var kvp in _lruCache)
                {
                    try { kvp.Value.Dispose(); } catch { }
                }
                _lruCache.Clear();
                _imageSourceCache.Clear();
                _lruKeys.Clear();
            }
        }

        private static string GetCacheKey(FileItem item)
        {
            if (item.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                return "special::home";
            }
            if (RecycleBinService.IsRecycleBinPath(item.FullPath))
            {
                return "special::recyclebin";
            }
            if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                return "special::thispc";
            }
            if (item.FullPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || item.FullPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                return "special::network";
            }
            if (IsWslRootPath(item.FullPath, item.Name))
            {
                return "special::wsl::" + item.FullPath.ToLowerInvariant();
            }

            // ドライブ
            if (item.FullPath.Length <= 3 && item.FullPath.Contains(':'))
            {
                return "drive::" + item.FullPath.ToUpperInvariant();
            }

            // フォルダーはそれぞれの固有パスをキーにしてキャッシュ
            if (item.IsDirectory)
            {
                return "folder::" + item.FullPath.ToLowerInvariant();
            }

            // ファイル
            string ext = item.Extension;
            if (item.AllowThumbnail && (string.IsNullOrEmpty(ext) || MediaPreviewExtensions.Contains(ext) || CustomIconExtensions.Contains(ext)))
            {
                return item.FullPath.ToLowerInvariant();
            }
            if (!item.AllowThumbnail && (string.IsNullOrEmpty(ext) || CustomIconExtensions.Contains(ext)))
            {
                return item.FullPath.ToLowerInvariant();
            }
            return "ext::" + ext;
        }

        private void ProcessWorkQueue()
        {
            try
            {
                Core.Win32Interop.OleInitialize(nint.Zero);
            }
            catch { }

            // UI DispatcherQueue の初期化完了を待機（最大5秒）
            _initEvent.Wait(5000);

            foreach (var item in _workQueue.GetConsumingEnumerable())
            {
                _queuedPaths.TryRemove(item.FullPath, out _);
                try
                {
                    string key = GetCacheKey(item);
                    if (_lruCache.TryGetValue(key, out var cachedBitmap))
                    {
                        SetIconToItem(item, cachedBitmap, key);
                        continue;
                    }

                    SoftwareBitmap? bitmap = ExtractIconAsSoftwareBitmap(item);
                    if (bitmap != null)
                    {
                        AddToCache(key, bitmap);
                        SetIconToItem(item, bitmap, key);
                    }
                }
                catch
                {
                    // アイコン抽出エラーはスキップ
                }

                if (_workQueue.Count == 0)
                {
                    // キュー消化完了時に一時メモリを解放し、未使用物理メモリをOSに返却
                    GC.Collect(2, GCCollectionMode.Optimized, false, false);
                    try
                    {
                        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
                        Core.Win32Interop.SetProcessWorkingSetSize(curProcess.Handle, (nint)(-1), (nint)(-1));
                    }
                    catch { }
                }
            }
        }

        private void AddToCache(string key, SoftwareBitmap bitmap)
        {
            lock (_lruLock)
            {
                if (_lruCache.Count >= MaxCacheEntries)
                {
                    if (_lruKeys.First != null)
                    {
                        string oldestKey = _lruKeys.First.Value;
                        _lruKeys.RemoveFirst();
                        if (_lruCache.TryRemove(oldestKey, out var oldBmp))
                        {
                            try { oldBmp.Dispose(); } catch { }
                        }
                        _imageSourceCache.TryRemove(oldestKey, out _);
                    }
                }
                _lruCache[key] = bitmap;
                _lruKeys.AddLast(key);
            }
        }

        private void SetIconToItem(FileItem item, SoftwareBitmap softwareBitmap, string cacheKey)
        {
            if (_dispatcherQueue == null)
            {
                _initEvent.Wait(2000);
            }

            _dispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    if (_imageSourceCache.TryGetValue(cacheKey, out var cachedSource))
                    {
                        item.Icon = cachedSource;
                        return;
                    }

                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(softwareBitmap);
                    _imageSourceCache[cacheKey] = source;
                    item.Icon = source;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[IconThumbnailService] SetIconToItem error: {ex.Message}");
                }
            });
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
