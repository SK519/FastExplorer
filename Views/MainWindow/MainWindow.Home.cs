using System;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Home View Logic

        private bool _homeEventsInitialized;

        private void InitializeHomeEvents()
        {
            if (_homeEventsInitialized) return;
            _homeEventsInitialized = true;

            if (HomeView != null)
            {
                HomeView.ItemNavigateRequested += path => CurrentTab?.NavigateTo(path);
                HomeView.ItemContextMenuRequested += (element, pt, item) => ShowItemContextMenu(element, pt, [item]);
                HomeView.PathContextMenuRequested += (element, pt, path, isDir) => ShowItemContextMenuForPath(element, pt, path, isDirectory: isDir);
                HomeView.DragItemsStarting += FileList_DragItemsStarting;
                HomeView.QuickAccessDragOver += HomeQuickAccess_DragOver;
                HomeView.QuickAccessDrop += HomeQuickAccess_Drop;
            }

            QuickAccessService.RecentItemsChanged += OnQuickAccessRecentItemsChanged;
            QuickAccessService.PinnedItemsChanged += OnQuickAccessPinnedItemsForHomeChanged;
        }

        private void OnQuickAccessRecentItemsChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CurrentTab != null && CurrentTab.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    HomeView?.UpdateHomeTabContent();
                }
            });
        }

        private void OnQuickAccessPinnedItemsForHomeChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CurrentTab != null && CurrentTab.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshHomeView();
                }
            });
        }

        public void UpdateHomeViewVisibility()
        {
            if (CurrentTab == null) return;

            InitializeHomeEvents();

            bool isHome = CurrentTab.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase);

            if (isHome)
            {
                FileListContainer.Visibility = Visibility.Collapsed;
                if (FileListHeader != null) FileListHeader.Visibility = Visibility.Collapsed;
                if (HomeView != null) HomeView.Visibility = Visibility.Visible;
                RefreshHomeView();
            }
            else
            {
                if (HomeView != null) HomeView.Visibility = Visibility.Collapsed;
                FileListContainer.Visibility = Visibility.Visible;
                if (FileListHeader != null)
                {
                    FileListHeader.Visibility = (CurrentTab.ViewMode == FolderViewMode.Details) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            UpdatePreviewPane();
        }

        public void RefreshHomeView()
        {
            if (HomeView == null || HomeView.Visibility == Visibility.Collapsed) return;
            HomeView.RefreshHomeView();
        }

        #endregion
    }
}
