using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using FastExplorer.Views.Properties;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu Actions & Event Handlers

        public enum ArchiveFormat
        {
            Zip,
            SevenZip
        }

        private void CreateNewFileFromTemplate(ShellNewTemplate template)
        {
            if (CurrentTab == null || CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;

            string? createdPath = ShellNewService.CreateFileFromTemplate(CurrentTab.CurrentPath, template);
            if (createdPath != null)
            {
                CurrentTab.Refresh();
                SelectAndRenameCreatedPath(createdPath);
            }
        }

        private void SelectAndRenameCreatedPath(string createdPath)
        {
            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var item = CurrentTab?.Items.FirstOrDefault(i => i.FullPath.Equals(createdPath, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    ActiveListControl.SelectedItem = item;
                    BeginRename(item);
                }
            });
        }

        private static void LaunchDirectProcess(string exePath, IReadOnlyList<string> filePaths)
        {
            try
            {
                // ファイルパスをスペース区切りでクォートして渡す
                var quotedPaths = string.Join(" ", filePaths.Select(p => $"\"{p}\""));
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = quotedPaths,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchDirectProcess] Error: {ex.Message}");
            }
        }

        private List<FileItem> GetContextTargetItems()
        {
            if (ItemContextMenu.IsOpen && _contextTargetItemsOverride != null && _contextTargetItemsOverride.Count > 0)
            {
                return _contextTargetItemsOverride;
            }
            if (CurrentTab?.Items != null)
            {
                var selected = CurrentTab.Items.Where(i => i.IsSelected).ToList();
                if (selected.Count > 0) return selected;
            }
            return ActiveListControl?.SelectedItems?.OfType<FileItem>().ToList() ?? [];
        }

        private FileItem? GetContextTargetItem()
        {
            var items = GetContextTargetItems();
            return items.Count > 0 ? items[0] : null;
        }

        private List<string> GetSelectedPaths()
        {
            return GetContextTargetItems().Select(i => i.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }

        private void ContextMenuOpen_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            OpenSelectedItem();
        }

        private void ContextMenuOpenNewTab_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item != null && !string.IsNullOrEmpty(item.FullPath))
            {
                CreateNewTab(item.FullPath);
            }
        }

        private void ContextMenuOpenWith_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item != null && !item.IsDirectory)
            {
                FileOperationService.OpenWithDialog(item.FullPath);
            }
        }

        private void ContextMenuEdit_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            if (item == null || item.IsDirectory) return;

            var editor = ConfigService.Current.Editor;
            try
            {
                string args = string.Join(" ", editor.Args.Select(a => a.Replace("{filePath}", $"\"{item.FullPath}\"")));
                Process.Start(new ProcessStartInfo
                {
                    FileName = editor.Path,
                    Arguments = args,
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{item.FullPath}\"") { UseShellExecute = true });
            }
        }

        private void ContextMenuTerminal_Click(object sender, RoutedEventArgs e)
        {
            var targetItem = GetContextTargetItem();
            ItemContextMenu.Hide();
            string targetDir = CurrentTab?.CurrentPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (targetItem is { IsDirectory: true } dirItem)
            {
                targetDir = dirItem.FullPath;
            }

            if (targetDir.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                targetDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var terminal = ConfigService.Current.Terminal;
            try
            {
                string args = string.Join(" ", terminal.Args.Select(a => a.Replace("{dirPath}", $"\"{targetDir}\"")));
                Process.Start(new ProcessStartInfo
                {
                    FileName = terminal.Path,
                    Arguments = args,
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    WorkingDirectory = targetDir,
                    UseShellExecute = true
                });
            }
        }

        private void ContextMenuCut_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedPaths();
            ItemContextMenu.Hide();
            if (paths.Count > 0)
            {
                FileOperationService.CutFiles(paths);
            }
        }

        private void ContextMenuCopy_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedPaths();
            ItemContextMenu.Hide();
            if (paths.Count > 0)
            {
                FileOperationService.CopyFiles(paths);
            }
        }

        private void ContextMenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var paths = GetSelectedPaths();
            ItemContextMenu.Hide();
            if (paths.Count == 0) return;

            var package = new DataPackage();
            package.SetText(string.Join(Environment.NewLine, paths));
            Clipboard.SetContent(package);
        }

        private async void ContextMenuPaste_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null || CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;

            bool success = await PerformPasteWithDialogAsync(CurrentTab.CurrentPath);
            if (success)
            {
                CurrentTab.Refresh();
            }
        }

        private async Task PerformCompressAsync(ArchiveFormat format, ArchiveCompressionLevel level)
        {
            var paths = GetSelectedPaths();
            ItemContextMenu.Hide();
            CloseAllSubmenus();
            if (CurrentTab == null || CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;
            if (paths.Count == 0) return;

            string? resultPath = format switch
            {
                ArchiveFormat.Zip => await ArchiveService.CompressToZipAsync(paths, CurrentTab.CurrentPath, level),
                ArchiveFormat.SevenZip => await ArchiveService.CompressTo7zAsync(paths, CurrentTab.CurrentPath, level),
                _ => null
            };

            if (resultPath != null)
            {
                CurrentTab.Refresh();
            }
        }

        private async void ContextMenuCompressZip_Click(object sender, RoutedEventArgs e)
        {
            var shellConfig = ConfigService.Current.ShellMenu;
            await PerformCompressAsync(ArchiveFormat.Zip, shellConfig.DefaultZipLevel);
        }

        private async void ContextMenuCompress7z_Click(object sender, RoutedEventArgs e)
        {
            var shellConfig = ConfigService.Current.ShellMenu;
            await PerformCompressAsync(ArchiveFormat.SevenZip, shellConfig.DefaultSevenZipLevel);
        }

        private async void ContextMenuExtractHere_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            CloseAllSubmenus();
            if (CurrentTab == null || item == null || string.IsNullOrEmpty(item.FullPath)) return;

            bool success = await ArchiveService.ExtractAsync(item.FullPath, CurrentTab.CurrentPath, createSubFolder: false);
            if (success)
            {
                CurrentTab.Refresh();
            }
        }

        private async void ContextMenuExtractToFolder_Click(object sender, RoutedEventArgs e)
        {
            var item = GetContextTargetItem();
            ItemContextMenu.Hide();
            CloseAllSubmenus();
            if (CurrentTab == null || item == null || string.IsNullOrEmpty(item.FullPath)) return;

            bool success = await ArchiveService.ExtractAsync(item.FullPath, CurrentTab.CurrentPath, createSubFolder: true);
            if (success)
            {
                CurrentTab.Refresh();
            }
        }

        private async void ContextMenuExtractZip_Click(object sender, RoutedEventArgs e)
        {
            ContextMenuExtractToFolder_Click(sender, e);
        }

        private void ContextMenuNewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null || CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;

            string? folderPath = FileOperationService.CreateNewFolder(CurrentTab.CurrentPath);
            if (folderPath != null)
            {
                CurrentTab.Refresh();
                SelectAndRenameCreatedPath(folderPath);
            }
        }

        private void ContextMenuNewTextFile_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null || CurrentTab.CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;

            string? filePath = FileOperationService.CreateNewTextFile(CurrentTab.CurrentPath);
            if (filePath != null)
            {
                CurrentTab.Refresh();
                SelectAndRenameCreatedPath(filePath);
            }
        }

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
                        tb.PreviewKeyDown -= RenameBox_PreviewKeyDown;
                        tb.PreviewKeyDown += RenameBox_PreviewKeyDown;
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

        private void MenuToggleHidden_Click(object sender, RoutedEventArgs e)
        {
            ToggleShowHiddenFiles();
        }

        private void ContextMenuTogglePreview_Click(object sender, RoutedEventArgs e)
        {
            ItemContextMenu.Hide();
            IsPreviewPaneVisible = true;
            UpdatePreviewPane();
        }

        #endregion
    }
}
