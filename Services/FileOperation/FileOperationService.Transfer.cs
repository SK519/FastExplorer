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

            // 初期化（直下ファイルのサイズ・項目数を同期的に即座に取得して初期値とする）
            long initialBytes = 0;
            int initialFiles = 0;
            bool hasDirectories = false;

            foreach (var p in pathList)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        initialBytes += new FileInfo(p).Length;
                        initialFiles++;
                    }
                    catch { }
                }
                else if (Directory.Exists(p))
                {
                    initialFiles++;
                    hasDirectories = true;
                }
            }

            var state = new FileTransferState
            {
                TotalBytes = initialBytes,
                TotalFiles = Math.Max(1, initialFiles),
                DestinationDirectory = destinationDirectory,
                IsMove = isMove,
                IsSizeCalculating = hasDirectories
            };

            // ディレクトリを含む場合のみバックグラウンドで全体サイズ・ファイル数を再帰集計
            using var sizeCts = new System.Threading.CancellationTokenSource();
            if (hasDirectories)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var (tBytes, tFiles) = CalculateTotalSize(pathList, sizeCts.Token);
                        if (!sizeCts.IsCancellationRequested)
                        {
                            state.TotalBytes = Math.Max(state.BytesTransferred, tBytes);
                            state.TotalFiles = Math.Max(state.FilesTransferred, tFiles);
                        }
                    }
                    catch { }
                    finally
                    {
                        state.IsSizeCalculating = false;
                    }
                }, sizeCts.Token);
            }

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

                    // 1. フォルダーの自己・子孫移動/コピー防止（無限再帰ループ 2\2\2... の完全阻止）
                    if (Directory.Exists(src))
                    {
                        if (IsSameOrSubPath(src, destinationDirectory))
                        {
                            Debug.WriteLine($"[FileOperation] Blocked recursive copy/move of '{src}' into '{destinationDirectory}'");
                            continue;
                        }
                    }

                    // 2. 移動操作で、移動元と移動先の親フォルダーが同一の場合はスキップ（同一フォルダーへの移動は無効）
                    if (isMove)
                    {
                        string? srcParent = Path.GetDirectoryName(src);
                        if (!string.IsNullOrEmpty(srcParent))
                        {
                            string parentFull = Path.GetFullPath(srcParent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string destFull = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (parentFull.Equals(destFull, StringComparison.OrdinalIgnoreCase))
                            {
                                Debug.WriteLine($"[FileOperation] Skipped move of '{src}' to its own folder '{destinationDirectory}'");
                                continue;
                            }
                        }
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

                // 完了時に確実に 100% を進捗報告
                if (controller?.IsCancelled != true)
                {
                    state.IsSizeCalculating = false;
                    state.FilesTransferred = Math.Max(state.FilesTransferred, state.TotalFiles);
                    state.BytesTransferred = Math.Max(state.BytesTransferred, state.TotalBytes);
                    ReportCurrentProgress(progress, state, controller, force: true);
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
            finally
            {
                sizeCts.Cancel();
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

            ReportCurrentProgress(progress, state, controller, force: false);

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
                        Interlocked.Add(ref state.BytesTransferred, fileLength);
                        Interlocked.Increment(ref state.FilesTransferred);
                        state.UpdateSpeed();
                        ReportCurrentProgress(progress, state, controller, force: true);
                        return;
                    }
                    catch
                    {
                        // フォールバック: コピー＆削除
                    }
                }
            }

            // 1. ReFS / Dev Drive (Windows 11) Block Cloning (Copy-on-Write) の試行
            // 同一ドライブ内のコピーであれば、実データ転送なしで 0 秒複製
            if (!isMove && TryBlockCloneCopy(src, destPath, fileLength))
            {
                Interlocked.Add(ref state.BytesTransferred, fileLength);
                Interlocked.Increment(ref state.FilesTransferred);
                state.UpdateSpeed();
                ReportCurrentProgress(progress, state, controller, force: true);
                return;
            }

            // 2. Win32 CopyFileExW によるカーネルダイレクト高速転送 (DMA / ODX オフロード)
            try
            {
                bool kernelCopied = await CopyFileExAsync(src, destPath, fileLength, progress, controller, state);
                if (kernelCopied)
                {
                    if (isMove)
                    {
                        try { File.Delete(src); } catch { }
                    }
                    Interlocked.Increment(ref state.FilesTransferred);
                    ReportCurrentProgress(progress, state, controller, force: true);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                throw;
            }
            catch
            {
                // フォールバック: チャンク単位のストリームコピー
            }

            // 3. フォールバック: チャンク単位の非同期ストリームコピー
            await CopyFileStreamAsync(src, destPath, fileLength, isMove, progress, controller, state);
        }

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
                    if (lastTransferred > 0)
                    {
                        Interlocked.Add(ref state.BytesTransferred, -lastTransferred);
                    }
                    if (pbCancel != 0 || controller?.IsCancelled == true)
                    {
                        throw new OperationCanceledException();
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

        private static async Task CopyOrMoveDirectoryAsync(
            string sourceDir,
            string targetDir,
            bool isMove,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            FileTransferState state)
        {
            if (IsSameOrSubPath(sourceDir, targetDir))
            {
                return;
            }

            // 同一ドライブ間の移動で、移動先フォルダーがまだ存在しない場合は Directory.Move で高速移動
            if (isMove)
            {
                bool sameDrive = Path.GetPathRoot(sourceDir)?.Equals(Path.GetPathRoot(targetDir), StringComparison.OrdinalIgnoreCase) == true;
                if (sameDrive && !Directory.Exists(targetDir))
                {
                    try
                    {
                        state.CurrentFileName = new DirectoryInfo(sourceDir).Name;
                        Directory.Move(sourceDir, targetDir);
                        Interlocked.Increment(ref state.FilesTransferred);
                        ReportCurrentProgress(progress, state, controller, force: true);
                        return;
                    }
                    catch
                    {
                        // フォールバック: 個別ファイルコピー/移動
                    }
                }
            }

            Directory.CreateDirectory(targetDir);

            var files = Directory.GetFiles(sourceDir);

            // 小規模ファイル群の並行コピー (最大並行度 6) による I/O スループットの最大化
            int maxConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);
            if (files.Length > 1)
            {
                using var semaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = new List<Task>(files.Length);

                foreach (string file in files)
                {
                    controller?.WaitIfPaused();
                    if (controller?.IsCancelled == true) throw new OperationCanceledException();

                    string dest = Path.Combine(targetDir, Path.GetFileName(file));
                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            controller?.WaitIfPaused();
                            if (controller?.IsCancelled == true) throw new OperationCanceledException();
                            await CopyOrMoveSingleFileAsync(file, dest, isMove, progress, controller, state);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
            }
            else if (files.Length == 1)
            {
                controller?.WaitIfPaused();
                if (controller?.IsCancelled == true) throw new OperationCanceledException();

                string dest = Path.Combine(targetDir, Path.GetFileName(files[0]));
                await CopyOrMoveSingleFileAsync(files[0], dest, isMove, progress, controller, state);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                controller?.WaitIfPaused();
                if (controller?.IsCancelled == true) throw new OperationCanceledException();

                // サブフォルダーのコピー先が循環参照にならないかチェック
                if (IsSameOrSubPath(subDir, targetDir)) continue;

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
            long totalBytes = 0;
            int totalFiles = 0;

            foreach (var path in paths)
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
