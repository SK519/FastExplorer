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

            // 管理者権限が必要なフォルダー（アクセス権がないフォルダー）の場合、Windows シェル (IFileOperation) に委譲
            // これにより、Windows 標準の「管理者のアクセス許可を提供する必要があります」確認画面および UAC 昇格プロンプトが表示される
            if (!ShellFileOperationHelper.HasWritePermission(destinationDirectory))
            {
                Debug.WriteLine($"[FileOperation] Destination requires elevation: '{destinationDirectory}'. Delegating to IFileOperation.");
                return await ShellFileOperationHelper.ExecuteCopyOrMoveAsync(pathList, destinationDirectory, isMove, DefaultOwnerHwnd, forceElevate: true);
            }

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
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[FileOperation] Access denied during transfer, delegating to elevated IFileOperation: {ex.Message}");
                return await ShellFileOperationHelper.ExecuteCopyOrMoveAsync(pathList, destinationDirectory, isMove, DefaultOwnerHwnd, forceElevate: true);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 5)
            {
                Debug.WriteLine($"[FileOperation] ERROR_ACCESS_DENIED during transfer, delegating to elevated IFileOperation: {ex.Message}");
                return await ShellFileOperationHelper.ExecuteCopyOrMoveAsync(pathList, destinationDirectory, isMove, DefaultOwnerHwnd, forceElevate: true);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Any(e => e is UnauthorizedAccessException || (e is IOException ioe && (ioe.HResult & 0xFFFF) == 5)))
            {
                Debug.WriteLine($"[FileOperation] Access denied in parallel tasks, delegating to elevated IFileOperation: {ex.Message}");
                return await ShellFileOperationHelper.ExecuteCopyOrMoveAsync(pathList, destinationDirectory, isMove, DefaultOwnerHwnd, forceElevate: true);
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
                        if (src.Equals(destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.Equals(src, destPath, StringComparison.Ordinal))
                            {
                                File.Move(src, destPath);
                            }
                        }
                        else
                        {
                            File.Move(src, destPath, overwrite: true);
                        }
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
    }
}
