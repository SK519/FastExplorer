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
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region File Transfer Dialog Integration

        public async Task<bool> PerformFileTransferWithDialogAsync(IEnumerable<string> sourcePaths, string destinationDirectory, bool isMove)
        {
            var pathList = sourcePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0) return false;

            var controller = new FileTransferController();
            TransferDialogControl.Controller = controller;
            TransferDialogControl.ResetState(isMove ? "アイテムを移動中" : "アイテムをコピー中");

            FileTransferOverlayGrid.Visibility = Visibility.Visible;

            var progress = new Progress<FileTransferProgress>(p =>
            {
                TransferDialogControl.UpdateProgress(p);
            });

            bool success = false;
            try
            {
                success = await FileOperationService.ExecuteCopyOrMoveAsync(pathList, destinationDirectory, isMove, progress, controller);
            }
            finally
            {
                FileTransferOverlayGrid.Visibility = Visibility.Collapsed;
            }

            return success;
        }

        public async Task<bool> PerformPasteWithDialogAsync(string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory)) return false;

            var controller = new FileTransferController();
            TransferDialogControl.Controller = controller;
            TransferDialogControl.ResetState("アイテムを貼り付け中");

            FileTransferOverlayGrid.Visibility = Visibility.Visible;

            var progress = new Progress<FileTransferProgress>(p =>
            {
                TransferDialogControl.UpdateProgress(p);
            });

            bool success = false;
            try
            {
                success = await FileOperationService.PasteFilesAsync(destinationDirectory, progress, controller);
            }
            finally
            {
                FileTransferOverlayGrid.Visibility = Visibility.Collapsed;
            }

            return success;
        }

        #endregion

        #region Breadcrumbs Drag & Drop (Incoming)

        private void Breadcrumb_DragOver(object sender, DragEventArgs e)
        {
            if (!IsDataPackageSupported(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            var (targetPath, displayName) = GetBreadcrumbDropTarget(e);
            if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool isCtrl = e.Modifiers.HasFlag(DragDropModifiers.Control);
            DataPackageOperation op = isCtrl ? DataPackageOperation.Copy : DataPackageOperation.Move;

            e.AcceptedOperation = op;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.Caption = $"{displayName} に{(op == DataPackageOperation.Move ? "移動" : "コピー")}";
        }

        private async void Breadcrumb_Drop(object sender, DragEventArgs e)
        {
            var def = e.GetDeferral();
            try
            {
                var (targetPath, _) = GetBreadcrumbDropTarget(e);
                if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath)) return;

                var paths = await ExtractPathsFromDataPackageAsync(e.DataView);
                if (paths.Count == 0) return;

                bool isMove = (e.AcceptedOperation == DataPackageOperation.Move) ||
                              e.Modifiers.HasFlag(DragDropModifiers.Shift);

                bool success = await PerformFileTransferWithDialogAsync(paths, targetPath, isMove);
                if (success)
                {
                    CurrentTab?.Refresh();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Breadcrumb_Drop] Error: {ex.Message}");
            }
            finally
            {
                def.Complete();
            }
        }

        private (string? Path, string? DisplayName) GetBreadcrumbDropTarget(DragEventArgs e)
        {
            if (AddressBar == null) return (null, null);

            var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), AddressBar);
            foreach (var element in hitElements)
            {
                if (element is FrameworkElement fe)
                {
                    if (fe.DataContext is BreadcrumbItem bItem && !string.IsNullOrEmpty(bItem.FullPath) && Directory.Exists(bItem.FullPath))
                    {
                        return (bItem.FullPath, bItem.Label);
                    }
                }
            }

            return (null, null);
        }

        #endregion

        #region Home Quick Access Drag & Drop

        private void HomeQuickAccess_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            var list = new List<string>();
            foreach (var obj in e.Items)
            {
                if (obj is QuickAccessFolderItem qa && Directory.Exists(qa.Path))
                {
                    list.Add(qa.Path);
                }
                else if (obj is FileItem fi && (File.Exists(fi.FullPath) || Directory.Exists(fi.FullPath)))
                {
                    list.Add(fi.FullPath);
                }
            }

            if (list.Count == 0) return;

            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var def = request.GetDeferral();
                try
                {
                    var sList = new List<IStorageItem>();
                    foreach (var p in list)
                    {
                        try
                        {
                            if (File.Exists(p)) sList.Add(await StorageFile.GetFileFromPathAsync(p));
                            else if (Directory.Exists(p)) sList.Add(await StorageFolder.GetFolderFromPathAsync(p));
                        }
                        catch { }
                    }
                    request.SetData(sList);
                }
                catch { }
                finally
                {
                    def.Complete();
                }
            });

            e.Data.SetText(string.Join(Environment.NewLine, list));
        }

        private void HomeQuickAccess_DragOver(object sender, DragEventArgs e)
        {
            if (!IsDataPackageSupported(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            var (targetPath, displayName) = GetHomeQuickAccessDropTarget(e);
            if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool isCtrl = e.Modifiers.HasFlag(DragDropModifiers.Control);
            DataPackageOperation op = isCtrl ? DataPackageOperation.Copy : DataPackageOperation.Move;

            e.AcceptedOperation = op;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.Caption = $"{displayName} に{(op == DataPackageOperation.Move ? "移動" : "コピー")}";
        }

        private async void HomeQuickAccess_Drop(object sender, DragEventArgs e)
        {
            var def = e.GetDeferral();
            try
            {
                var (targetPath, _) = GetHomeQuickAccessDropTarget(e);
                if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath)) return;

                var paths = await ExtractPathsFromDataPackageAsync(e.DataView);
                if (paths.Count == 0) return;

                bool isMove = (e.AcceptedOperation == DataPackageOperation.Move) ||
                              e.Modifiers.HasFlag(DragDropModifiers.Shift);

                await PerformFileTransferWithDialogAsync(paths, targetPath, isMove);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeQuickAccess_Drop] Error: {ex.Message}");
            }
            finally
            {
                def.Complete();
            }
        }

        private (string? Path, string? DisplayName) GetHomeQuickAccessDropTarget(DragEventArgs e)
        {
            if (HomeView == null) return (null, null);

            var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), HomeView);
            foreach (var element in hitElements)
            {
                if (element is FrameworkElement fe)
                {
                    var container = fe.FindParent<GridViewItem>();
                    if (container?.Content is QuickAccessFolderItem qa && Directory.Exists(qa.Path))
                    {
                        return (qa.Path, qa.Name);
                    }
                    if (container?.Content is FileItem fi && Directory.Exists(fi.FullPath))
                    {
                        return (fi.FullPath, fi.Name);
                    }
                }
            }

            return (null, null);
        }

        #endregion
    }
}
