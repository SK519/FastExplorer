using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu Invocation & Request Routing

        private List<FileItem>? _contextTargetItemsOverride;

        private void FileListView_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            HandleContextRequested(sender, args);
        }

        private void FileGridView_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            HandleContextRequested(sender, args);
        }

        public void ShowItemContextMenuForPath(UIElement targetElement, Windows.Foundation.Point? point, string path, bool isDirectory)
        {
            if (string.IsNullOrEmpty(path)) return;

            bool isShift = IsShiftPressed();
            if (isShift)
            {
                if (path.Equals("Home", StringComparison.OrdinalIgnoreCase) || path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Win32Interop.GetCursorPos(out var pt);
                var screenPoint = new Windows.Foundation.Point(pt.X, pt.Y);
                var hwnd = WindowHandle;
                ShellContextMenuService.ShowContextMenuAsync(hwnd, [path], screenPoint, isShift: true);
                return;
            }

            FileItem fileItem;
            if (path.Equals("Home", StringComparison.OrdinalIgnoreCase) || path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                fileItem = new FileItem
                {
                    Name = path.Equals("Home", StringComparison.OrdinalIgnoreCase) ? "ホーム" : "PC",
                    FullPath = path,
                    IsDirectory = true,
                    IsPinned = false
                };
            }
            else if (isDirectory || Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                fileItem = new FileItem
                {
                    Name = dirInfo.Name,
                    FullPath = path,
                    IsDirectory = true,
                    DateModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.Now,
                    IsPinned = QuickAccessService.IsPinned(path)
                };
            }
            else
            {
                var fileInfo = new FileInfo(path);
                fileItem = new FileItem
                {
                    Name = fileInfo.Name,
                    FullPath = path,
                    IsDirectory = false,
                    SizeInBytes = fileInfo.Exists ? fileInfo.Length : 0,
                    DateModified = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.Now,
                    IsPinned = QuickAccessService.IsPinned(path)
                };
            }

            ShowItemContextMenu(targetElement, point, [fileItem]);
        }

        public void ShowItemContextMenu(UIElement targetElement, Windows.Foundation.Point? point, List<FileItem> items)
        {
            if (items.Count == 0) return;
            _contextTargetItemsOverride = items;

            var fe = targetElement as FrameworkElement ?? ActiveListControl;
            if (point.HasValue)
            {
                ItemContextMenu.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point.Value });
            }
            else
            {
                ItemContextMenu.ShowAt(fe);
            }
        }

        private void HandleContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (CurrentTab == null) return;
            _contextTargetItemsOverride = null;

            List<string> targetPaths = [];
            if (args.OriginalSource is DependencyObject dep)
            {
                var listViewItem = dep.FindParent<ListViewItem>();
                var gridViewItem = dep.FindParent<GridViewItem>();

                FileItem? clickedItem = null;
                if (listViewItem != null)
                {
                    clickedItem = listViewItem.Content as FileItem ?? listViewItem.DataContext as FileItem;
                }
                else if (gridViewItem != null)
                {
                    clickedItem = gridViewItem.Content as FileItem ?? gridViewItem.DataContext as FileItem;
                }
                else if (dep is FrameworkElement fe)
                {
                    clickedItem = fe.DataContext as FileItem ?? fe.Tag as FileItem;
                }

                if (clickedItem != null)
                {
                    if (!ActiveListControl.SelectedItems.Contains(clickedItem))
                    {
                        ActiveListControl.SelectedItems.Clear();
                        ActiveListControl.SelectedItem = clickedItem;
                    }
                    UpdateActionToolbarButtons();

                    if (ActiveListControl.SelectedItems.Contains(clickedItem) && ActiveListControl.SelectedItems.Count > 1)
                    {
                        targetPaths = ActiveListControl.SelectedItems.OfType<FileItem>().Select(x => x.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    }
                    else if (!string.IsNullOrEmpty(clickedItem.FullPath))
                    {
                        targetPaths = [clickedItem.FullPath];
                    }
                }
            }

            if (targetPaths.Count == 0 && ActiveListControl.SelectedItems.Count > 0)
            {
                targetPaths = ActiveListControl.SelectedItems.OfType<FileItem>().Select(x => x.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }

            var point = new Windows.Foundation.Point(0, 0);
            bool hasPoint = args.TryGetPosition(sender, out point);
            var targetElement = sender as FrameworkElement ?? ActiveListControl;

            bool isShift = IsShiftPressed();
            if (isShift)
            {
                // Shift + 右クリック時は別スレッド (STA) で OS 標準メニューを開く
                args.Handled = true;
                Win32Interop.GetCursorPos(out var pt);
                var screenPoint = new Windows.Foundation.Point(pt.X, pt.Y);
                var hwnd = WindowHandle;
                var currentPath = CurrentTab.CurrentPath;
                var capturedPaths = targetPaths;

                if (capturedPaths.Count > 0)
                {
                    ShellContextMenuService.ShowContextMenuAsync(hwnd, capturedPaths, screenPoint, isShift: true);
                }
                else if (!string.IsNullOrEmpty(currentPath) && !currentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) && !currentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    ShellContextMenuService.ShowFolderBackgroundContextMenuAsync(hwnd, currentPath, screenPoint, isShift: true);
                }
                return;
            }

            if (ActiveListControl.SelectedItems.Count > 0)
            {
                args.Handled = true;
                if (hasPoint)
                {
                    ItemContextMenu.ShowAt(targetElement, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
                }
                else
                {
                    ItemContextMenu.ShowAt(targetElement);
                }
            }
            else
            {
                args.Handled = true;
                if (hasPoint)
                {
                    BackgroundContextMenu.ShowAt(targetElement, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
                }
                else
                {
                    BackgroundContextMenu.ShowAt(targetElement);
                }
            }
        }

        #endregion
    }
}
