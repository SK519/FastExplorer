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
        public void ClearCache()
        {
            lock (_lruLock)
            {
                _lruCache.Clear();
                _imageSourceCache.Clear();
                _thumbnailSourceCache.Clear();
                _lruKeys.Clear();
                _imageSourceKeys.Clear();
                _thumbnailSourceKeys.Clear();
            }
        }

        public static bool IsSharedBitmap(SoftwareBitmap? bmp)
        {
            if (bmp == null) return false;
            if (ReferenceEquals(bmp, _defaultFolderBitmap) ||
                ReferenceEquals(bmp, _defaultFileBitmap) ||
                ReferenceEquals(bmp, _defaultPcBitmap) ||
                ReferenceEquals(bmp, _defaultHomeBitmap) ||
                ReferenceEquals(bmp, _defaultRecycleBinBitmap) ||
                ReferenceEquals(bmp, _defaultNetworkBitmap) ||
                ReferenceEquals(bmp, _defaultWslBitmap) ||
                ReferenceEquals(bmp, _defaultDriveBitmap))
            {
                return true;
            }
            foreach (var shared in _extensionIconCache.Values)
            {
                if (ReferenceEquals(bmp, shared))
                    return true;
            }
            return false;
        }

        private static string GetCacheKey(FileItem item)
        {
            string sizePrefix = item.AllowThumbnail ? "large::" : "small::";

            if (item.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                return "special::" + sizePrefix + "home";
            }
            if (RecycleBinService.IsRecycleBinPath(item.FullPath))
            {
                return "special::" + sizePrefix + "recyclebin";
            }
            if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                return "special::" + sizePrefix + "thispc";
            }
            if (item.FullPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || item.FullPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                return "special::" + sizePrefix + "network";
            }
            if (IsWslRootPath(item.FullPath, item.Name))
            {
                return "special::wsl::" + sizePrefix + item.FullPath.ToLowerInvariant();
            }
            if (IsWslUncPath(item.FullPath))
            {
                if (item.IsDirectory)
                {
                    return "special::wsl::folder";
                }
                string wslExt = item.Extension;
                if (!string.IsNullOrEmpty(wslExt))
                {
                    return "ext::small::" + wslExt;
                }
                return "special::wsl::file";
            }

            if (item.FullPath.StartsWith("::") || item.FullPath.StartsWith("shell:") || item.FullPath.StartsWith("urn:"))
            {
                return "shellitem::" + sizePrefix + item.FullPath.ToLowerInvariant();
            }

            // ドライブ
            if (item.FullPath.Length <= 3 && item.FullPath.Contains(':'))
            {
                return "drive::" + sizePrefix + item.FullPath.ToUpperInvariant();
            }

            // フォルダーはそれぞれの固有パスをキーにしてキャッシュ
            if (item.IsDirectory)
            {
                return "folder::" + sizePrefix + item.FullPath.ToLowerInvariant();
            }

            // ファイル
            string ext = item.Extension;
            if (item.AllowThumbnail)
            {
                return "file::" + sizePrefix + item.FullPath.ToLowerInvariant();
            }
            if (string.IsNullOrEmpty(ext) || CustomIconExtensions.Contains(ext))
            {
                return "file::" + sizePrefix + item.FullPath.ToLowerInvariant();
            }
            return "ext::" + sizePrefix + ext;
        }

        private static bool IsFileThumbnailKey(string key) =>
            key.StartsWith("file::large::", StringComparison.OrdinalIgnoreCase);

        private void ProcessWorkQueue()
        {
            try
            {
                Core.Win32Interop.OleInitialize(nint.Zero);
            }
            catch { }

            // UI DispatcherQueue の初期化完了を待機（最大5秒）
            _initEvent.Wait(5000);

            foreach (var (item, gen) in _workQueue.GetConsumingEnumerable())
            {
                _queuedPaths.TryRemove(item.FullPath, out _);

                // 古いフォルダー世代のアイテムは即座にスキップ
                if (gen != Volatile.Read(ref _queueGeneration))
                {
                    continue;
                }

                try
                {
                    string key = GetCacheKey(item);
                    if (_thumbnailSourceCache.TryGetValue(key, out var cachedThumb))
                    {
                        _dispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                        {
                            if (gen == Volatile.Read(ref _queueGeneration))
                                item.Icon = cachedThumb;
                        });
                        continue;
                    }
                    if (_imageSourceCache.TryGetValue(key, out var cachedSrc))
                    {
                        _dispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                        {
                            if (gen == Volatile.Read(ref _queueGeneration))
                                item.Icon = cachedSrc;
                        });
                        continue;
                    }
                    if (_lruCache.TryGetValue(key, out var cachedBitmap))
                    {
                        SetIconToItem(item, cachedBitmap, key, disposeAfterUpload: false, releaseSemaphore: false, gen);
                        continue;
                    }

                    // UI 反映待ちの SoftwareBitmap 総数を制限（バックプレッシャー）
                    _inFlightSemaphore.Wait();
                    bool ownershipTransferred = false;
                    SoftwareBitmap? bitmap = null;
                    try
                    {
                        if (gen != Volatile.Read(ref _queueGeneration))
                        {
                            continue;
                        }

                        bitmap = ExtractIconAsSoftwareBitmap(item);
                        if (bitmap != null)
                        {
                            bool isFileThumb = IsFileThumbnailKey(key);
                            bool disposeAfter = isFileThumb && !IsSharedBitmap(bitmap);
                            if (!isFileThumb)
                            {
                                AddToCache(key, bitmap);
                            }

                            ownershipTransferred = SetIconToItem(item, bitmap, key, disposeAfter, releaseSemaphore: true, gen);
                        }
                    }
                    finally
                    {
                        if (!ownershipTransferred)
                        {
                            if (bitmap != null && !IsSharedBitmap(bitmap) && IsFileThumbnailKey(key))
                            {
                                try { bitmap.Dispose(); } catch { }
                            }
                            _inFlightSemaphore.Release();
                        }
                    }
                }
                catch
                {
                    // アイコン抽出エラーはスキップ
                }
            }
        }

        private void AddToCache(string key, SoftwareBitmap bitmap)
        {
            int limit = Math.Clamp(ConfigService.Current.Cache.MaxEntries, 200, MaxCacheEntries);
            lock (_lruLock)
            {
                if (_lruCache.ContainsKey(key))
                {
                    _lruCache[key] = bitmap;
                    return;
                }

                while (_lruCache.Count >= limit && _lruKeys.First != null)
                {
                    string oldestKey = _lruKeys.First.Value;
                    _lruKeys.RemoveFirst();
                    _lruCache.TryRemove(oldestKey, out _);
                    _imageSourceCache.TryRemove(oldestKey, out _);
                }

                _lruCache[key] = bitmap;
                _lruKeys.AddLast(key);
            }
        }

        private void AddSourceToCache(string key, Microsoft.UI.Xaml.Media.ImageSource source)
        {
            if (IsFileThumbnailKey(key))
            {
                lock (_lruLock)
                {
                    if (_thumbnailSourceCache.ContainsKey(key))
                    {
                        _thumbnailSourceCache[key] = source;
                        return;
                    }

                    while (_thumbnailSourceKeys.Count >= MaxThumbnailCacheEntries && _thumbnailSourceKeys.First != null)
                    {
                        string oldestKey = _thumbnailSourceKeys.First.Value;
                        _thumbnailSourceKeys.RemoveFirst();
                        _thumbnailSourceCache.TryRemove(oldestKey, out _);
                    }

                    _thumbnailSourceCache[key] = source;
                    _thumbnailSourceKeys.AddLast(key);
                }
                return;
            }

            int limit = Math.Clamp(ConfigService.Current.Cache.MaxEntries, 200, MaxCacheEntries);
            lock (_lruLock)
            {
                if (_imageSourceCache.ContainsKey(key))
                {
                    _imageSourceCache[key] = source;
                    return;
                }

                while (_imageSourceKeys.Count >= limit && _imageSourceKeys.First != null)
                {
                    string oldestKey = _imageSourceKeys.First.Value;
                    _imageSourceKeys.RemoveFirst();
                    _imageSourceCache.TryRemove(oldestKey, out _);
                }

                _imageSourceCache[key] = source;
                _imageSourceKeys.AddLast(key);
            }
        }

        public void TrimCache(int maxKeep = 200)
        {
            int thumbKeep = Math.Min(maxKeep, 80);
            lock (_lruLock)
            {
                while (_lruKeys.Count > maxKeep && _lruKeys.First != null)
                {
                    string oldestKey = _lruKeys.First.Value;
                    _lruKeys.RemoveFirst();
                    _lruCache.TryRemove(oldestKey, out _);
                }
                while (_imageSourceKeys.Count > maxKeep && _imageSourceKeys.First != null)
                {
                    string oldestKey = _imageSourceKeys.First.Value;
                    _imageSourceKeys.RemoveFirst();
                    _imageSourceCache.TryRemove(oldestKey, out _);
                }
                while (_thumbnailSourceKeys.Count > thumbKeep && _thumbnailSourceKeys.First != null)
                {
                    string oldestKey = _thumbnailSourceKeys.First.Value;
                    _thumbnailSourceKeys.RemoveFirst();
                    _thumbnailSourceCache.TryRemove(oldestKey, out _);
                }
            }
        }

        public void TrimThumbnailCache(int maxKeep = 80)
        {
            lock (_lruLock)
            {
                while (_thumbnailSourceKeys.Count > maxKeep && _thumbnailSourceKeys.First != null)
                {
                    string oldestKey = _thumbnailSourceKeys.First.Value;
                    _thumbnailSourceKeys.RemoveFirst();
                    _thumbnailSourceCache.TryRemove(oldestKey, out _);
                }
            }
        }

        private readonly ConcurrentDictionary<string, System.Threading.Tasks.Task<SoftwareBitmapSource>> _pendingSourceTasks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// UIスレッドへディスパッチし、成功すれば bitmap / semaphore の所有権を引き継ぐ。
        /// </summary>
        private bool SetIconToItem(
            FileItem item,
            SoftwareBitmap softwareBitmap,
            string cacheKey,
            bool disposeAfterUpload,
            bool releaseSemaphore,
            int gen)
        {
            if (_dispatcherQueue == null)
            {
                _initEvent.Wait(2000);
            }

            if (_dispatcherQueue == null)
            {
                return false;
            }

            if (_thumbnailSourceCache.TryGetValue(cacheKey, out var existingThumb) ||
                _imageSourceCache.TryGetValue(cacheKey, out existingThumb))
            {
                if (_dispatcherQueue.HasThreadAccess)
                {
                    if (gen == Volatile.Read(ref _queueGeneration))
                        item.Icon = existingThumb;
                }
                else
                {
                    _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        if (gen == Volatile.Read(ref _queueGeneration))
                            item.Icon = existingThumb;
                    });
                }
                return false;
            }

            bool enqueued = _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    if (gen != Volatile.Read(ref _queueGeneration))
                    {
                        return;
                    }

                    if (_thumbnailSourceCache.TryGetValue(cacheKey, out var cachedSource) ||
                        _imageSourceCache.TryGetValue(cacheKey, out cachedSource))
                    {
                        item.Icon = cachedSource;
                        return;
                    }

                    System.Threading.Tasks.Task<SoftwareBitmapSource> task;
                    bool isOwnerOfTask = false;
                    lock (_pendingSourceTasks)
                    {
                        if (!_pendingSourceTasks.TryGetValue(cacheKey, out task!))
                        {
                            task = CreateSoftwareBitmapSourceAsync(softwareBitmap, cacheKey);
                            _pendingSourceTasks[cacheKey] = task;
                            isOwnerOfTask = true;
                        }
                    }

                    var source = await task;
                    if (gen == Volatile.Read(ref _queueGeneration))
                    {
                        item.Icon = source;
                    }
                    _ = isOwnerOfTask;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[IconThumbnailService] SetIconToItem error: {ex.Message}");
                }
                finally
                {
                    if (disposeAfterUpload)
                    {
                        try { softwareBitmap.Dispose(); } catch { }
                    }
                    if (releaseSemaphore)
                    {
                        _inFlightSemaphore.Release();
                    }
                }
            });

            return enqueued;
        }

        private async System.Threading.Tasks.Task<SoftwareBitmapSource> CreateSoftwareBitmapSourceAsync(SoftwareBitmap softwareBitmap, string cacheKey)
        {
            try
            {
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(softwareBitmap);
                AddSourceToCache(cacheKey, source);
                return source;
            }
            finally
            {
                _pendingSourceTasks.TryRemove(cacheKey, out _);
            }
        }
    }
}
