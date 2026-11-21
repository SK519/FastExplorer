using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;

namespace FastExplorer.Views.MainWindow.Preview
{
    public sealed partial class PreviewPaneControl
    {
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

                try
                {
                    // バックグラウンドで OS ネイティブ WIC を用いた確実・高速なデコード
                    await Task.Run(async () =>
                    {
                        try
                        {
                            if (!File.Exists(filePath)) return;
                            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var randomStream = stream.AsRandomAccessStream();
                            var decoder = await BitmapDecoder.CreateAsync(randomStream);
                            width = decoder.PixelWidth;
                            height = decoder.PixelHeight;

                            uint targetSize = 600;
                            var transform = new BitmapTransform();
                            if (width > targetSize || height > targetSize)
                            {
                                if (width >= height)
                                {
                                    transform.ScaledWidth = targetSize;
                                    transform.ScaledHeight = Math.Max(1, (uint)((double)height / width * targetSize));
                                }
                                else
                                {
                                    transform.ScaledHeight = targetSize;
                                    transform.ScaledWidth = Math.Max(1, (uint)((double)width / height * targetSize));
                                }
                                transform.InterpolationMode = BitmapInterpolationMode.Linear;
                            }

                            decodedBitmap = await decoder.GetSoftwareBitmapAsync(
                                BitmapPixelFormat.Bgra8,
                                BitmapAlphaMode.Premultiplied,
                                transform,
                                ExifOrientationMode.RespectExifOrientation,
                                ColorManagementMode.ColorManageToSRgb);
                        }
                        catch { }
                    }, ct);

                    if (ct.IsCancellationRequested) return;

                    if (decodedBitmap != null)
                    {
                        var source = IconThumbnailService.CreateWriteableBitmapFromSoftwareBitmap(decodedBitmap);
                        if (source != null && !ct.IsCancellationRequested)
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
                }
                finally
                {
                    decodedBitmap?.Dispose();
                }

                if (ct.IsCancellationRequested) return;

                // WIC で取得できなかった場合の Shell サムネイルフォールバック
                using var shellBmp = await Task.Run(() => IconThumbnailService.ExtractThumbnailViaShellItem(filePath, 384), ct);
                if (shellBmp != null && !ct.IsCancellationRequested)
                {
                    var source = IconThumbnailService.CreateWriteableBitmapFromSoftwareBitmap(shellBmp);
                    if (source != null && !ct.IsCancellationRequested)
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

                using var shellBmp = await Task.Run(() => IconThumbnailService.ExtractThumbnailViaShellItem(filePath, 384), ct);
                if (shellBmp != null && !ct.IsCancellationRequested)
                {
                    var source = IconThumbnailService.CreateWriteableBitmapFromSoftwareBitmap(shellBmp);
                    if (source != null && !ct.IsCancellationRequested)
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

        private async void LoadArchivePreviewAsync(string filePath, CancellationToken ct)
        {
            try
            {
                await Task.Delay(40, ct);
                if (ct.IsCancellationRequested) return;

                string? archiveText = await ArchiveService.GetArchivePreviewTextAsync(filePath, maxEntries: 100);
                if (ct.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(archiveText))
                {
                    PreviewTextBlock.Text = archiveText;
                    PreviewTextScrollViewer.Visibility = Visibility.Visible;
                    PreviewLargeIconGrid.Visibility = Visibility.Collapsed;
                }
                else
                {
                    LoadGenericFilePreviewAsync(new FileItem { FullPath = filePath, Name = Path.GetFileName(filePath) }, ct);
                }
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                {
                    LoadGenericFilePreviewAsync(new FileItem { FullPath = filePath, Name = Path.GetFileName(filePath) }, ct);
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
                using var shellBmp = await Task.Run(() => IconThumbnailService.ExtractThumbnailViaShellItem(item.FullPath, 256), ct);
                if (shellBmp != null && !ct.IsCancellationRequested)
                {
                    var source = IconThumbnailService.CreateWriteableBitmapFromSoftwareBitmap(shellBmp);
                    if (source != null && !ct.IsCancellationRequested)
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
                    await SetCopiedBitmapSafeAsync(PreviewLargeImageIcon, bmp);
                    if (!ct.IsCancellationRequested)
                    {
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
            if (folderPath.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

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
    }
}
