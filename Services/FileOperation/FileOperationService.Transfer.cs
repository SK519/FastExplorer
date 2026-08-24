using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastExplorer.Models;
using FastExplorer.Views.Dialogs;

namespace FastExplorer.Services
{
    public static partial class FileOperationService
    {
        public static async Task<bool> ExecuteCopyOrMoveAsync(
            IEnumerable<string> sourcePaths,
            string destinationDirectory,
            bool isMove,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                return false;

            var pathList = sourcePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0) return false;

            // 事前にファイル総数と合計サイズを計算
            var (totalBytes, totalFiles) = await Task.Run(() => CalculateTotalSize(pathList));

            var state = new FileTransferState
            {
                TotalBytes = totalBytes,
                TotalFiles = totalFiles,
                DestinationDirectory = destinationDirectory,
                IsMove = isMove
            };

            ConflictResolution? rememberedResolution = null;

            try
            {
                foreach (var src in pathList)
                {
                    controller?.WaitIfPaused();
                    if (controller?.IsCancelled == true)
                    {
                        break;
                    }

                    string name = Path.GetFileName(src);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = new DirectoryInfo(src).Name;
                    }

                    state.SourceDirectory = Path.GetDirectoryName(src) ?? string.Empty;
                    string destPath = Path.Combine(destinationDirectory, name);
                    bool isSamePath = src.Equals(destPath, StringComparison.OrdinalIgnoreCase);

                    if (isSamePath)
                    {
                        if (isMove) continue;
                        destPath = GetUniqueDestinationPath(destinationDirectory, name);
                    }
                    else if (File.Exists(destPath) || Directory.Exists(destPath))
                    {
                        ConflictResolution resolution;
                        if (rememberedResolution.HasValue)
                        {
                            resolution = rememberedResolution.Value;
                        }
                        else if (ConflictResolver != null)
                        {
                            var result = await ConflictResolver(src, destPath);
                            resolution = result.Resolution;
                            if (result.ApplyToAll)
                            {
                                rememberedResolution = resolution;
                            }
                        }
                        else
                        {
                            resolution = ConflictResolution.KeepBoth;
                        }

                        if (resolution == ConflictResolution.Cancel)
                        {
                            break;
                        }
                        else if (resolution == ConflictResolution.Skip)
                        {
                            continue;
                        }
                        else if (resolution == ConflictResolution.KeepBoth)
                        {
                            destPath = GetUniqueDestinationPath(destinationDirectory, name);
                        }
                    }

                    if (File.Exists(src))
                    {
                        await CopyOrMoveSingleFileAsync(src, destPath, isMove, progress, controller, state);
                    }
                    else if (Directory.Exists(src))
                    {
                        await CopyOrMoveDirectoryAsync(src, destPath, isMove, progress, controller, state);
                    }
                }

                return controller?.IsCancelled != true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileOperation] ExecuteCopyOrMoveAsync error: {ex.Message}");
                return false;
            }
        }

        private static async Task CopyOrMoveSingleFileAsync(
            string src,
            string destPath,
            bool isMove,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            FileTransferState state)
        {
            var fileInfo = new FileInfo(src);
            long fileLength = fileInfo.Length;
            state.CurrentFileName = fileInfo.Name;

            ReportCurrentProgress(progress, state, controller);

            if (isMove)
            {
                // 同一ドライブ間の移動はファイルシステムレベルで高速移動
                bool sameDrive = Path.GetPathRoot(src)?.Equals(Path.GetPathRoot(destPath), StringComparison.OrdinalIgnoreCase) == true;
                if (sameDrive)
                {
                    try
                    {
                        if (File.Exists(destPath) && !src.Equals(destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(destPath);
                        }
                        File.Move(src, destPath);
                        state.BytesTransferred += fileLength;
                        state.FilesTransferred++;
                        state.UpdateSpeed();
                        ReportCurrentProgress(progress, state, controller);
                        return;
                    }
                    catch
                    {
                        // フォールバック: コピー＆削除
                    }
                }
            }

            // チャンク単位のストリームコピー
            const int bufferSize = 1024 * 1024; // 1 MB
            byte[] buffer = new byte[bufferSize];

            try
            {
                await using (var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
                await using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                {
                    int bytesRead;
                    while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        controller?.WaitIfPaused();
                        if (controller?.IsCancelled == true)
                        {
                            throw new OperationCanceledException();
                        }

                        await destStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        state.BytesTransferred += bytesRead;
                        state.UpdateSpeed();
                        ReportCurrentProgress(progress, state, controller);
                    }
                }

                if (isMove)
                {
                    try
                    {
                        File.Delete(src);
                    }
                    catch { }
                }

                state.FilesTransferred++;
                ReportCurrentProgress(progress, state, controller);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (File.Exists(destPath)) File.Delete(destPath);
                }
                catch { }
                throw;
            }
        }

        private static async Task CopyOrMoveDirectoryAsync(
            string sourceDir,
            string targetDir,
            bool isMove,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            FileTransferState state)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                controller?.WaitIfPaused();
                if (controller?.IsCancelled == true) throw new OperationCanceledException();

                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                await CopyOrMoveSingleFileAsync(file, dest, isMove, progress, controller, state);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                controller?.WaitIfPaused();
                if (controller?.IsCancelled == true) throw new OperationCanceledException();

                string dest = Path.Combine(targetDir, Path.GetFileName(subDir));
                await CopyOrMoveDirectoryAsync(subDir, dest, isMove, progress, controller, state);
            }

            if (isMove)
            {
                try
                {
                    if (Directory.Exists(sourceDir) && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
                    {
                        Directory.Delete(sourceDir, true);
                    }
                }
                catch { }
            }
        }

        private static (long TotalBytes, int TotalFiles) CalculateTotalSize(IEnumerable<string> paths)
        {
            long totalBytes = 0;
            int totalFiles = 0;

            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        totalBytes += new FileInfo(path).Length;
                        totalFiles++;
                    }
                    else if (Directory.Exists(path))
                    {
                        var dirInfo = new DirectoryInfo(path);
                        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            totalBytes += file.Length;
                            totalFiles++;
                        }
                    }
                }
                catch { }
            }

            return (totalBytes, Math.Max(1, totalFiles));
        }

        private static void ReportCurrentProgress(
            IProgress<FileTransferProgress>? progress,
            FileTransferState state,
            FileTransferController? controller)
        {
            if (progress == null) return;

            var p = new FileTransferProgress
            {
                CurrentFileName = state.CurrentFileName,
                SourceDirectory = state.SourceDirectory,
                DestinationDirectory = state.DestinationDirectory,
                TotalBytes = state.TotalBytes,
                BytesTransferred = state.BytesTransferred,
                TotalFiles = state.TotalFiles,
                FilesTransferred = state.FilesTransferred,
                BytesPerSecond = state.CurrentSpeedBytesPerSec,
                EstimatedTimeRemaining = state.GetEstimatedRemainingTime(),
                IsPaused = controller?.IsPaused == true,
                IsCancelled = controller?.IsCancelled == true,
                IsMove = state.IsMove
            };

            progress.Report(p);
        }

        private class FileTransferState
        {
            public string CurrentFileName = string.Empty;
            public string SourceDirectory = string.Empty;
            public string DestinationDirectory = string.Empty;
            public long TotalBytes;
            public long BytesTransferred;
            public int TotalFiles;
            public int FilesTransferred;
            public bool IsMove;

            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private long _lastBytes = 0;
            private double _lastTimeSec = 0;
            public double CurrentSpeedBytesPerSec = 0;

            public void UpdateSpeed()
            {
                double elapsedSec = _stopwatch.Elapsed.TotalSeconds;
                double dt = elapsedSec - _lastTimeSec;
                if (dt >= 0.08)
                {
                    long dBytes = BytesTransferred - _lastBytes;
                    double instSpeed = dBytes / Math.Max(0.001, dt);
                    if (CurrentSpeedBytesPerSec <= 0)
                    {
                        CurrentSpeedBytesPerSec = instSpeed;
                    }
                    else
                    {
                        CurrentSpeedBytesPerSec = 0.65 * CurrentSpeedBytesPerSec + 0.35 * instSpeed;
                    }
                    _lastBytes = BytesTransferred;
                    _lastTimeSec = elapsedSec;
                }
            }

            public TimeSpan GetEstimatedRemainingTime()
            {
                long remainingBytes = Math.Max(0, TotalBytes - BytesTransferred);
                if (CurrentSpeedBytesPerSec > 1024)
                {
                    double remSec = remainingBytes / CurrentSpeedBytesPerSec;
                    if (remSec is > 0 and < 86400)
                    {
                        return TimeSpan.FromSeconds(remSec);
                    }
                }
                return TimeSpan.Zero;
            }
        }

        private static void CopyDirectoryRecursively(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectoryRecursively(subDir, dest);
            }
        }
    }
}
