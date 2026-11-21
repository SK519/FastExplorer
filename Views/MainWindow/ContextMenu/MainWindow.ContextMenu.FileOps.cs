using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            _renameStartedTimestamp = Stopwatch.GetTimestamp();
            _isRenameImeComposing = false;
            item.RenameText = item.Name;
            item.IsRenaming = true;

            void TryFocus()
            {
                if (!item.IsRenaming) return;
                var list = ActiveListControl;
                if (list == null) return;
                try
                {
                    list.ScrollIntoView(item);
                    list.UpdateLayout();
                }
                catch { }

                var container = list.ContainerFromItem(item) as FrameworkElement;
                if (container != null)
                {
                    var tb = container.FindDescendant<TextBox>();
                    if (tb != null)
                    {
                        AttachRenameBoxEvents(tb);
                        FocusAndSelectRenameBox(tb, item);
                    }
                }
            }

            // 即時実行 + メニュー/ツールバーのFlyoutクローズ完了を待って確実にフォーカスを名前欄へ
            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, TryFocus);
            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, TryFocus);
                });
            });
        }

        private async void ContextMenuDelete_Click(object sender, RoutedEventArgs e)
        {
            try
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

                await ExecuteDeleteWithRetryAsync(paths, permanent: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ContextMenu] Delete error: {ex.Message}");
            }
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
            try
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
                        CurrentTab?.Refresh();
                    }
                    else
                    {
                        await ExecuteDeleteWithRetryAsync(paths, permanent: true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ContextMenu] DeletePermanently error: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task ExecuteDeleteWithRetryAsync(List<string> paths, bool permanent)
        {
            var pendingPaths = paths;
            while (pendingPaths.Count > 0)
            {
                var result = permanent
                    ? await FileOperationService.DeletePermanentlyAsync(pendingPaths)
                    : await FileOperationService.MoveToRecycleBinAsync(pendingPaths);

                CurrentTab?.Refresh();

                if (result.Failures.Count == 0 || result.Cancelled)
                {
                    break;
                }

                bool retry = await ShowDeleteFailureDialogAsync(result.Failures);
                if (!retry)
                {
                    break;
                }

                pendingPaths = result.Failures.Select(f => f.Path).ToList();
            }
        }

        private async System.Threading.Tasks.Task<bool> ShowDeleteFailureDialogAsync(List<FileOperationService.DeleteFailureInfo> failures)
        {
            bool anyInUse = failures.Any(f => f.IsInUse);
            string title = anyInUse ? "ファイルは使用中のため削除できません" : "削除できない項目があります";

            var sb = new System.Text.StringBuilder();
            if (anyInUse)
            {
                sb.AppendLine("別のプログラムで開かれているため、以下の項目を削除できませんでした。該当するプログラムを閉じてから「再試行」してください。\n");
            }
            else
            {
                sb.AppendLine("以下の項目を削除できませんでした。\n");
            }

            int shown = 0;
            foreach (var f in failures)
            {
                if (shown >= 6)
                {
                    sb.AppendLine($"…他 {failures.Count - shown} 件");
                    break;
                }

                sb.Append($"• {f.FileName}");
                if (f.LockingProcesses.Count > 0)
                {
                    sb.Append($"（使用中: {string.Join(", ", f.LockingProcesses)}）");
                }
                else if (f.IsInUse)
                {
                    sb.Append("（別のプロセスが使用中）");
                }
                else if (!string.IsNullOrWhiteSpace(f.Reason))
                {
                    sb.Append($"（{f.Reason}）");
                }
                sb.AppendLine();
                shown++;
            }

            var dlg = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = sb.ToString().TrimEnd(),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 440
                },
                PrimaryButtonText = "再試行",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            return await dlg.ShowAsync() == ContentDialogResult.Primary;
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
                string targetPath = item.FullPath;
                if (!string.IsNullOrEmpty(item.ShortcutPath))
                {
                    targetPath = item.ShortcutPath;
                }
                else if (item.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string? resolved = Core.Win32Interop.ResolveShortcutTarget(item.FullPath);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        targetPath = resolved;
                    }
                }

                string? folder = Directory.Exists(targetPath)
                    ? targetPath
                    : Path.GetDirectoryName(targetPath.TrimEnd('\\', '/'));

                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && CurrentTab != null)
                {
                    if (!Directory.Exists(targetPath))
                    {
                        CurrentTab.PendingSelectedItemName = Path.GetFileName(targetPath.TrimEnd('\\', '/'));
                    }
                    CurrentTab.NavigateTo(folder);
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
