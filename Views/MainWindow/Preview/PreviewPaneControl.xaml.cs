using System;
using System.Collections.Generic;
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, e);
        }

        public void UpdatePreview(IReadOnlyList<FileItem>? selectedItems, string? currentFolderPath)
        {
            _previewCts?.Cancel();
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
                ShowCurrentFolderPreview(currentFolderPath);
            }
        }

        private void ShowSingleItemPreview(FileItem item, CancellationToken ct)
        {
            _currentPreviewPath = item.FullPath;

            PreviewItemNameText.Text = item.Name;
            PreviewItemTypeText.Text = item.FileType;
            PreviewItemSizeText.Text = string.IsNullOrEmpty(item.FormattedSize) ? "-" : item.FormattedSize;
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
                    try
                    {
                        var src = new SoftwareBitmapSource();
                        _ = src.SetBitmapAsync(SoftwareBitmap.Copy(IconThumbnailService.DefaultFolderBitmap));
                        PreviewLargeImageIcon.Source = src;
                        PreviewLargeImageIcon.Visibility = Visibility.Visible;
                    }
                    catch { }
                }
                LoadFolderAdditionalInfoAsync(item.FullPath, ct);
            }
            else
            {
                string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
                if (IsImageFile(ext))
                {
                    LoadImagePreviewAsync(item.FullPath, ct);
                }
                else if (IsTextFile(ext))
                {
                    LoadTextPreviewAsync(item.FullPath, ct);
                }
                else
                {
                    LoadFileIconPreviewAsync(item, ct);
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
                try
                {
                    var copy = SoftwareBitmap.Copy(defaultFolder);
                    var source = new SoftwareBitmapSource();
                    _ = source.SetBitmapAsync(copy);
                    PreviewLargeImageIcon.Source = source;
                    PreviewLargeImageIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE8B7";
                    PreviewLargeImageIcon.Visibility = Visibility.Collapsed;
                }
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

        private void ShowCurrentFolderPreview(string? folderPath)
        {
            _currentPreviewPath = folderPath;
            ResetPreviewControls();

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
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
                    try
                    {
                        var copy = SoftwareBitmap.Copy(bmp);
                        var source = new SoftwareBitmapSource();
                        _ = source.SetBitmapAsync(copy);
                        PreviewLargeImageIcon.Source = source;
                        PreviewLargeImageIcon.Visibility = Visibility.Visible;
                        PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                    }
                    catch
                    {
                        PreviewLargeFontIcon.Visibility = Visibility.Visible;
                        PreviewLargeFontIcon.Glyph = "\uE838";
                        PreviewLargeImageIcon.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE838";
                    PreviewLargeImageIcon.Visibility = Visibility.Collapsed;
                }

                LoadFolderAdditionalInfoAsync(folderPath, CancellationToken.None);
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

        private async void LoadImagePreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                // 高速キー移動時の不要なI/Oをスキップするデバウンス (50ms)
                await Task.Delay(50, ct);
                if (ct.IsCancellationRequested) return;

                // ヘッダ情報から超高速に解像度を取得
                uint width = 0;
                uint height = 0;
                try
                {
                    using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var codec = SkiaSharp.SKCodec.Create(stream);
                    if (codec != null)
                    {
                        width = (uint)codec.Info.Width;
                        height = (uint)codec.Info.Height;
                        PreviewResolutionRow.Visibility = Visibility.Visible;
                        PreviewItemResolutionText.Text = $"{width} × {height} ピクセル";
                    }
                }
                catch { }

                if (ct.IsCancellationRequested) return;

                using var fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var randomAccessStream = fileStream.AsRandomAccessStream();

                if (ct.IsCancellationRequested) return;

                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 600
                };
                await bitmap.SetSourceAsync(randomAccessStream);

                if (ct.IsCancellationRequested) return;

                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;

                if (width == 0 || height == 0)
                {
                    try
                    {
                        randomAccessStream.Seek(0);
                        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                        width = decoder.PixelWidth;
                        height = decoder.PixelHeight;
                        PreviewResolutionRow.Visibility = Visibility.Visible;
                        PreviewItemResolutionText.Text = $"{width} × {height} ピクセル";
                    }
                    catch { }
                }
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uEB9F";
                }
            }
        }

        private async void LoadTextPreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                const int maxChars = 2000;
                char[] buffer = new char[maxChars];
                int readCount;

                using (var reader = new StreamReader(filePath))
                {
                    readCount = await reader.ReadBlockAsync(buffer, 0, maxChars);
                }

                if (ct.IsCancellationRequested) return;

                string previewText = new string(buffer, 0, readCount);
                if (readCount == maxChars)
                {
                    previewText += "\n\n... (プレビュー上限)";
                }

                PreviewTextBlock.Text = previewText;
                PreviewTextScrollViewer.Visibility = Visibility.Visible;
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE8C8";
                }
            }
        }

        private async void LoadFileIconPreviewAsync(FileItem item, CancellationToken ct)
        {
            PreviewLargeIconGrid.Visibility = Visibility.Visible;

            if (item.Icon != null)
            {
                PreviewLargeImageIcon.Source = item.Icon;
                PreviewLargeImageIcon.Visibility = Visibility.Visible;
                PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
            }

            await Task.Run(async () =>
            {
                var bmp = IconThumbnailService.GetSoftwareBitmapForPath(item.FullPath, item.IsDirectory, true);
                if (bmp != null)
                {
                    try
                    {
                        var copy = SoftwareBitmap.Copy(bmp);
                        var source = new SoftwareBitmapSource();
                        await source.SetBitmapAsync(copy);
                        if (!ct.IsCancellationRequested)
                        {
                            PreviewLargeImageIcon.Source = source;
                            PreviewLargeImageIcon.Visibility = Visibility.Visible;
                            PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch { }
                }
            }, ct);
        }

        private async void LoadFileAdditionalInfoAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var fi = await Task.Run(() => new FileInfo(filePath), ct);
                if (ct.IsCancellationRequested) return;

                PreviewCreatedRow.Visibility = Visibility.Visible;
                PreviewItemCreatedText.Text = fi.CreationTime.ToString("yyyy/MM/dd HH:mm");

                PreviewAttributesRow.Visibility = Visibility.Visible;
                var attrs = new List<string>();
                if ((fi.Attributes & FileAttributes.ReadOnly) != 0) attrs.Add("読み取り専用");
                if ((fi.Attributes & FileAttributes.Hidden) != 0) attrs.Add("隠しファイル");
                if ((fi.Attributes & FileAttributes.System) != 0) attrs.Add("システム");
                if ((fi.Attributes & FileAttributes.Archive) != 0) attrs.Add("アーカイブ");
                PreviewItemAttributesText.Text = attrs.Count > 0 ? string.Join(", ", attrs) : "通常";
            }
            catch { }
        }

        private async void LoadFolderAdditionalInfoAsync(string folderPath, CancellationToken ct)
        {
            try
            {
                var di = await Task.Run(() => new DirectoryInfo(folderPath), ct);
                if (ct.IsCancellationRequested) return;

                PreviewCreatedRow.Visibility = Visibility.Visible;
                PreviewItemCreatedText.Text = di.CreationTime.ToString("yyyy/MM/dd HH:mm");

                PreviewAttributesRow.Visibility = Visibility.Visible;
                var attrs = new List<string>();
                if ((di.Attributes & FileAttributes.ReadOnly) != 0) attrs.Add("読み取り専用");
                if ((di.Attributes & FileAttributes.Hidden) != 0) attrs.Add("隠しファイル");
                PreviewItemAttributesText.Text = attrs.Count > 0 ? string.Join(", ", attrs) : "通常";
            }
            catch { }
        }

        private static bool IsImageFile(string ext)
        {
            return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico";
        }

        private static bool IsTextFile(string ext)
        {
            return ext is ".txt" or ".log" or ".json" or ".xml" or ".csv" or ".md" or ".cs" or ".js" or ".ts" or ".html" or ".css" or ".xaml" or ".yaml" or ".yml" or ".ini" or ".bat" or ".ps1" or ".cmd" or ".sh" or ".sql" or ".py";
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
