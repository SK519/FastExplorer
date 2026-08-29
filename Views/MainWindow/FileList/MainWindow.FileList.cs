using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        public ListViewBase ActiveListControl => (CurrentTab?.ViewMode switch
        {
            FolderViewMode.Details or FolderViewMode.Content => (ListViewBase)FileListView,
            _ => (ListViewBase)FileGridView
        }) ?? FileListView;

        private bool _isSelectionVisualsUpdatePending = false;

        public void RequestSelectionVisualsUpdate()
        {
            if (_isSelectionVisualsUpdatePending) return;
            _isSelectionVisualsUpdatePending = true;

            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _isSelectionVisualsUpdatePending = false;
                UpdateSelectionVisuals();
            });
        }

        public void InitializeFileListEvents()
        {
            InitializeMarqueeSelection();
            FileOperationService.ClipboardStateChanged += OnClipboardStateChanged;
            FileItem.SelectionVisualsCallback = () =>
            {
                RequestSelectionVisualsUpdate();
            };
            HookFileListScrollSync();

            // Handled = true にされても確実にタップ・クリックを捕捉する
            FileListView.AddHandler(UIElement.TappedEvent, new TappedEventHandler(FileListView_Tapped), true);
            FileListView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(FileListView_PointerPressed), true);
            FileGridView.AddHandler(UIElement.TappedEvent, new TappedEventHandler(FileGridView_Tapped), true);
            FileGridView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(FileGridView_PointerPressed), true);
            SidebarContainerGrid.AddHandler(UIElement.TappedEvent, new TappedEventHandler(SidebarContainer_Tapped), true);
            SidebarList.AddHandler(UIElement.TappedEvent, new TappedEventHandler(SidebarList_Tapped), true);
            SidebarList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SidebarList_PointerPressed), true);
        }

        private ScrollViewer? _fileListScrollViewer;

        private void HookFileListScrollSync()
        {
            FileListView.Loaded += (s, e) =>
            {
                _fileListScrollViewer = FileListView.FindDescendant<ScrollViewer>();
                if (_fileListScrollViewer != null)
                {
                    _fileListScrollViewer.ViewChanged += (sender, args) =>
                    {
                        var headerSv = FileListHeader?.FindDescendant<ScrollViewer>();
                        if (_fileListScrollViewer != null && headerSv != null)
                        {
                            headerSv.ChangeView(_fileListScrollViewer.HorizontalOffset, null, null, true);
                        }
                    };
                }
            };
        }

        private void OnClipboardStateChanged()
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                UpdateCutVisuals();
                UpdateActionToolbarButtons();
            });
        }

        public void UpdateCutVisuals()
        {
            if (CurrentTab?.Items != null)
            {
                foreach (var item in CurrentTab.Items)
                {
                    item.IsCut = FileOperationService.IsPathCut(item.FullPath);
                }
            }
        }

        #region File List Events & Key Handlers

        private void FileListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var listViewItem = dep.FindParent<ListViewItem>();
                if (listViewItem?.Content is FileItem item)
                {
                    OpenFileItem(item);
                    return;
                }
            }
            OpenSelectedItem();
        }

        private void FileGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var gridViewItem = dep.FindParent<GridViewItem>();
                if (gridViewItem?.Content is FileItem item)
                {
                    OpenFileItem(item);
                    return;
                }
            }
            OpenSelectedItem();
        }

        private void FileList_ItemClick(object sender, ItemClickEventArgs e)
        {
        }

        public void SelectSingleItem(FileItem target)
        {
            if (CurrentTab?.Items == null) return;
            try
            {
                _isSynchronizingSelection = true;
                foreach (var item in CurrentTab.Items)
                {
                    bool isTarget = (item == target);
                    if (item.IsSelected != isTarget)
                    {
                        item.SetIsSelectedSilently(isTarget);
                    }
                }
                var list = ActiveListControl;
                if (list != null)
                {
                    list.SelectedItems.Clear();
                    if (target != null)
                    {
                        list.SelectedItems.Add(target);
                    }
                }
            }
            finally
            {
                _isSynchronizingSelection = false;
            }
            RequestSelectionVisualsUpdate();
        }

        public void ClearAllSelections()
        {
            try
            {
                _isSynchronizingSelection = true;
                if (CurrentTab?.Items != null)
                {
                    foreach (var item in CurrentTab.Items)
                    {
                        if (item.IsSelected)
                        {
                            item.SetIsSelectedSilently(false);
                        }
                    }
                }
                ActiveListControl?.SelectedItems?.Clear();
            }
            finally
            {
                _isSynchronizingSelection = false;
            }
            RequestSelectionVisualsUpdate();
        }

        private void SidebarContainer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ClearAllSelections();
        }

        private void SidebarList_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var item = dep.FindParent<ListViewItem>();
                if (item == null)
                {
                    ClearAllSelections();
                }
            }
        }

        private FileItem? _itemOnPointerPressed;
        private bool _wasSelectedOnPointerPressed;
        private int _selectionCountOnPointerPressed;

        private void FileListView_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var listViewItem = dep.FindParent<ListViewItem>();
                if (listViewItem == null)
                {
                    ClearAllSelections();
                }
                else if (listViewItem.Content is FileItem item)
                {
                    // CheckBox や RenameBox (TextBox) のクリック時は個別の動作に任せる
                    if (dep.FindParent<CheckBox>() == null && dep.FindParent<TextBox>() == null)
                    {
                        if (!IsCtrlPressed() && !IsShiftPressed())
                        {
                            if (_wasSelectedOnPointerPressed)
                            {
                                // 既にチェックがついていたアイテムの行（名前・更新日時・種類・サイズ等）をクリックした場合はチェックを解除
                                item.IsSelected = false;
                                ActiveListControl?.SelectedItems?.Remove(item);
                                UpdateSelectionVisuals();
                            }
                            else
                            {
                                // チェックがついていなかったアイテムをクリックした場合はそのアイテムを選択
                                SelectSingleItem(item);
                            }
                        }
                    }
                }
            }
            _itemOnPointerPressed = null;
            _wasSelectedOnPointerPressed = false;
            _selectionCountOnPointerPressed = 0;
        }

        private void FileGridView_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var gridViewItem = dep.FindParent<GridViewItem>();
                if (gridViewItem == null)
                {
                    ClearAllSelections();
                }
                else if (gridViewItem.Content is FileItem item)
                {
                    // CheckBox や RenameBox (TextBox) のクリック時は個別の動作に任せる
                    if (dep.FindParent<CheckBox>() == null && dep.FindParent<TextBox>() == null)
                    {
                        if (!IsCtrlPressed() && !IsShiftPressed())
                        {
                            if (_wasSelectedOnPointerPressed)
                            {
                                // 既にチェックがついていたアイテムの行をクリックした場合はチェックを解除
                                item.IsSelected = false;
                                ActiveListControl?.SelectedItems?.Remove(item);
                                UpdateSelectionVisuals();
                            }
                            else
                            {
                                // チェックがついていなかったアイテムをクリックした場合はそのアイテムを選択
                                SelectSingleItem(item);
                            }
                        }
                    }
                }
            }
            _itemOnPointerPressed = null;
            _wasSelectedOnPointerPressed = false;
            _selectionCountOnPointerPressed = 0;
        }

        private void FileListView_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var prop = e.GetCurrentPoint(FileListView).Properties;

            // マウスホイール (中ボタン) クリック: フォルダー/アーカイブを新しいタブで開く
            if (prop.IsMiddleButtonPressed)
            {
                if (e.OriginalSource is DependencyObject dep)
                {
                    var listViewItem = dep.FindParent<ListViewItem>();
                    if (listViewItem?.Content is FileItem item && (item.IsDirectory || ArchiveService.IsSupportedArchive(item.FullPath) || item.FullPath.StartsWith("shell:")))
                    {
                        CreateNewTab(item.FullPath);
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (!prop.IsLeftButtonPressed) return;

            if (e.OriginalSource is DependencyObject depLeft)
            {
                var listViewItem = depLeft.FindParent<ListViewItem>();
                if (listViewItem == null)
                {
                    _itemOnPointerPressed = null;
                    _wasSelectedOnPointerPressed = false;
                    _selectionCountOnPointerPressed = 0;
                    ClearAllSelections();
                }
                else if (listViewItem.Content is FileItem item)
                {
                    _itemOnPointerPressed = item;
                    _wasSelectedOnPointerPressed = item.IsSelected;
                    _selectionCountOnPointerPressed = ActiveListControl?.SelectedItems?.Count ?? 0;
                }
            }
        }

        private void FileGridView_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var prop = e.GetCurrentPoint(FileGridView).Properties;

            // マウスホイール (中ボタン) クリック: フォルダー/アーカイブを新しいタブで開く
            if (prop.IsMiddleButtonPressed)
            {
                if (e.OriginalSource is DependencyObject dep)
                {
                    var gridViewItem = dep.FindParent<GridViewItem>();
                    if (gridViewItem?.Content is FileItem item && (item.IsDirectory || ArchiveService.IsSupportedArchive(item.FullPath) || item.FullPath.StartsWith("shell:")))
                    {
                        CreateNewTab(item.FullPath);
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (!prop.IsLeftButtonPressed) return;

            if (e.OriginalSource is DependencyObject depLeft)
            {
                var gridViewItem = depLeft.FindParent<GridViewItem>();
                if (gridViewItem == null)
                {
                    _itemOnPointerPressed = null;
                    _wasSelectedOnPointerPressed = false;
                    _selectionCountOnPointerPressed = 0;
                    ClearAllSelections();
                }
                else if (gridViewItem.Content is FileItem item)
                {
                    _itemOnPointerPressed = item;
                    _wasSelectedOnPointerPressed = item.IsSelected;
                    _selectionCountOnPointerPressed = ActiveListControl?.SelectedItems?.Count ?? 0;
                }
            }
        }

        private void FileListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var listViewItem = dep.FindParent<ListViewItem>();
                if (listViewItem != null && listViewItem.Content is FileItem item)
                {
                    if (!FileListView.SelectedItems.Contains(item))
                    {
                        FileListView.SelectedItem = item;
                    }
                }
                else
                {
                    FileListView.SelectedItems.Clear();
                }
                UpdateActionToolbarButtons();
            }
        }

        private void FileGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var gridViewItem = dep.FindParent<GridViewItem>();
                if (gridViewItem != null && gridViewItem.Content is FileItem item)
                {
                    if (!FileGridView.SelectedItems.Contains(item))
                    {
                        FileGridView.SelectedItem = item;
                    }
                }
                else
                {
                    FileGridView.SelectedItems.Clear();
                }
                UpdateActionToolbarButtons();
            }
        }

        private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            HandleSelectionChanged();
        }

        private void FileGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            HandleSelectionChanged();
        }

        private bool _isSynchronizingSelection = false;

        private void HandleSelectionChanged()
        {
            if (CurrentTab == null || _isSynchronizingSelection) return;

            try
            {
                _isSynchronizingSelection = true;
                var list = ActiveListControl;
                if (list != null && CurrentTab.Items != null)
                {
                    var selectedSet = new HashSet<FileItem>(list.SelectedItems.OfType<FileItem>());
                    foreach (var item in CurrentTab.Items)
                    {
                        bool shouldBeSelected = selectedSet.Contains(item);
                        if (item.IsSelected != shouldBeSelected)
                        {
                            item.SetIsSelectedSilently(shouldBeSelected);
                        }
                    }
                }
            }
            finally
            {
                _isSynchronizingSelection = false;
            }

            RequestSelectionVisualsUpdate();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is FileItem item)
            {
                bool isChecked = cb.IsChecked == true;
                item.IsSelected = isChecked;

                var list = ActiveListControl;
                if (list != null)
                {
                    _isSynchronizingSelection = true;
                    try
                    {
                        if (isChecked)
                        {
                            if (!list.SelectedItems.Contains(item))
                            {
                                list.SelectedItems.Add(item);
                            }
                        }
                        else
                        {
                            if (list.SelectedItems.Contains(item))
                            {
                                list.SelectedItems.Remove(item);
                            }
                        }
                    }
                    finally
                    {
                        _isSynchronizingSelection = false;
                    }
                }

                RequestSelectionVisualsUpdate();
            }
        }

        public List<FileItem> GetCurrentlySelectedItems()
        {
            if (CurrentTab?.Items != null)
            {
                var selected = CurrentTab.Items.Where(i => i.IsSelected).ToList();
                if (selected.Count > 0) return selected;
            }
            return ActiveListControl?.SelectedItems?.OfType<FileItem>()?.ToList() ?? [];
        }

        private void UpdateSelectionVisuals()
        {
            if (CurrentTab == null) return;

            var selected = GetCurrentlySelectedItems();
            int selectedCount = selected.Count;
            long totalBytes = 0;
            for (int i = 0; i < selectedCount; i++)
            {
                var item = selected[i];
                if (!item.IsDirectory)
                {
                    totalBytes += item.SizeInBytes;
                }
            }

            CurrentTab.UpdateStatusText(selectedCount, totalBytes);
            if (StatusBar != null) StatusBar.StatusText = CurrentTab.StatusText;

            bool hasSelection = selectedCount > 0;
            bool isSingle = selectedCount == 1;
            bool canPaste = FileOperationService.CanPaste();
            bool isThisPC = CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase);
            bool isRecycleBin = RecycleBinService.IsRecycleBinPath(CurrentTab.CurrentPath);

            ActionToolbar?.UpdateButtonsState(hasSelection, isSingle, isThisPC, canPaste, isRecycleBin);
            FileListHeader?.UpdateHeaderForRecycleBin(isRecycleBin);
            FileListHeader?.UpdateSelectAllCheckBox(selectedCount, CurrentTab.Items?.Count ?? 0);

            if (PreviewPane != null && IsPreviewPaneVisible)
            {
                PreviewPane.UpdatePreview(selected, CurrentTab.CurrentPath);
            }
        }

        public void UpdateActionToolbarButtons()
        {
            UpdateSelectionVisuals();
        }

        #endregion
    }
}
