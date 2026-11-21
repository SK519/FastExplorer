using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Selection Synchronization & Visuals

        private bool _isSynchronizingSelection = false;

        public void SelectSingleItem(FileItem target)
        {
            if (CurrentTab?.Items == null) return;
            try
            {
                _isSynchronizingSelection = true;
                var list = ActiveListControl;
                if (list != null)
                {
                    // 以前選択されていたアイテムのみ解除 (全件ループを完全排除)
                    foreach (var obj in list.SelectedItems)
                    {
                        if (obj is FileItem prevItem && prevItem != target)
                        {
                            prevItem.SetIsSelectedSilently(false);
                        }
                    }
                    list.SelectedItems.Clear();
                    if (target != null)
                    {
                        target.SetIsSelectedSilently(true);
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
                var list = ActiveListControl;
                if (list?.SelectedItems != null)
                {
                    foreach (var obj in list.SelectedItems)
                    {
                        if (obj is FileItem item)
                        {
                            item.SetIsSelectedSilently(false);
                        }
                    }
                    list.SelectedItems.Clear();
                }
            }
            finally
            {
                _isSynchronizingSelection = false;
            }
            RequestSelectionVisualsUpdate();
        }

        private void HandleSelectionChanged(SelectionChangedEventArgs? e = null)
        {
            if (CurrentTab == null || _isSynchronizingSelection) return;

            try
            {
                _isSynchronizingSelection = true;
                var list = ActiveListControl;
                if (list != null)
                {
                    if (e != null && (e.AddedItems.Count > 0 || e.RemovedItems.Count > 0))
                    {
                        // O(k): 変更のあったアイテムのみ同期（全件ループを完全排除）
                        foreach (var obj in e.AddedItems)
                        {
                            if (obj is FileItem addedItem)
                            {
                                addedItem.SetIsSelectedSilently(true);
                            }
                        }
                        foreach (var obj in e.RemovedItems)
                        {
                            if (obj is FileItem removedItem)
                            {
                                removedItem.SetIsSelectedSilently(false);
                            }
                        }
                    }
                    else if (CurrentTab.Items != null)
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
            }
            finally
            {
                _isSynchronizingSelection = false;
            }

            RequestSelectionVisualsUpdate();
        }

        public void SyncItemSelection(FileItem item, bool isSelected)
        {
            if (_isSynchronizingSelection) return;
            var list = ActiveListControl;
            if (list == null) return;

            try
            {
                _isSynchronizingSelection = true;
                if (isSelected)
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

            RequestSelectionVisualsUpdate();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is FileItem item)
            {
                bool isChecked = cb.IsChecked == true;
                item.IsSelected = isChecked;
                SyncItemSelection(item, isChecked);
            }
        }

        public List<FileItem> GetCurrentlySelectedItems()
        {
            var list = ActiveListControl;
            if (list?.SelectedItems != null && list.SelectedItems.Count > 0)
            {
                var result = new List<FileItem>(list.SelectedItems.Count);
                foreach (var obj in list.SelectedItems)
                {
                    if (obj is FileItem fi) result.Add(fi);
                }
                return result;
            }

            if (CurrentTab?.Items != null)
            {
                var selected = CurrentTab.Items.Where(i => i.IsSelected).ToList();
                if (selected.Count > 0) return selected;
            }
            return [];
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

            if (PreviewPane != null && IsPreviewPaneVisible && !_isMarqueeSelecting)
            {
                PreviewPane.UpdatePreview(selected, CurrentTab.CurrentPath);
            }

            if (!_isMarqueeSelecting && selectedCount > 0)
            {
                ScheduleShellMenuPrefetch(selected);
            }
        }

        public void UpdateActionToolbarButtons()
        {
            UpdateSelectionVisuals();
        }

        #endregion
    }
}
