using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region File List Drag & Drop (Outgoing & Incoming)

        private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            var items = e.Items.OfType<FileItem>().ToList();
            if (items.Count == 0) return;

            // 選択中の複数アイテムがある場合は、選択全体をドラッグ対象にする
            var selected = ActiveListControl?.SelectedItems?.OfType<FileItem>()?.ToList() ?? [];
            if (selected.Count > 1 && items.Any(i => selected.Contains(i)))
            {
                items = selected;
            }

            var validPaths = items.Select(i => i.FullPath)
                                 .Where(p => File.Exists(p) || Directory.Exists(p))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            if (validPaths.Count == 0) return;

            // コピーと移動の両方を許可
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

            // 1. StorageItems の設定 (Windows Explorer / 外部アプリ / WinUI 連携用)
            try
            {
                var storageItems = Task.Run(async () =>
                {
                    var list = new List<IStorageItem>();
                    foreach (var path in validPaths)
                    {
                        try
                        {
                            if (File.Exists(path))
                            {
                                var file = await StorageFile.GetFileFromPathAsync(path);
                                list.Add(file);
                            }
                            else if (Directory.Exists(path))
                            {
                                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                                list.Add(folder);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DragItemsStarting] StorageItem resolve error for {path}: {ex.Message}");
                        }
                    }
                    return list;
                }).Result;

                if (storageItems.Count > 0)
                {
                    e.Data.SetStorageItems(storageItems);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DragItemsStarting] SetStorageItems error: {ex.Message}");
            }

            // 2. テキスト形式 (フルパス) の設定 (テキストエディタ・ターミナル等へのドロップ互換)
            e.Data.SetText(string.Join(Environment.NewLine, validPaths));
        }

        private void FileList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (args.DropResult == DataPackageOperation.Move)
            {
                CurrentTab?.Refresh();
            }
        }

        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            if (!IsDataPackageSupported(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool isCtrl = e.Modifiers.HasFlag(DragDropModifiers.Control);
            bool isShift = e.Modifiers.HasFlag(DragDropModifiers.Shift);

            // ドロップ先フォルダーの判定 (カーソル下のフォルダーアイテム、またはカレントフォルダ背景)
            var (targetDir, targetName) = GetFileListDropTarget(e);

            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            DataPackageOperation op;
            if (isCtrl)
            {
                op = DataPackageOperation.Copy;
            }
            else if (isShift)
            {
                op = DataPackageOperation.Move;
            }
            else
            {
                // Windows Explorer 互換: デフォルトで移動（同一/異種ドライブでの標準的挙動）
                op = (e.AllowedOperations.HasFlag(DataPackageOperation.Move))
                    ? DataPackageOperation.Move
                    : DataPackageOperation.Copy;
            }

            e.AcceptedOperation = op;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsContentVisible = true;

            string opName = (op == DataPackageOperation.Move) ? "移動" : "コピー";
            e.DragUIOverride.Caption = $"{targetName} に{opName}";
        }

        private async void FileList_Drop(object sender, DragEventArgs e)
        {
            var def = e.GetDeferral();
            try
            {
                var (targetDir, _) = GetFileListDropTarget(e);
                if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
                {
                    if (CurrentTab != null && Directory.Exists(CurrentTab.CurrentPath))
                    {
                        targetDir = CurrentTab.CurrentPath;
                    }
                    else
                    {
                        return;
                    }
                }

                var paths = await ExtractPathsFromDataPackageAsync(e.DataView);
                if (paths.Count == 0) return;

                bool isMove = (e.AcceptedOperation == DataPackageOperation.Move) ||
                              e.Modifiers.HasFlag(DragDropModifiers.Shift);

                bool success = await PerformFileTransferWithDialogAsync(paths, targetDir, isMove);
                if (success)
                {
                    CurrentTab?.Refresh();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileList_Drop] Error: {ex.Message}");
            }
            finally
            {
                def.Complete();
            }
        }

        private void FileList_DragLeave(object sender, DragEventArgs e)
        {
            // ドラッグ離脱時のクリーンアップ
        }

        private (string? Path, string? DisplayName) GetFileListDropTarget(DragEventArgs e)
        {
            var listControl = ActiveListControl;
            if (listControl != null)
            {
                var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), listControl);
                foreach (var element in hitElements)
                {
                    if (element is FrameworkElement fe)
                    {
                        var container = fe.FindParent<SelectorItem>();
                        if (container?.Content is FileItem item && item.IsDirectory && !string.IsNullOrEmpty(item.FullPath) && Directory.Exists(item.FullPath))
                        {
                            return (item.FullPath, item.Name);
                        }
                    }
                }
            }

            if (CurrentTab != null && Directory.Exists(CurrentTab.CurrentPath))
            {
                return (CurrentTab.CurrentPath, CurrentTab.Header);
            }

            return (null, null);
        }

        #endregion

        #region Sidebar Drag & Drop (Incoming)

        private void SidebarList_DragOver(object sender, DragEventArgs e)
        {
            if (!IsDataPackageSupported(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            var (targetPath, displayName) = GetSidebarDropTarget(e);
            if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool isCtrl = e.Modifiers.HasFlag(DragDropModifiers.Control);
            bool isShift = e.Modifiers.HasFlag(DragDropModifiers.Shift);

            DataPackageOperation op = isCtrl
                ? DataPackageOperation.Copy
                : (isShift || e.AllowedOperations.HasFlag(DataPackageOperation.Move) ? DataPackageOperation.Move : DataPackageOperation.Copy);

            e.AcceptedOperation = op;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;

            string opName = (op == DataPackageOperation.Move) ? "移動" : "コピー";
            e.DragUIOverride.Caption = $"{displayName} に{opName}";
        }

        private async void SidebarList_Drop(object sender, DragEventArgs e)
        {
            var def = e.GetDeferral();
            try
            {
                var (targetPath, _) = GetSidebarDropTarget(e);
                if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath)) return;

                var paths = await ExtractPathsFromDataPackageAsync(e.DataView);
                if (paths.Count == 0) return;

                bool isMove = (e.AcceptedOperation == DataPackageOperation.Move) ||
                              e.Modifiers.HasFlag(DragDropModifiers.Shift);

                bool success = await PerformFileTransferWithDialogAsync(paths, targetPath, isMove);
                if (success && CurrentTab?.CurrentPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    CurrentTab.Refresh();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SidebarList_Drop] Error: {ex.Message}");
            }
            finally
            {
                def.Complete();
            }
        }

        private void SidebarList_DragLeave(object sender, DragEventArgs e)
        {
        }

        private (string? Path, string? DisplayName) GetSidebarDropTarget(DragEventArgs e)
        {
            if (SidebarList == null) return (null, null);

            var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), SidebarList);
            foreach (var element in hitElements)
            {
                if (element is FrameworkElement fe)
                {
                    var container = fe.FindParent<ListViewItem>();
                    if (container?.Content is FileItem item && item.IsDirectory && !string.IsNullOrEmpty(item.FullPath) && Directory.Exists(item.FullPath))
                    {
                        return (item.FullPath, item.Name);
                    }
                }
            }

            return (null, null);
        }

        #endregion

        #region Common DnD Helpers

        private static bool IsDataPackageSupported(DataPackageView dataView)
        {
            if (dataView == null) return false;
            return dataView.Contains(StandardDataFormats.StorageItems) ||
                   dataView.Contains(StandardDataFormats.Text);
        }

        private static async Task<List<string>> ExtractPathsFromDataPackageAsync(DataPackageView dataView)
        {
            var paths = new List<string>();

            if (dataView.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await dataView.GetStorageItemsAsync();
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrEmpty(item.Path) && (File.Exists(item.Path) || Directory.Exists(item.Path)))
                        {
                            paths.Add(item.Path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExtractPaths] GetStorageItems error: {ex.Message}");
                }
            }

            if (paths.Count == 0 && dataView.Contains(StandardDataFormats.Text))
            {
                try
                {
                    string text = await dataView.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var lines = text.Split([Environment.NewLine, "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            string trimmed = line.Trim().Trim('"');
                            if (File.Exists(trimmed) || Directory.Exists(trimmed))
                            {
                                paths.Add(trimmed);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExtractPaths] GetText error: {ex.Message}");
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        #endregion
    }
}
