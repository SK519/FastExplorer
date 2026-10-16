using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace FastExplorer.Services
{
    public static partial class ArchiveService
    {
        private static readonly HashSet<string> SupportedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst"
        };

        private const int BufferSize = 128 * 1024; // 128 KB buffer for smooth progress

        /// <summary>
        /// 指定されたパスの拡張子が対応アーカイブ形式かどうかを判定
        /// </summary>
        public static bool IsSupportedArchive(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            return SupportedArchiveExtensions.Contains(ext);
        }

        private class ArchiveSourceFileInfo
        {
            public required string PhysicalPath { get; set; }
            public required string EntryName { get; set; }
            public long Size { get; set; }
        }

        private static List<ArchiveSourceFileInfo> CollectSourceFiles(IReadOnlyList<string> paths)
        {
            var result = new List<ArchiveSourceFileInfo>();

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        result.Add(new ArchiveSourceFileInfo
                        {
                            PhysicalPath = path,
                            EntryName = fi.Name,
                            Size = fi.Length
                        });
                    }
                    catch { }
                }
                else if (Directory.Exists(path))
                {
                    try
                    {
                        var rootDi = new DirectoryInfo(path);
                        string rootName = rootDi.Name;
                        CollectDirectoryFiles(rootDi, rootName, result);
                    }
                    catch { }
                }
            }

            return result;
        }

        private static void CollectDirectoryFiles(DirectoryInfo dir, string entryPrefix, List<ArchiveSourceFileInfo> list)
        {
            try
            {
                foreach (var file in dir.GetFiles())
                {
                    try
                    {
                        list.Add(new ArchiveSourceFileInfo
                        {
                            PhysicalPath = file.FullName,
                            EntryName = Path.Combine(entryPrefix, file.Name),
                            Size = file.Length
                        });
                    }
                    catch { }
                }

                foreach (var subDir in dir.GetDirectories())
                {
                    CollectDirectoryFiles(subDir, Path.Combine(entryPrefix, subDir.Name), list);
                }
            }
            catch { }
        }

        #region ZIP Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで ZIP ファイルに圧縮
        /// </summary>
        public static async Task<string?> CompressToZipAsync(
            IEnumerable<string> paths,
            string destinationDirectory,
            ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0 || !Directory.Exists(destinationDirectory)) return null;

            string baseName = pathList.Count == 1
                ? (Directory.Exists(pathList[0]) ? Path.GetFileName(pathList[0].TrimEnd('\\', '/')) : Path.GetFileNameWithoutExtension(pathList[0]))
                : "アーカイブ";

            if (string.IsNullOrWhiteSpace(baseName)) baseName = "アーカイブ";
            string zipPath = FileOperationService.GetUniqueDestinationPath(destinationDirectory, baseName + ".zip");

            var netCompressionLevel = level switch
            {
                ArchiveCompressionLevel.Store => CompressionLevel.NoCompression,
                ArchiveCompressionLevel.Fast => CompressionLevel.Fastest,
                ArchiveCompressionLevel.Normal => CompressionLevel.Optimal,
                ArchiveCompressionLevel.Ultra => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal
            };

            bool success = await Task.Run(() =>
            {
                var sourceFiles = CollectSourceFiles(pathList);
                long totalBytes = sourceFiles.Sum(f => f.Size);
                int totalFiles = sourceFiles.Count;

                var progressState = new FileTransferProgress
                {
                    CustomTitle = "ZIPに圧縮中",
                    TotalBytes = totalBytes,
                    TotalFiles = Math.Max(1, totalFiles),
                    DestinationDirectory = destinationDirectory,
                    IsSizeCalculating = false
                };

                long bytesTransferred = 0;
                int filesTransferred = 0;
                var sw = Stopwatch.StartNew();
                long lastReportTick = 0;
                byte[] buffer = new byte[BufferSize];

                try
                {
                    using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false, System.Text.Encoding.UTF8))
                    {
                        foreach (var file in sourceFiles)
                        {
                            controller?.WaitIfPaused();
                            if (controller?.IsCancelled == true)
                            {
                                return false;
                            }

                            progressState.CurrentFileName = Path.GetFileName(file.PhysicalPath);
                            progressState.SourceDirectory = Path.GetDirectoryName(file.PhysicalPath) ?? "";
                            progressState.FilesTransferred = filesTransferred;
                            progressState.BytesTransferred = bytesTransferred;

                            double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                            progressState.BytesPerSecond = bytesTransferred / elapsedSec;
                            if (progressState.BytesPerSecond > 0 && totalBytes > bytesTransferred)
                            {
                                progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / progressState.BytesPerSecond);
                            }
                            progress?.Report(progressState);

                            var entry = archive.CreateEntry(file.EntryName, netCompressionLevel);
                            using (var entryStream = entry.Open())
                            using (var sourceStream = new FileStream(file.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                int read;
                                while ((read = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    controller?.WaitIfPaused();
                                    if (controller?.IsCancelled == true)
                                    {
                                        return false;
                                    }

                                    entryStream.Write(buffer, 0, read);
                                    bytesTransferred += read;

                                    if (sw.ElapsedMilliseconds - lastReportTick >= 100)
                                    {
                                        lastReportTick = sw.ElapsedMilliseconds;
                                        progressState.BytesTransferred = bytesTransferred;
                                        elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                                        progressState.BytesPerSecond = bytesTransferred / elapsedSec;
                                        if (progressState.BytesPerSecond > 0 && totalBytes > bytesTransferred)
                                        {
                                            progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / progressState.BytesPerSecond);
                                        }
                                        progress?.Report(progressState);
                                    }
                                }
                            }

                            filesTransferred++;
                        }
                    }

                    // 100% 完了報告
                    progressState.BytesTransferred = totalBytes;
                    progressState.FilesTransferred = totalFiles;
                    progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                    progress?.Report(progressState);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CompressToZipAsync] Error: {ex.Message}");
                    return false;
                }
            });

            if (!success)
            {
                try
                {
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }
                }
                catch { }
                return null;
            }

            return zipPath;
        }

        #endregion

        #region 7-Zip Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで 7-Zip (.7z) ファイルに圧縮
        /// </summary>
        public static async Task<string?> CompressTo7zAsync(
            IEnumerable<string> paths,
            string destinationDirectory,
            ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0 || !Directory.Exists(destinationDirectory)) return null;

            string baseName = pathList.Count == 1
                ? (Directory.Exists(pathList[0]) ? Path.GetFileName(pathList[0].TrimEnd('\\', '/')) : Path.GetFileNameWithoutExtension(pathList[0]))
                : "アーカイブ";

            if (string.IsNullOrWhiteSpace(baseName)) baseName = "アーカイブ";
            string sevenZipPath = FileOperationService.GetUniqueDestinationPath(destinationDirectory, baseName + ".7z");

            int compLevel = level switch
            {
                ArchiveCompressionLevel.Store => 0,
                ArchiveCompressionLevel.Fast => 1,
                ArchiveCompressionLevel.Normal => 5,
                ArchiveCompressionLevel.Ultra => 9,
                _ => 5
            };

            bool success = await Task.Run(() =>
            {
                var sourceFiles = CollectSourceFiles(pathList);
                long totalBytes = sourceFiles.Sum(f => f.Size);
                int totalFiles = sourceFiles.Count;

                var progressState = new FileTransferProgress
                {
                    CustomTitle = "7-Zipに圧縮中",
                    TotalBytes = totalBytes,
                    TotalFiles = Math.Max(1, totalFiles),
                    DestinationDirectory = destinationDirectory,
                    IsSizeCalculating = false
                };

                long bytesTransferred = 0;
                int filesTransferred = 0;
                var sw = Stopwatch.StartNew();

                try
                {
                    var writerOptions = new SevenZipWriterOptions(CompressionType.LZMA2)
                    {
                        CompressionLevel = compLevel
                    };

                    using (var writer = (SevenZipWriter)WriterFactory.OpenWriter(sevenZipPath, ArchiveType.SevenZip, writerOptions))
                    {
                        foreach (var file in sourceFiles)
                        {
                            controller?.WaitIfPaused();
                            if (controller?.IsCancelled == true)
                            {
                                return false;
                            }

                            progressState.CurrentFileName = Path.GetFileName(file.PhysicalPath);
                            progressState.SourceDirectory = Path.GetDirectoryName(file.PhysicalPath) ?? "";
                            progressState.FilesTransferred = filesTransferred;
                            progressState.BytesTransferred = bytesTransferred;

                            double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                            progressState.BytesPerSecond = bytesTransferred / elapsedSec;
                            if (progressState.BytesPerSecond > 0 && totalBytes > bytesTransferred)
                            {
                                progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / progressState.BytesPerSecond);
                            }
                            progress?.Report(progressState);

                            writer.Write(file.EntryName, file.PhysicalPath);

                            bytesTransferred += file.Size;
                            filesTransferred++;
                        }
                    }

                    // 100% 完了報告
                    progressState.BytesTransferred = totalBytes;
                    progressState.FilesTransferred = totalFiles;
                    progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                    progress?.Report(progressState);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CompressTo7zAsync] Error: {ex.Message}");
                    return false;
                }
            });

            if (!success)
            {
                try
                {
                    if (File.Exists(sevenZipPath))
                    {
                        File.Delete(sevenZipPath);
                    }
                }
                catch { }
                return null;
            }

            return sevenZipPath;
        }

        #endregion

        #region Extraction (ZIP, 7-Zip, RAR, TAR, GZ, etc.)

        /// <summary>
        /// アーカイブファイルを指定されたディレクトリに解凍・展開
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
                string folderName = Path.GetFileNameWithoutExtension(archivePath);
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

                    var readerOptions = new ReaderOptions
                    {
                        ArchiveEncoding = new ArchiveEncoding { Default = System.Text.Encoding.UTF8 }
                    };

                    using (var archive = ArchiveFactory.OpenArchive(archivePath, readerOptions))
                    {
                        var entries = archive.Entries.ToList();

                        long totalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
                        int totalFiles = entries.Count(e => !e.IsDirectory);

                        var progressState = new FileTransferProgress
                        {
                            CustomTitle = "アーカイブを展開中",
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

                        foreach (var entry in entries)
                        {
                            controller?.WaitIfPaused();
                            if (controller?.IsCancelled == true)
                            {
                                return false;
                            }

                            if (string.IsNullOrEmpty(entry.Key)) continue;

                            string relativePath = entry.Key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                            string destinationPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));
                            string fullTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (!destinationPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (entry.IsDirectory)
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

                            progressState.CurrentFileName = Path.GetFileName(destinationPath);
                            progressState.FilesTransferred = filesTransferred;
                            progressState.BytesTransferred = bytesTransferred;

                            double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                            progressState.BytesPerSecond = bytesTransferred / elapsedSec;
                            if (progressState.BytesPerSecond > 0 && totalBytes > bytesTransferred)
                            {
                                progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / progressState.BytesPerSecond);
                            }
                            progress?.Report(progressState);

                            bool fileExisted = File.Exists(destinationPath);
                            if (!fileExisted)
                            {
                                createdFiles.Add(destinationPath);
                            }

                            using (var entryStream = entry.OpenEntryStream())
                            using (var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                int read;
                                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    controller?.WaitIfPaused();
                                    if (controller?.IsCancelled == true)
                                    {
                                        return false;
                                    }

                                    outStream.Write(buffer, 0, read);
                                    bytesTransferred += read;

                                    if (sw.ElapsedMilliseconds - lastReportTick >= 100)
                                    {
                                        lastReportTick = sw.ElapsedMilliseconds;
                                        progressState.BytesTransferred = bytesTransferred;
                                        elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                                        progressState.BytesPerSecond = bytesTransferred / elapsedSec;
                                        if (progressState.BytesPerSecond > 0 && totalBytes > bytesTransferred)
                                        {
                                            progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / progressState.BytesPerSecond);
                                        }
                                        progress?.Report(progressState);
                                    }
                                }
                            }

                            filesTransferred++;
                        }

                        // 100% 完了報告
                        progressState.BytesTransferred = totalBytes;
                        progressState.FilesTransferred = totalFiles;
                        progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                        progress?.Report(progressState);

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ArchiveService.ExtractAsync] SharpCompress error: {ex.Message}");

                    if (controller?.IsCancelled == true)
                    {
                        return false;
                    }

                    // SharpCompress で失敗した場合、標準 ZIP であれば ZipFile によるフォールバックを試みる
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
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

                            foreach (var entry in zip.Entries)
                            {
                                controller?.WaitIfPaused();
                                if (controller?.IsCancelled == true)
                                {
                                    return false;
                                }

                                string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                                string destinationPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));
                                string fullTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                                if (!destinationPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
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

                                bool fileExisted = File.Exists(destinationPath);
                                if (!fileExisted)
                                {
                                    createdFiles.Add(destinationPath);
                                }

                                using (var entryStream = entry.Open())
                                using (var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    int read;
                                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        controller?.WaitIfPaused();
                                        if (controller?.IsCancelled == true)
                                        {
                                            return false;
                                        }

                                        outStream.Write(buffer, 0, read);
                                        bytesTransferred += read;

                                        if (sw.ElapsedMilliseconds - lastReportTick >= 100)
                                        {
                                            lastReportTick = sw.ElapsedMilliseconds;
                                            progressState.BytesTransferred = bytesTransferred;
                                            double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                                            progressState.BytesPerSecond = bytesTransferred / elapsedSec;
                                            if (progressState.BytesPerSecond > 0 && totalBytes > bytesTransferred)
                                            {
                                                progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesTransferred) / progressState.BytesPerSecond);
                                            }
                                            progress?.Report(progressState);
                                        }
                                    }
                                }

                                filesTransferred++;
                            }

                            progressState.BytesTransferred = totalBytes;
                            progressState.FilesTransferred = totalFiles;
                            progress?.Report(progressState);

                            return true;
                        }
                        catch (Exception fallbackEx)
                        {
                            Debug.WriteLine($"[ArchiveService.ExtractAsync] Fallback ZipFile error: {fallbackEx.Message}");
                        }
                    }

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
