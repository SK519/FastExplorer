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

        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultFolderSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultFileSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultPcSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultHomeSource;
        private static Microsoft.UI.Xaml.Media.ImageSource? _defaultRecycleBinSource;
        private static readonly ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.ImageSource> _extensionSourceCache = new(StringComparer.OrdinalIgnoreCase);

        public static SoftwareBitmap? DefaultFolderBitmap => _defaultFolderBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_FOLDER);
        public static SoftwareBitmap? DefaultFileBitmap => _defaultFileBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DOCNOTASSOC);
        public static SoftwareBitmap? DefaultPcBitmap => _defaultPcBitmap ??= GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DESKTOPPC);
        public static SoftwareBitmap? DefaultHomeBitmap => _defaultHomeBitmap ??= GetHomeSoftwareBitmap(32);
        public static SoftwareBitmap? DefaultRecycleBinBitmap => _defaultRecycleBinBitmap ??= GetRecycleBinSoftwareBitmap(true);

        private IconThumbnailService()
        {
            int workerCount = Math.Clamp(Environment.ProcessorCount, 2, 8);
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
        }

        public void Initialize(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
            _initEvent.Set();

            // デフォルトアイコンの事前ロードおよび主要拡張子のウォームアップ
            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
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

                    _defaultPcBitmap = GetStockIconSoftwareBitmap(Core.Win32Interop.SHSTOCKICONID.SIID_DESKTOPPC);
                    if (_defaultPcBitmap != null)
                    {
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(_defaultPcBitmap);
                        _defaultPcSource = src;
                    }

                    string[] commonExtensions =
                    {
                        ".txt", ".pdf", ".zip", ".png", ".jpg", ".jpeg", ".bmp", ".gif",
                        ".mp4", ".mp3", ".wav", ".docx", ".xlsx", ".pptx", ".exe", ".dll",
                        ".json", ".xml", ".cs", ".html", ".css", ".js", ".ts", ".md"
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
                if (_defaultPcSource != null) item.Icon = _defaultPcSource;
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
            string key = GetCacheKey(item);
            if (_lruCache.TryGetValue(key, out var cachedBitmap))
            {
                SetIconToItem(item, cachedBitmap);
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
                _lruKeys.Clear();
            }
        }

        private static string GetCacheKey(FileItem item)
        {
            if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                return "special::thispc";
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
                        SetIconToItem(item, cachedBitmap);
                        continue;
                    }

                    SoftwareBitmap? bitmap = ExtractIconAsSoftwareBitmap(item);
                    if (bitmap != null)
                    {
                        AddToCache(key, bitmap);
                        SetIconToItem(item, bitmap);
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
                    }
                }
                _lruCache[key] = bitmap;
                _lruKeys.AddLast(key);
            }
        }

        private void SetIconToItem(FileItem item, SoftwareBitmap softwareBitmap)
        {
            if (_dispatcherQueue == null)
            {
                _initEvent.Wait(2000);
            }

            _dispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(softwareBitmap);
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
