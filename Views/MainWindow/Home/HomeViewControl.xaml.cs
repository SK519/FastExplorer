using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FastExplorer.Views.MainWindow.Home
{
    public sealed partial class HomeViewControl : UserControl
    {
        private int _selectedHomeTab = 0;
        private bool _isQuickAccessSectionExpanded = true;

        public event Action<string>? ItemNavigateRequested;
        public event Action<UIElement, Point?, FileItem>? ItemContextMenuRequested;
        public event Action<UIElement, Point?, string, bool>? PathContextMenuRequested;
        public event DragItemsStartingEventHandler? DragItemsStarting;
        public event DragEventHandler? QuickAccessDragOver;
        public event DragEventHandler? QuickAccessDrop;

        public HomeViewControl()
        {
            this.InitializeComponent();
        }

        private int _loadVersion = 0;

        public void RefreshHomeView()
        {
            int version = System.Threading.Interlocked.Increment(ref _loadVersion);

            // バックグラウンドでピン留めフォルダーを取得して非同期でUIに反映
            System.Threading.Tasks.Task.Run(() =>
            {
                var pinnedFolders = QuickAccessService.GetPinnedFolderItems();
                foreach (var item in pinnedFolders)
                {
                    IconThumbnailService.Instance.ApplyImmediateDefaultIcon(item);
                    IconThumbnailService.Instance.Enqueue(item);
                }

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (version == _loadVersion)
                    {
                        HomeQuickAccessGridView.ItemsSource = pinnedFolders;
                    }
                });
            });

            UpdateHomeTabContent();
        }

        private void ToggleQuickAccessSection_Click(object sender, RoutedEventArgs e)
        {
            _isQuickAccessSectionExpanded = !_isQuickAccessSectionExpanded;
            HomeQuickAccessGridView.Visibility = _isQuickAccessSectionExpanded ? Visibility.Visible : Visibility.Collapsed;
            IconQuickAccessChevron.Glyph = _isQuickAccessSectionExpanded ? "\uE70D" : "\uE76C";
        }

        private void HomeQuickAccessGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FileItem item)
            {
                ItemNavigateRequested?.Invoke(item.FullPath);
            }
            else if (e.ClickedItem is QuickAccessFolderItem qaItem)
            {
                ItemNavigateRequested?.Invoke(qaItem.Path);
            }
        }

        private void HomeQuickAccessGridView_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            string? targetPath = null;
            bool isDir = true;
            if (e.OriginalSource is FrameworkElement fe)
            {
                if (fe.DataContext is FileItem item)
                {
                    e.Handled = true;
                    var point = new Windows.Foundation.Point(0, 0);
                    bool hasPoint = e.TryGetPosition(sender, out point);
                    ItemContextMenuRequested?.Invoke(sender, hasPoint ? point : null, item);
                    return;
                }
                else if (fe.DataContext is QuickAccessFolderItem qa)
                {
                    targetPath = qa.Path;
                    isDir = true;
                }

                if (!string.IsNullOrEmpty(targetPath))
                {
                    e.Handled = true;
                    var point = new Windows.Foundation.Point(0, 0);
                    bool hasPoint = e.TryGetPosition(sender, out point);
                    PathContextMenuRequested?.Invoke(sender, hasPoint ? point : null, targetPath, isDir);
                }
            }
        }

        private void HomeRecentListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FileItem item)
            {
                ItemNavigateRequested?.Invoke(item.FullPath);
            }
        }

        private void HomeRecentListView_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileItem item)
            {
                e.Handled = true;
                var point = new Windows.Foundation.Point(0, 0);
                bool hasPoint = e.TryGetPosition(sender, out point);
                ItemContextMenuRequested?.Invoke(sender, hasPoint ? point : null, item);
            }
        }

        private void HomeTabRecent_Click(object sender, RoutedEventArgs e) => SetHomeTab(0);
        private void HomeTabFavorites_Click(object sender, RoutedEventArgs e) => SetHomeTab(1);
        private void HomeTabShared_Click(object sender, RoutedEventArgs e) => SetHomeTab(2);

        private void SetHomeTab(int tabIndex)
        {
            _selectedHomeTab = tabIndex;

            ApplyPillButtonStyle(HomeTabBtnRecent, tabIndex == 0);
            ApplyPillButtonStyle(HomeTabBtnFavorites, tabIndex == 1);
            ApplyPillButtonStyle(HomeTabBtnShared, tabIndex == 2);

            UpdateHomeTabContent();
        }

        private static void ApplyPillButtonStyle(Button btn, bool isActive)
        {
            if (btn == null) return;
            if (isActive)
            {
                btn.Background = Views.Settings.SettingsControl.GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue));
                btn.Foreground = Views.Settings.SettingsControl.GetThemeBrush("TextOnAccentFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.White));
            }
            else
            {
                btn.Background = Views.Settings.SettingsControl.GetThemeBrush("SubtleFillColorTransparentBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent));
                btn.Foreground = Views.Settings.SettingsControl.GetThemeBrush("TextFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.White));
            }
        }

        public void UpdateHomeTabContent()
        {
            int currentTab = _selectedHomeTab;
            int version = _loadVersion;

            if (currentTab == 0)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    var recentItems = GetWindowsRecentFiles();
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (version == _loadVersion && _selectedHomeTab == 0)
                        {
                            if (recentItems.Count > 0)
                            {
                                HomeRecentEmptyStateGrid.Visibility = Visibility.Collapsed;
                                HomeRecentListView.Visibility = Visibility.Visible;
                                HomeRecentListView.ItemsSource = recentItems;
                            }
                            else
                            {
                                HomeRecentListView.Visibility = Visibility.Collapsed;
                                HomeRecentEmptyStateGrid.Visibility = Visibility.Visible;
                            }
                        }
                    });
                });
            }
            else
            {
                HomeRecentListView.Visibility = Visibility.Collapsed;
                HomeRecentEmptyStateGrid.Visibility = Visibility.Visible;
            }
        }

        private static List<FileItem> GetWindowsRecentFiles()
        {
            var list = new List<FileItem>();
            try
            {
                string recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (Directory.Exists(recentFolder))
                {
                    var files = new DirectoryInfo(recentFolder).GetFiles("*.lnk")
                        .OrderByDescending(f => f.LastWriteTime)
                        .Take(50);

                    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var file in files)
                    {
                        string targetPath = Core.Win32Interop.ResolveShortcut(file.FullName) ?? string.Empty;
                        if (string.IsNullOrEmpty(targetPath))
                        {
                            targetPath = file.FullName;
                        }

                        if (!seenPaths.Add(targetPath)) continue;

                        bool isDir = Directory.Exists(targetPath);
                        bool isFile = File.Exists(targetPath);

                        if (!isDir && !isFile) continue;

                        string name = Path.GetFileName(targetPath);
                        if (string.IsNullOrEmpty(name))
                        {
                            name = targetPath;
                        }

                        var item = new FileItem
                        {
                            Name = name,
                            FullPath = targetPath,
                            IsDirectory = isDir,
                            DateModified = file.LastWriteTime,
                            Subtitle = Path.GetDirectoryName(targetPath) ?? string.Empty,
                            FileType = isDir ? "フォルダー" : (Path.GetExtension(targetPath).ToUpperInvariant() + " ファイル")
                        };

                        IconThumbnailService.Instance.ApplyImmediateDefaultIcon(item);
                        IconThumbnailService.Instance.Enqueue(item);

                        list.Add(item);
                    }
                }
            }
            catch { }
            return list;
        }

        private void HomeOpenPrivacySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-general") { UseShellExecute = true });
            }
            catch { }
        }

        private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            DragItemsStarting?.Invoke(sender, e);
        }

        private void HomeQuickAccess_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            DragItemsStarting?.Invoke(sender, e);
        }

        private void HomeQuickAccess_DragOver(object sender, DragEventArgs e)
        {
            QuickAccessDragOver?.Invoke(sender, e);
        }

        private void HomeQuickAccess_Drop(object sender, DragEventArgs e)
        {
            QuickAccessDrop?.Invoke(sender, e);
        }
    }
}
