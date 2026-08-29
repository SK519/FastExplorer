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
                    try
                    {
                        var src = new SoftwareBitmapSource();
                        _ = src.SetBitmapAsync(SoftwareBitmap.Copy(IconThumbnailService.DefaultFolderBitmap));
                        PreviewLargeImageIcon.Source = src;
                        PreviewLargeImageIcon.Visibility = Visibility.Visible;
                    }
                    catch { }
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

        private void ShowCurrentFolderPreview(string? folderPath, CancellationToken ct)
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

        private async void LoadImagePreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                // デバウンス
                await Task.Delay(40, ct);
                if (ct.IsCancellationRequested) return;

                uint width = 0;
                uint height = 0;
                SoftwareBitmap? decodedBitmap = null;

                // バックグラウンドで SkiaSharp を用いた確実・高速なデコード
                await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(filePath)) return;
                        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var codec = SkiaSharp.SKCodec.Create(stream);
                        if (codec != null)
                        {
                            width = (uint)codec.Info.Width;
                            height = (uint)codec.Info.Height;

                            int targetSize = 600;
                            int targetW, targetH;
                            if (width >= height)
                            {
                                targetW = Math.Min((int)width, targetSize);
                                targetH = Math.Max(1, (int)((double)height / width * targetW));
                            }
                            else
                            {
                                targetH = Math.Min((int)height, targetSize);
                                targetW = Math.Max(1, (int)((double)width / height * targetH));
                            }

                            var imageInfo = new SkiaSharp.SKImageInfo(targetW, targetH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                            using var origBmp = SkiaSharp.SKBitmap.Decode(codec);
                            if (origBmp != null)
                            {
                                using var resized = origBmp.Resize(imageInfo, SkiaSharp.SKSamplingOptions.Default);
                                if (resized != null)
                                {
                                    var pixelSpan = resized.GetPixelSpan();
                                    int byteCount = pixelSpan.Length;
                                    byte[] rentedArray = ArrayPool<byte>.Shared.Rent(byteCount);
                                    try
                                    {
                                        pixelSpan.CopyTo(rentedArray);
                                        var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, targetW, targetH, BitmapAlphaMode.Premultiplied);
                                        sb.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(rentedArray, 0, byteCount));
                                        decodedBitmap = sb;
                                    }
                                    finally
                                    {
                                        ArrayPool<byte>.Shared.Return(rentedArray);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }, ct);

                if (ct.IsCancellationRequested) return;

                if (decodedBitmap != null)
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(decodedBitmap);
                    if (!ct.IsCancellationRequested)
                    {
                        PreviewImage.Source = source;
                        PreviewImage.Visibility = Visibility.Visible;
                        if (width > 0 && height > 0)
                        {
                            PreviewResolutionRow.Visibility = Visibility.Visible;
                            PreviewItemResolutionText.Text = $"{width} × {height} ピクセル";
                        }
                        return;
                    }
                }

                // SkiaSharp で取得できなかった場合の Shell サムネイルフォールバック
                var shellBmp = await Task.Run(() => IconThumbnailService.ExtractThumbnailViaShellItem(filePath, 384), ct);
                if (shellBmp != null && !ct.IsCancellationRequested)
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(shellBmp);
                    if (!ct.IsCancellationRequested)
                    {
                        PreviewImage.Source = source;
                        PreviewImage.Visibility = Visibility.Visible;
                        return;
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uEB9F";
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

        private async void LoadVideoPreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                await Task.Delay(40, ct);
                if (ct.IsCancellationRequested) return;

                var shellBmp = await Task.Run(() => IconThumbnailService.ExtractThumbnailViaShellItem(filePath, 384), ct);
                if (shellBmp != null && !ct.IsCancellationRequested)
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(shellBmp);
                    if (!ct.IsCancellationRequested)
                    {
                        PreviewImage.Source = source;
                        PreviewImage.Visibility = Visibility.Visible;
                        return;
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE8B2";
                }
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE8B2";
                }
            }
        }

        private async void LoadTextPreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                await Task.Delay(40, ct);
                if (ct.IsCancellationRequested) return;

                const int maxChars = 3000;
                string? previewText = null;

                await Task.Run(() =>
                {
                    try
                    {
                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
                        char[] buffer = new char[maxChars];
                        int readCount = reader.ReadBlock(buffer, 0, maxChars);
                        string text = new string(buffer, 0, readCount);
                        if (readCount == maxChars)
                        {
                            text += "\n\n... (プレビュー上限)";
                        }
                        previewText = text;
                    }
                    catch { }
                }, ct);

                if (ct.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(previewText))
                {
                    PreviewTextBlock.Text = previewText;
                    PreviewTextScrollViewer.Visibility = Visibility.Visible;
                }
                else
                {
                    PreviewLargeIconGrid.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Visibility = Visibility.Visible;
                    PreviewLargeFontIcon.Glyph = "\uE8C8";
                }
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

        private async void LoadGenericFilePreviewAsync(FileItem item, CancellationToken ct)
        {
            PreviewLargeIconGrid.Visibility = Visibility.Visible;

            if (item.Icon != null)
            {
                PreviewLargeImageIcon.Source = item.Icon;
                PreviewLargeImageIcon.Visibility = Visibility.Visible;
                PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
            }

            try
            {
                // PDF や ドキュメント等の Shell サムネイル試行
                var shellBmp = await Task.Run(() => IconThumbnailService.ExtractThumbnailViaShellItem(item.FullPath, 256), ct);
                if (shellBmp != null && !ct.IsCancellationRequested)
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(shellBmp);
                    if (!ct.IsCancellationRequested)
                    {
                        PreviewLargeIconGrid.Visibility = Visibility.Collapsed;
                        PreviewImage.Source = source;
                        PreviewImage.Visibility = Visibility.Visible;
                        return;
                    }
                }

                // 一般ファイルアイコンの高品質取得
                var bmp = await Task.Run(() => IconThumbnailService.GetSoftwareBitmapForPath(item.FullPath, item.IsDirectory, true), ct);
                if (bmp != null && !ct.IsCancellationRequested)
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(SoftwareBitmap.Copy(bmp));
                    if (!ct.IsCancellationRequested)
                    {
                        PreviewLargeImageIcon.Source = source;
                        PreviewLargeImageIcon.Visibility = Visibility.Visible;
                        PreviewLargeFontIcon.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch { }
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

        private async void LoadFolderDetailsAndSizeAsync(string folderPath, CancellationToken ct)
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

                // バックグラウンドでフォルダサイズとアイテム数を非同期計算
                var (bytes, fileCount, dirCount, timedOut) = await Task.Run(() =>
                {
                    long totalBytes = 0;
                    int fCount = 0;
                    int dCount = 0;
                    bool isTimedOut = false;
                    try
                    {
                        var stack = new Stack<string>();
                        stack.Push(folderPath);
                        var sw = Stopwatch.StartNew();

                        while (stack.Count > 0 && !ct.IsCancellationRequested)
                        {
                            if (sw.ElapsedMilliseconds > 3000)
                            {
                                isTimedOut = true;
                                break;
                            }
                            string current = stack.Pop();
                            try
                            {
                                var dir = new DirectoryInfo(current);
                                foreach (var file in dir.EnumerateFiles())
                                {
                                    if (ct.IsCancellationRequested) return (0L, 0, 0, false);
                                    totalBytes += file.Length;
                                    fCount++;
                                }
                                foreach (var subDir in dir.EnumerateDirectories())
                                {
                                    if (ct.IsCancellationRequested) return (0L, 0, 0, false);
                                    dCount++;
                                    stack.Push(subDir.FullName);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                    return (totalBytes, fCount, dCount, isTimedOut);
                }, ct);

                if (ct.IsCancellationRequested) return;

                if (fileCount == 0 && dirCount == 0 && !timedOut)
                {
                    PreviewItemSizeText.Text = "0 バイト (空のフォルダー)";
                }
                else
                {
                    string suffix = timedOut ? " (一部集計)" : "";
                    PreviewItemSizeText.Text = $"{FileItem.FormatFileSize(bytes)} ({fileCount} ファイル, {dirCount} フォルダー){suffix}";
                }
            }
            catch { }
        }

        private static bool IsImageFile(string ext)
        {
            return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico" or ".tiff" or ".tif" or ".avif" or ".svg" or ".heic";
        }

        private static bool IsVideoFile(string ext)
        {
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v" or ".3gp" or ".ts";
        }

        private static bool IsTextFile(string ext)
        {
            return ext is ".txt" or ".log" or ".json" or ".xml" or ".csv" or ".tsv" or ".md" or ".cs" or ".js" or ".jsx" or ".ts" or ".tsx" or ".html" or ".htm" or ".css" or ".scss" or ".xaml" or ".yaml" or ".yml" or ".ini" or ".bat" or ".ps1" or ".cmd" or ".sh" or ".sql" or ".py" or ".cpp" or ".c" or ".h" or ".hpp" or ".rs" or ".go" or ".java" or ".kt" or ".php" or ".rb" or ".toml" or ".env" or ".gitignore" or ".gitattributes" or ".editorconfig" or ".properties" or ".config";
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
