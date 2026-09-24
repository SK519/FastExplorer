using System;
using System.IO;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
        private readonly object _watcherLock = new();

        private void SetupWatcher(string path)
        {
            DisposeWatcher();

            if (string.IsNullOrEmpty(path) ||
                path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
                RecycleBinService.IsRecycleBinPath(path) ||
                path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("::", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(path))
                return;

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 65536
                };

                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationTabItem] SetupWatcher error for {path}: {ex.Message}");
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // バッファオーバーフロー等のエラー時はウォッチャーを再起動して手動リフレッシュ
            var dq = DispatcherQueue;
            dq?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                SetupWatcher(CurrentPath);
                Refresh();
            });
        }

        private void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            lock (_watcherLock)
            {
                if (_debounceTimer == null)
                {
                    _debounceTimer = new System.Threading.Timer(OnDebounceTimerElapsed, null, 150, System.Threading.Timeout.Infinite);
                }
                else
                {
                    try
                    {
                        _debounceTimer.Change(150, System.Threading.Timeout.Infinite);
                    }
                    catch (ObjectDisposedException)
                    {
                        _debounceTimer = new System.Threading.Timer(OnDebounceTimerElapsed, null, 150, System.Threading.Timeout.Infinite);
                    }
                }
            }
        }

        private void OnDebounceTimerElapsed(object? state)
        {
            var dq = DispatcherQueue;
            dq?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                Refresh();
            });
        }

        public void DisposeWatcher()
        {
            lock (_watcherLock)
            {
                if (_debounceTimer != null)
                {
                    try { _debounceTimer.Dispose(); } catch { }
                    _debounceTimer = null;
                }
            }

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnFolderChanged;
                    _watcher.Deleted -= OnFolderChanged;
                    _watcher.Renamed -= OnFolderChanged;
                    _watcher.Changed -= OnFolderChanged;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                }
                catch { }
                _watcher = null;
            }
        }

        public void Dispose()
        {
            QuickAccessService.PinnedItemsChanged -= OnQuickAccessPinnedChanged;
            DisposeWatcher();
        }
    }
}
