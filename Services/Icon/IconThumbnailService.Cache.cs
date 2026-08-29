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

            if (item.FullPath.StartsWith("::") || item.FullPath.StartsWith("shell:") || item.FullPath.StartsWith("urn:"))
            {
                return "shellitem::" + item.FullPath.ToLowerInvariant();
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
    }
}
