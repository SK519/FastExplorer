using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    public static partial class ArchiveService
    {
        #region Extraction (ZIP, 7-Zip, RAR, TAR, GZ, BZ2, XZ, ZST)

        /// <summary>
        /// アーカイブファイルを指定されたディレクトリに解凍・展開 (Rust ネイティブ優先)
        /// </summary>
        public static async Task<bool> ExtractAsync(
            string archivePath,
            string destinationDirectory,
            bool createSubFolder = true,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            if (!File.Exists(archivePath) || !Directory.Exists(destinationDirectory)) return false;

            string targetDir;
            if (createSubFolder)
            {
                string folderName = GetArchiveBaseName(archivePath);
                targetDir = FileOperationService.GetUniqueDestinationPath(destinationDirectory, folderName);
            }
            else
            {
                targetDir = destinationDirectory;
            }

            bool targetDirExistedBefore = Directory.Exists(targetDir);
            var createdFiles = new List<string>();
            var createdDirs = new List<string>();

            bool success = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                        createdDirs.Add(targetDir);
                    }

                    if (NativeArchiveInterop.IsAvailable)
                    {
                        int code = NativeArchiveInterop.ExtractAll(
                            archivePath,
                            targetDir,
                            "アーカイブを展開中",
                            progress,
                            controller,
                            out string? err);

                        if (code == NativeArchiveInterop.FA_OK)
                        {
                            return true;
                        }

                        Debug.WriteLine($"[ArchiveService.ExtractAsync] Native Rust failed ({code}): {err}");
                        if (code == NativeArchiveInterop.FA_ERR_CANCELLED || controller?.IsCancelled == true)
                        {
                            return false;
                        }
                    }

                    // フォールバック: 標準 ZIP の場合は .NET ZipFile で展開
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return ExtractZipManaged(archivePath, targetDir, progress, controller, createdFiles, createdDirs);
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ArchiveService.ExtractAsync] Error: {ex.Message}");
                    return false;
                }
            });

            if (!success)
            {
                CleanupExtractedItems(targetDir, createSubFolder, targetDirExistedBefore, createdFiles, createdDirs);
                return false;
            }

            return true;
        }

        private static bool ExtractZipManaged(
            string archivePath,
            string targetDir,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            List<string> createdFiles,
            List<string> createdDirs)
        {
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                long totalBytes = zip.Entries.Sum(e => e.Length);
                int totalFiles = zip.Entries.Count(e => !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\"));

                var progressState = new FileTransferProgress
                {
                    CustomTitle = "ZIPアーカイブを展開中",
                    TotalBytes = totalBytes,
                    TotalFiles = Math.Max(1, totalFiles),
                    SourceDirectory = Path.GetDirectoryName(archivePath) ?? "",
                    DestinationDirectory = targetDir,
                    IsSizeCalculating = false
                };

                long bytesTransferred = 0;
                int filesTransferred = 0;
                var sw = Stopwatch.StartNew();
                long lastReportTick = 0;
                byte[] buffer = new byte[BufferSize];
                string fullTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                foreach (var entry in zip.Entries)
                {
                    controller?.WaitIfPaused();
                    if (controller?.IsCancelled == true) return false;

                    string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                    if (relativePath.Contains(':')) continue;
                    string destinationPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));
                    if (!destinationPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(destinationPath) && (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0) continue;

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        if (!Directory.Exists(destinationPath))
                        {
                            Directory.CreateDirectory(destinationPath);
                            createdDirs.Add(destinationPath);
                        }
                        continue;
                    }

                    string? parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                        createdDirs.Add(parentDir);
                    }

                    progressState.CurrentFileName = entry.Name;
                    progressState.FilesTransferred = filesTransferred;
                    progress?.Report(progressState);

                    if (!File.Exists(destinationPath)) createdFiles.Add(destinationPath);

                    using (var entryStream = entry.Open())
                    using (var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            controller?.WaitIfPaused();
                            if (controller?.IsCancelled == true) return false;

                            outStream.Write(buffer, 0, read);
                            bytesTransferred += read;

                            if (sw.ElapsedMilliseconds - lastReportTick >= 100)
                            {
                                lastReportTick = sw.ElapsedMilliseconds;
                                progressState.BytesTransferred = bytesTransferred;
                                UpdateRateAndEta(progressState, bytesTransferred, totalBytes, sw);
                                progress?.Report(progressState);
                            }
                        }
                    }

                    filesTransferred++;
                }

                progressState.BytesTransferred = totalBytes;
                progressState.FilesTransferred = totalFiles;
                progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                progress?.Report(progressState);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ArchiveService.ExtractZipManaged] Error: {ex.Message}");
                return false;
            }
        }

        private static void UpdateRateAndEta(FileTransferProgress state, long bytesTransferred, long totalBytes, Stopwatch sw)
        {
            double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
            state.BytesPerSecond = bytesTransferred / elapsedSec;
            if (state.BytesPerSecond > 0 && totalBytes > bytesTransferred)
            {
                state.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / state.BytesPerSecond);
            }
            else
            {
                state.EstimatedTimeRemaining = TimeSpan.Zero;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void CleanupExtractedItems(
            string targetDir,
            bool createSubFolder,
            bool targetDirExistedBefore,
            List<string> createdFiles,
            List<string> createdDirs)
        {
            Thread.Sleep(50);
            try
            {
                if (createSubFolder && !targetDirExistedBefore)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            if (Directory.Exists(targetDir))
                            {
                                Directory.Delete(targetDir, recursive: true);
                            }
                            break;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(100);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
                else
                {
                    foreach (var f in createdFiles)
                    {
                        try
                        {
                            if (File.Exists(f)) File.Delete(f);
                        }
                        catch { }
                    }

                    createdDirs.Reverse();
                    foreach (var d in createdDirs)
                    {
                        try
                        {
                            if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                            {
                                Directory.Delete(d);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ArchiveService.CleanupExtractedItems] Error: {ex.Message}");
            }
        }

        #endregion
    }
}
