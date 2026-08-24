using System;
using System.IO;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
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
                _watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 65536,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnFolderChanged;
                _watcher.Deleted += OnFolderChanged;
                _watcher.Renamed += OnFolderChanged;
                _watcher.Changed += OnFolderChanged;
                _watcher.Error += OnWatcherError;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavigationTabItem] SetupWatcher error for {path}: {ex.Message}");
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // バッファオーバーフロー等のエラー時はウォッチャーを再起動して手動リフレッシュ
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                SetupWatcher(CurrentPath);
                Refresh();
            });
        }

        private void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Threading.Timer(_ =>
                {
                    DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                    {
                        Refresh();
                    });
                }, null, 150, System.Threading.Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(150, System.Threading.Timeout.Infinite);
            }
        }

        public void DisposeWatcher()
        {
            if (_debounceTimer != null)
            {
                try { _debounceTimer.Dispose(); } catch { }
                _debounceTimer = null;
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
