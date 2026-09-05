using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using FastExplorer.Views.Properties;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu File Operations & Properties

        private void ContextMenuRename_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item == null) return;
            BeginRename(item);
        }

        public void BeginRename(FileItem item)
        {
            item.IsRenaming = true;
            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var container = ActiveListControl.ContainerFromItem(item) as FrameworkElement;
                if (container != null)
                {
                    var tb = container.FindDescendant<TextBox>();
                    if (tb != null)
                    {
                        AttachRenameBoxEvents(tb);
                        tb.Focus(FocusState.Programmatic);
                        string text = tb.Text;
                        int dot = text.LastIndexOf('.');
                        if (dot > 0 && !item.IsDirectory)
                        {
                            tb.Select(0, dot);
                        }
                        else
                        {
                            tb.SelectAll();
                        }
                    }
                }
            });
        }

        private async void ContextMenuDelete_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedPaths();
            ItemContextMenu.Hide();
            if (paths.Count == 0) return;

            bool isRecycleBin = RecycleBinService.IsRecycleBinPath(CurrentTab?.CurrentPath);

            if (isRecycleBin)
            {
                var dialog = new ContentDialog
                {
                    Title = "完全に削除の確認",
                    Content = $"{paths.Count} 個の項目をごみ箱から完全に削除しますか？\n（元に戻すことはできません）",
                    PrimaryButtonText = "完全に削除",
                    CloseButtonText = "キャンセル",
                    XamlRoot = this.Content.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RecycleBinService.DeletePermanently(paths);
                    CurrentTab?.Refresh();
                }
                return;
            }

            if (ConfigService.Current.Ui.ConfirmDelete)
            {
                var dialog = new ContentDialog
                {
                    Title = "削除の確認",
                    Content = $"{paths.Count} 個の項目をゴミ箱に移動しますか？",
                    PrimaryButtonText = "削除",
                    CloseButtonText = "キャンセル",
                    XamlRoot = this.Content.XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            FileOperationService.MoveToRecycleBin(paths);
            CurrentTab?.Refresh();
        }

        private void ContextMenuRestore_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedPaths();
            ItemContextMenu.Hide();
            if (paths.Count == 0) return;

            RecycleBinService.RestoreItems(paths);
            CurrentTab?.Refresh();
        }

        private void ContextMenuEmptyRecycleBin_Click(object sender, RoutedEventArgs e)
        {
            ItemContextMenu.Hide();
            BackgroundContextMenu.Hide();
            RecycleBinService.EmptyRecycleBin(WindowHandle, showConfirmation: true);
            if (RecycleBinService.IsRecycleBinPath(CurrentTab?.CurrentPath))
            {
                CurrentTab?.Refresh();
            }
        }

        private async void DeletePermanentlyAction()
        {
            var paths = GetSelectedPaths();
            if (paths.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "完全に削除の確認",
                Content = $"{paths.Count} 個の項目を完全に削除しますか？\n（ゴミ箱には移動されません）",
                PrimaryButtonText = "完全に削除",
                CloseButtonText = "キャンセル",
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (RecycleBinService.IsRecycleBinPath(CurrentTab?.CurrentPath))
                {
                    RecycleBinService.DeletePermanently(paths);
                }
                else
                {
                    FileOperationService.DeletePermanently(paths);
                }
                CurrentTab?.Refresh();
            }
        }

        private void ContextMenuProperties_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedPaths();
            if (paths.Count == 0)
            {
                var item = GetContextTargetItem();
                if (item != null) paths.Add(item.FullPath);
            }
            ItemContextMenu.Hide();
            if (paths.Count == 0) return;

            ShowPropertiesWindow(paths);
        }

        private void ContextMenuCurrentFolderProperties_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null || CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;
            ShowPropertiesWindow([CurrentTab.CurrentPath]);
        }

        private void ShowPropertiesWindow(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;

            try
            {
                Win32Interop.GetCursorPos(out var pt);
                var screenPoint = new Windows.Foundation.Point(pt.X, pt.Y);
                var theme = (this.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;

                PropertiesWindow.Show(paths, screenPoint, theme);
            }
            catch
            {
                // ignored
            }
        }

        private void ContextMenuOsStandard_Click(object sender, RoutedEventArgs e)
        {
            ItemContextMenu.Hide();
            Win32Interop.GetCursorPos(out var pt);
            var screenPoint = new Windows.Foundation.Point(pt.X, pt.Y);
            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                ShowOsStandardContextMenu(screenPoint, isShift: false);
            });
        }

        private void ShowOsStandardContextMenu(Windows.Foundation.Point? screenPos = null, bool isShift = false)
        {
            if (CurrentTab == null) return;

            var selectedItems = ActiveListControl.SelectedItems.OfType<FileItem>().ToList();
            var hwnd = WindowHandle;

            System.Diagnostics.Trace.WriteLine($"[ShowOsStandard] hwnd={hwnd}, selectedCount={selectedItems.Count}, screenPos={screenPos}, isShift={isShift}");

            if (selectedItems.Count > 0)
            {
                var paths = selectedItems.Select(x => x.FullPath).ToList();
                System.Diagnostics.Trace.WriteLine($"[ShowOsStandard] Calling ShowContextMenuAsync with {paths.Count} paths, first={paths[0]}");
                ShellContextMenuService.ShowContextMenuAsync(hwnd, paths, screenPos, isShift);
            }
            else if (!string.IsNullOrEmpty(CurrentTab.CurrentPath) && !CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.WriteLine($"[ShowOsStandard] Calling ShowFolderBackgroundContextMenuAsync for {CurrentTab.CurrentPath}");
                ShellContextMenuService.ShowFolderBackgroundContextMenuAsync(hwnd, CurrentTab.CurrentPath, screenPos, isShift);
            }
            else
            {
                System.Diagnostics.Trace.WriteLine("[ShowOsStandard] No items selected and no valid path");
            }
        }

        private void ContextMenuPinQuickAccess_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item != null && !string.IsNullOrEmpty(item.FullPath))
            {
                QuickAccessService.PinFolder(item.FullPath);
                RefreshSidebar();
                RefreshHomeView();
            }
        }

        private void ContextMenuUnpinQuickAccess_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item != null && !string.IsNullOrEmpty(item.FullPath))
            {
                QuickAccessService.UnpinFolder(item.FullPath);
                RefreshSidebar();
                RefreshHomeView();
            }
        }

        private void ContextMenuOpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item != null && !string.IsNullOrEmpty(item.FullPath))
            {
                string path = item.FullPath;
                string? folder = item.IsDirectory ? Path.GetDirectoryName(path.TrimEnd('\\', '/')) : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    CurrentTab?.NavigateTo(folder);
                }
            }
        }

        private void ContextMenuRemoveFromRecent_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item != null)
            {
                string targetPath = !string.IsNullOrEmpty(item.ShortcutPath) ? item.ShortcutPath : item.FullPath;
                Core.Win32Interop.DeleteRecentShortcut(targetPath);
                if (!string.IsNullOrEmpty(item.ShortcutPath) && item.ShortcutPath != item.FullPath)
                {
                    Core.Win32Interop.DeleteRecentShortcut(item.FullPath);
                }
                QuickAccessService.NotifyRecentChanged();
            }
        }

        #endregion
    }
}
