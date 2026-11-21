using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace FastExplorer.Views.MainWindow.Preview
{
    public sealed partial class PreviewPaneControl : UserControl
    {
        private CancellationTokenSource? _previewCts;
        private string? _currentPreviewPath;

        public event RoutedEventHandler? CloseRequested;

        public PreviewPaneControl()
        {
            this.InitializeComponent();
        }

        private static Task SetCopiedBitmapSafeAsync(Image target, SoftwareBitmap sourceBitmap)
        {
            try
            {
                var wb = IconThumbnailService.CreateWriteableBitmapFromSoftwareBitmap(sourceBitmap);
                if (wb != null)
                {
                    target.Source = wb;
                }
            }
            catch { }
            return Task.CompletedTask;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, e);
        }

        public void UpdatePreview(IReadOnlyList<FileItem>? selectedItems, string? currentFolderPath)
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
            var ct = _previewCts.Token;

            var items = selectedItems ?? [];

            if (items.Count == 1)
            {
                ShowSingleItemPreview(items[0], ct);
            }
            else if (items.Count > 1)
            {
                ShowMultiItemPreview(items);
            }
            else
            {
                ShowCurrentFolderPreview(currentFolderPath, ct);
            }
        }

        private void ShowSingleItemPreview(FileItem item, CancellationToken ct)
        {
            _currentPreviewPath = item.FullPath;

            PreviewItemNameText.Text = item.Name;
            PreviewItemTypeText.Text = item.FileType;
            PreviewItemSizeText.Text = item.IsDirectory ? "-" : (string.IsNullOrEmpty(item.FormattedSize) ? "0 バイト" : item.FormattedSize);
            PreviewItemModifiedText.Text = item.FormattedDateModified;
            PreviewItemPathText.Text = item.FullPath;

            ResetPreviewControls();

            if (item.IsDirectory)
            {
                PreviewLargeIconGrid.Visibility = Visibility.Visible;
                PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                if (item.Icon != null)
                {
                    PreviewLargeImageIcon.Source = item.Icon;
                    PreviewLargeImageIcon.Visibility = Visibility.Visible;
                }
                else if (IconThumbnailService.DefaultFolderBitmap != null)
                {
                    PreviewLargeImageIcon.Visibility = Visibility.Visible;
                    _ = SetCopiedBitmapSafeAsync(PreviewLargeImageIcon, IconThumbnailService.DefaultFolderBitmap);
                }
                LoadFolderDetailsAndSizeAsync(item.FullPath, ct);
            }
            else
            {
                string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
                if (IsImageFile(ext))
                {
                    LoadImagePreviewAsync(item.FullPath, ct);
                }
                else if (IsVideoFile(ext))
                {
                    LoadVideoPreviewAsync(item.FullPath, ct);
                }
                else if (IsTextFile(ext))
                {
                    LoadTextPreviewAsync(item.FullPath, ct);
                }
                else if (ArchiveService.IsSupportedArchive(item.FullPath))
                {
                    LoadArchivePreviewAsync(item.FullPath, ct);
                }
                else
                {
                    LoadGenericFilePreviewAsync(item, ct);
                }
                LoadFileAdditionalInfoAsync(item.FullPath, ct);
            }
        }

        private void ShowMultiItemPreview(IReadOnlyList<FileItem> items)
        {
            _currentPreviewPath = null;
            ResetPreviewControls();

            PreviewLargeIconGrid.Visibility = Visibility.Visible;
            var defaultFolder = IconThumbnailService.DefaultFolderBitmap;
            if (defaultFolder != null)
            {
                PreviewLargeImageIcon.Visibility = Visibility.Visible;
                PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                _ = SetCopiedBitmapSafeAsync(PreviewLargeImageIcon, defaultFolder);
            }
            else
            {
                PreviewLargeFontIcon.Visibility = Visibility.Visible;
                PreviewLargeFontIcon.Glyph = "\uE8B7";
                PreviewLargeImageIcon.Visibility = Visibility.Collapsed;
            }

            int count = items.Count;
            long totalBytes = 0;
            int fileCount = 0;
            int folderCount = 0;

            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                if (item.IsDirectory)
                {
                    folderCount++;
                }
                else
                {
                    fileCount++;
                    totalBytes += item.SizeInBytes;
                }
            }

            PreviewItemNameText.Text = $"{count} 個の項目を選択";
            PreviewItemTypeText.Text = $"フォルダー: {folderCount}、ファイル: {fileCount}";
            PreviewItemSizeText.Text = FileItem.FormatFileSize(totalBytes);
            PreviewItemModifiedText.Text = "-";
            PreviewItemPathText.Text = Path.GetDirectoryName(items[0].FullPath) ?? "-";
        }

        private void ShowCurrentFolderPreview(string? folderPath, CancellationToken ct)
        {
            _currentPreviewPath = folderPath;
            ResetPreviewControls();

            if (string.IsNullOrEmpty(folderPath))
            {
                PreviewItemNameText.Text = "アイテム未選択";
                PreviewItemTypeText.Text = "-";
                PreviewItemSizeText.Text = "-";
                PreviewItemModifiedText.Text = "-";
                PreviewItemPathText.Text = "-";
                return;
            }

            // WSL パスは 9P 越しの同期 Directory.Exists / 再帰走査を避けて即時表示
            if (folderPath.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = folderPath.TrimEnd('\\');
                string[] parts = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                PreviewItemNameText.Text = parts.Length > 0 ? parts[^1] : "Linux (WSL)";
                PreviewItemTypeText.Text = parts.Length <= 2 ? "WSL ディストリビューション" : "フォルダー";
                PreviewItemSizeText.Text = "-";
                PreviewItemModifiedText.Text = "-";
                PreviewItemPathText.Text = folderPath;

                var wslBmp = parts.Length <= 2
                    ? (IconThumbnailService.DefaultWslBitmap ?? IconThumbnailService.DefaultFolderBitmap)
                    : IconThumbnailService.DefaultFolderBitmap;
                if (wslBmp != null)
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeImageIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                    _ = SetCopiedBitmapSafeAsync(PreviewLargeImageIcon, wslBmp);
                }
                return;
            }

            if (!Directory.Exists(folderPath))
            {
                PreviewItemNameText.Text = "アイテム未選択";
                PreviewItemTypeText.Text = "-";
                PreviewItemSizeText.Text = "-";
                PreviewItemModifiedText.Text = "-";
                PreviewItemPathText.Text = "-";
                return;
            }

            try
            {
                var dirInfo = new DirectoryInfo(folderPath);
                PreviewItemNameText.Text = dirInfo.Name;
                PreviewItemTypeText.Text = "フォルダー";
                PreviewItemSizeText.Text = "-";
                PreviewItemModifiedText.Text = dirInfo.LastWriteTime.ToString("yyyy/MM/dd HH:mm");
                PreviewItemPathText.Text = dirInfo.FullName;

                PreviewLargeIconGrid.Visibility = Visibility.Visible;
                var bmp = IconThumbnailService.GetSoftwareBitmapForPath(folderPath, true, true);
                if (bmp != null)
                {
                    PreviewLargeImageIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                    _ = SetCopiedBitmapSafeAsync(PreviewLargeImageIcon, bmp);
                }
                else
                {
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE838";
                    PreviewLargeImageIcon.Visibility = Visibility.Collapsed;
                }

                LoadFolderDetailsAndSizeAsync(folderPath, ct);
            }
            catch
            {
                PreviewItemNameText.Text = folderPath;
                PreviewItemTypeText.Text = "フォルダー";
                PreviewItemSizeText.Text = "-";
                PreviewItemModifiedText.Text = "-";
                PreviewItemPathText.Text = folderPath;
            }
        }

        private void ResetPreviewControls()
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            PreviewTextScrollViewer.Visibility = Visibility.Collapsed;
            PreviewTextBlock.Text = string.Empty;
            PreviewLargeIconGrid.Visibility = Visibility.Collapsed;
            PreviewLargeImageIcon.Source = null;

            PreviewCreatedRow.Visibility = Visibility.Collapsed;
            PreviewResolutionRow.Visibility = Visibility.Collapsed;
            PreviewAttributesRow.Visibility = Visibility.Collapsed;
        }

        private void PreviewCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentPreviewPath))
            {
                var dp = new DataPackage();
                dp.SetText(_currentPreviewPath);
                Clipboard.SetContent(dp);
            }
        }
    }
}
