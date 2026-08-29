using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FastExplorer.Core;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem : INotifyPropertyChanged, IDisposable
    {
        private string _header = "ホーム";
        private string _currentPath = string.Empty;
        private string _filterText = string.Empty;
        private bool _canGoBack;
        private bool _canGoForward;
        private bool _canGoUp;
        private bool _isLoading;
        private string _statusText = string.Empty;

        private SortColumn _currentSortColumn = SortColumn.Name;
        private bool _isSortAscending = true;
        private FolderViewMode _viewMode = FolderViewMode.Details;
        private ViewScaleLevel _viewScale = ViewScaleLevel.Normal;

        private readonly List<string> _backStack = [];
        private readonly List<string> _forwardStack = [];
        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _debounceTimer;
        private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public Microsoft.UI.Dispatching.DispatcherQueue? DispatcherQueue
        {
            get => _dispatcherQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            set => _dispatcherQueue = value;
        }

        public ObservableCollection<FileItem> Items { get; } = [];
        public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];
        private readonly List<FileItem> _allItems = [];
        private long _allItemsTotalBytes;

        public string? PendingSelectedItemName { get; set; }
        public event Action<NavigationTabItem, string>? ItemSelectionRequested;

        public string Header
        {
            get => _header;
            set => SetField(ref _header, value);
        }

        public string CurrentPath
        {
            get => _currentPath;
            set => SetField(ref _currentPath, value);
        }

        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetField(ref _filterText, value))
                {
                    ApplyFilter();
                }
            }
        }

        public bool CanGoBack
        {
            get => _canGoBack;
            private set => SetField(ref _canGoBack, value);
        }

        public bool CanGoForward
        {
            get => _canGoForward;
            private set => SetField(ref _canGoForward, value);
        }

        public bool CanGoUp
        {
            get => _canGoUp;
            private set => SetField(ref _canGoUp, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public SortColumn CurrentSortColumn
        {
            get => _currentSortColumn;
            set => SetField(ref _currentSortColumn, value);
        }

        public bool IsSortAscending
        {
            get => _isSortAscending;
            set => SetField(ref _isSortAscending, value);
        }

        public FolderViewMode ViewMode
        {
            get => _viewMode;
            set => SetField(ref _viewMode, value);
        }

        public ViewScaleLevel ViewScale
        {
            get => _viewScale;
            set => SetField(ref _viewScale, value);
        }

        private int _customSize = 48;
        public int CustomSize
        {
            get => _customSize;
            set => SetField(ref _customSize, value);
        }

        public NavigationTabItem()
        {
            if (Enum.TryParse<FolderViewMode>(ConfigService.Current.Ui.DefaultViewMode, true, out var defaultMode))
            {
                _viewMode = defaultMode;
            }

            QuickAccessService.PinnedItemsChanged += OnQuickAccessPinnedChanged;
        }

        private void OnQuickAccessPinnedChanged()
        {
            if (CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    Refresh();
                });
            }
        }

        public event Action<NavigationTabItem>? Navigated;

        public void NavigateTo(string path, bool recordHistory = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string normalizedPath;

            if (path.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = "Home";
                Header = "ホーム";
            }
            else if (path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) || path.Equals("PC", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = "ThisPC";
                Header = "PC";
            }
            else if (RecycleBinService.IsRecycleBinPath(path))
            {
                normalizedPath = RecycleBinService.RecycleBinUri;
                Header = "ごみ箱";
            }
            else if (path.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || path.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = "shell:NetworkPlacesFolder";
                Header = "ネットワーク";
            }
            else if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = path;
                Header = path;
            }
            else if (path.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = path.TrimEnd('\\');
                var parts = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                Header = parts.Length > 0 ? parts[^1] : "Linux";
            }
            else if (Services.ArchiveService.IsArchiveOrSubPath(path, out string archiveFile, out string internalSubPath))
            {
                normalizedPath = path;
                if (string.IsNullOrEmpty(internalSubPath))
                {
                    Header = Path.GetFileName(archiveFile);
                }
                else
                {
                    string subName = Path.GetFileName(internalSubPath.Replace('/', '\\'));
                    Header = string.IsNullOrEmpty(subName) ? Path.GetFileName(archiveFile) : subName;
                }
            }
            else
            {
                try
                {
                    normalizedPath = Path.GetFullPath(path);
                    var dirInfo = new DirectoryInfo(normalizedPath);
                    Header = string.IsNullOrEmpty(dirInfo.Name) ? normalizedPath : dirInfo.Name;
                }
                catch
                {
                    normalizedPath = path;
                    Header = path;
                }
            }

            if (recordHistory && !string.IsNullOrEmpty(_currentPath) && !_currentPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                _backStack.Add(_currentPath);
                _forwardStack.Clear();
            }

            ResolveFolderViewSetting(normalizedPath);
            CurrentPath = normalizedPath;
            UpdateBreadcrumbs(normalizedPath);
            UpdateNavigationState();
            SetupWatcher(normalizedPath);
            LoadItems();
            Navigated?.Invoke(this);
        }

        public void ResolveFolderViewSetting(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            string normPath = FastExplorer.Helpers.PathHelper.NormalizeFolderPath(path);
            if (ConfigService.Current.FolderViewSettings.TryGetValue(normPath, out var setting) ||
                ConfigService.Current.FolderViewSettings.TryGetValue(path, out setting))
            {
                if (Enum.TryParse<FolderViewMode>(setting.ViewMode, true, out var mode))
                {
                    _viewMode = mode;
                }
                _viewScale = (ViewScaleLevel)Math.Clamp(setting.ViewScale, 0, 3);
                _customSize = setting.CustomSize > 0 ? setting.CustomSize : 48;
                return;
            }

            // PC (ThisPC) の場合はタイル表示（デバイスとドライブ）を初期値に
            if (path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                _viewMode = FolderViewMode.Tiles;
                _viewScale = ViewScaleLevel.Normal;
                _customSize = 48;
                return;
            }

            // 画像フォルダー（パスやフォルダー名にピクチャ・写真・イラスト等が含まれる）の場合は大アイコンを初期値に
            if (FastExplorer.Helpers.FolderTypeHelper.IsImageFolderByPath(path))
            {
                _viewMode = FolderViewMode.LargeIcons;
                _viewScale = ViewScaleLevel.Normal;
                _customSize = 80;
                return;
            }

            // それ以外は全体デフォルト設定
            if (Enum.TryParse<FolderViewMode>(ConfigService.Current.Ui.DefaultViewMode, true, out var defaultMode))
            {
                _viewMode = defaultMode;
            }
            _viewScale = ViewScaleLevel.Normal;
            _customSize = 48;
        }

        public void SortBy(SortColumn column)
        {
            if (CurrentSortColumn == column)
            {
                IsSortAscending = !IsSortAscending;
            }
            else
            {
                CurrentSortColumn = column;
                IsSortAscending = true;
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            SyncFilteredItems();
        }

        public void RecalculateTotalBytes()
        {
            long total = 0;
            foreach (var item in _allItems)
            {
                if (!item.IsDirectory)
                {
                    total += item.SizeInBytes;
                }
            }
            _allItemsTotalBytes = total;
        }

        public void UpdateStatusText(int selectedCount = 0, long selectedBytes = 0)
        {
            if (selectedCount > 0)
            {
                StatusText = $"{_allItems.Count} 個の項目 | {selectedCount} 個を選択 ({FileItem.FormatFileSize(selectedBytes)})";
            }
            else
            {
                StatusText = $"{_allItems.Count} 個の項目 ({FileItem.FormatFileSize(_allItemsTotalBytes)})";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> _eventArgsCache = new();

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (PropertyChanged != null && propertyName != null)
            {
                var args = _eventArgsCache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));
                PropertyChanged(this, args);
            }
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
