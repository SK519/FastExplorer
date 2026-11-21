using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Views.Dialogs;

namespace FastExplorer.Services
{
    public static partial class FileOperationService
    {
        private static bool TryBlockCloneCopy(string src, string destPath, long fileLength)
        {
            if (fileLength < 0) return false;

            string? srcRoot = Path.GetPathRoot(src);
            string? destRoot = Path.GetPathRoot(destPath);
            if (string.IsNullOrEmpty(srcRoot) || string.IsNullOrEmpty(destRoot) ||
                !srcRoot.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using var hSrc = Win32Interop.CreateFileW(
                    src,
                    Win32Interop.GENERIC_READ,
                    Win32Interop.FILE_SHARE_READ | Win32Interop.FILE_SHARE_WRITE | Win32Interop.FILE_SHARE_DELETE,
                    nint.Zero,
                    Win32Interop.OPEN_EXISTING,
                    0,
                    nint.Zero);

                if (hSrc.IsInvalid) return false;

                using var hDest = Win32Interop.CreateFileW(
                    destPath,
                    Win32Interop.GENERIC_READ | Win32Interop.GENERIC_WRITE,
                    Win32Interop.FILE_SHARE_READ,
                    nint.Zero,
                    Win32Interop.CREATE_ALWAYS,
                    0,
                    nint.Zero);

                if (hDest.IsInvalid) return false;

                if (fileLength == 0)
                {
                    return true;
                }

                if (!Win32Interop.SetFilePointerEx(hDest, fileLength, out _, Win32Interop.FILE_BEGIN) ||
                    !Win32Interop.SetEndOfFile(hDest) ||
                    !Win32Interop.SetFilePointerEx(hDest, 0, out _, Win32Interop.FILE_BEGIN))
                {
                    return false;
                }

                var data = new Win32Interop.DUPLICATE_EXTENTS_DATA
                {
                    FileHandle = hSrc.DangerousGetHandle(),
                    SourceFileOffset = 0,
                    TargetFileOffset = 0,
                    ByteCount = fileLength
                };

                uint dataSize = (uint)Marshal.SizeOf<Win32Interop.DUPLICATE_EXTENTS_DATA>();
                bool success = Win32Interop.DeviceIoControl(
                    hDest,
                    Win32Interop.FSCTL_DUPLICATE_EXTENTS_TO_FILE,
                    ref data,
                    dataSize,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero);

                if (success)
                {
                    try
                    {
                        File.SetCreationTimeUtc(destPath, File.GetCreationTimeUtc(src));
                        File.SetLastWriteTimeUtc(destPath, File.GetLastWriteTimeUtc(src));
                        File.SetAttributes(destPath, File.GetAttributes(src));
                    }
                    catch { }
                    return true;
                }
            }
            catch { }

            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);
            }
            catch { }

            return false;
        }

        private static async Task<bool> CopyFileExAsync(
            string src,
            string destPath,
            long fileLength,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            FileTransferState state)
        {
            return await Task.Run(() =>
            {
                int pbCancel = 0;
                long lastTransferred = 0;

                Win32Interop.CopyProgressRoutine progressRoutine = (
                    long totalFileSize,
                    long totalBytesTransferred,
                    long streamSize,
                    long streamBytesTransferred,
                    uint dwStreamNumber,
                    uint dwCallbackReason,
                    nint hSourceFile,
                    nint hDestinationFile,
                    nint lpData) =>
                {
                    controller?.WaitIfPaused();
                    if (controller?.IsCancelled == true)
                    {
                        pbCancel = 1;
                        return Win32Interop.PROGRESS_CANCEL;
                    }

                    long delta = totalBytesTransferred - lastTransferred;
                    if (delta > 0)
                    {
                        Interlocked.Add(ref state.BytesTransferred, delta);
                        lastTransferred = totalBytesTransferred;
                        state.UpdateSpeed();
                        ReportCurrentProgress(progress, state, controller, force: false);
                    }

                    return Win32Interop.PROGRESS_CONTINUE;
                };

                uint copyFlags = Win32Interop.COPY_FILE_ALLOW_DECRYPTED_DESTINATION | Win32Interop.COPY_FILE_OPEN_AND_COPY_REPARSE_POINT;

                bool result = Win32Interop.CopyFileExW(
                    src,
                    destPath,
                    progressRoutine,
                    nint.Zero,
                    ref pbCancel,
                    copyFlags);

                if (!result)
                {
                    int lastErr = Marshal.GetLastWin32Error();
                    if (lastTransferred > 0)
                    {
                        Interlocked.Add(ref state.BytesTransferred, -lastTransferred);
                    }
                    if (pbCancel != 0 || controller?.IsCancelled == true)
                    {
                        throw new OperationCanceledException();
                    }
                    if (lastErr == 5) // ERROR_ACCESS_DENIED
                    {
                        throw new UnauthorizedAccessException($"Access denied copying '{src}' to '{destPath}'");
                    }
                    return false;
                }

                long remaining = fileLength - lastTransferred;
                if (remaining > 0)
                {
                    Interlocked.Add(ref state.BytesTransferred, remaining);
                }

                state.UpdateSpeed();
                return true;
            });
        }

        private static async Task CopyFileStreamAsync(
            string src,
            string destPath,
            long fileLength,
            bool isMove,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            FileTransferState state)
        {
            const int bufferSize = 1024 * 1024; // 1 MB
            byte[] buffer = new byte[bufferSize];

            try
            {
                await using (var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
                await using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                {
                    int bytesRead;
                    while ((bytesRead = await srcStream.ReadAsync(buffer.AsMemory())) > 0)
                    {
                        controller?.WaitIfPaused();
                        if (controller?.IsCancelled == true)
                        {
                            throw new OperationCanceledException();
                        }

                        await destStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        Interlocked.Add(ref state.BytesTransferred, bytesRead);
                        state.UpdateSpeed();
                        ReportCurrentProgress(progress, state, controller, force: false);
                    }
                }

                if (isMove)
                {
                    try { File.Delete(src); } catch { }
                }

                Interlocked.Increment(ref state.FilesTransferred);
                ReportCurrentProgress(progress, state, controller, force: true);
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                throw;
            }
        }

        public static bool IsSameOrSubPath(string basePath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidatePath))
                return false;

            try
            {
                string baseFull = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string candidateFull = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (baseFull.Equals(candidateFull, StringComparison.OrdinalIgnoreCase))
                    return true;

                return candidateFull.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       candidateFull.StartsWith(baseFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static (long TotalBytes, int TotalFiles) CalculateTotalSize(IEnumerable<string> paths, CancellationToken ct = default)
        {
            var pathList = paths as IReadOnlyList<string> ?? paths.ToList();

            // Rust ネイティブ並列集計を優先 (アクセス拒否フォルダーを安全スキップ & マルチコア高速集計)
            if (NativeArchiveInterop.CalcFolderSize(
                    pathList,
                    onProgress: null,
                    ct,
                    out long nativeBytes,
                    out long nativeFiles,
                    out _))
            {
                return (nativeBytes, Math.Max(1, (int)Math.Min(int.MaxValue, nativeFiles)));
            }

            long totalBytes = 0;
            int totalFiles = 0;

            foreach (var path in pathList)
            {
                if (ct.IsCancellationRequested) break;
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
                            if (ct.IsCancellationRequested) break;
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
            FileTransferController? controller,
            bool force = false)
        {
            if (progress == null) return;

            long now = Stopwatch.GetTimestamp();
            if (!force)
            {
                // 50ms 未満の頻繁な通知をスロットリングして UI ディスパッチ負荷を抑制
                double elapsedMs = (now - state.LastReportTimestamp) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < 50.0) return;
            }
            state.LastReportTimestamp = now;

            var p = new FileTransferProgress
            {
                CurrentFileName = state.CurrentFileName,
                SourceDirectory = state.SourceDirectory,
                DestinationDirectory = state.DestinationDirectory,
                TotalBytes = Interlocked.Read(ref state.TotalBytes),
                BytesTransferred = Interlocked.Read(ref state.BytesTransferred),
                TotalFiles = state.TotalFiles,
                FilesTransferred = state.FilesTransferred,
                BytesPerSecond = state.CurrentSpeedBytesPerSec,
                EstimatedTimeRemaining = state.GetEstimatedRemainingTime(),
                IsPaused = controller?.IsPaused == true,
                IsCancelled = controller?.IsCancelled == true,
                IsMove = state.IsMove,
                IsSizeCalculating = state.IsSizeCalculating
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
            public bool IsSizeCalculating;
            public long LastReportTimestamp = 0;

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
                    long currentBytes = Interlocked.Read(ref BytesTransferred);
                    long dBytes = currentBytes - _lastBytes;
                    double instSpeed = dBytes / Math.Max(0.001, dt);
                    if (CurrentSpeedBytesPerSec <= 0)
                    {
                        CurrentSpeedBytesPerSec = instSpeed;
                    }
                    else
                    {
                        CurrentSpeedBytesPerSec = 0.65 * CurrentSpeedBytesPerSec + 0.35 * instSpeed;
                    }
                    _lastBytes = currentBytes;
                    _lastTimeSec = elapsedSec;
                }
            }

            public TimeSpan GetEstimatedRemainingTime()
            {
                long currentBytes = Interlocked.Read(ref BytesTransferred);
                long currentTotal = Interlocked.Read(ref TotalBytes);
                long remainingBytes = Math.Max(0, currentTotal - currentBytes);
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
