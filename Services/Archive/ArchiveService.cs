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
        private static readonly HashSet<string> SupportedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst"
        };

        private const int BufferSize = 128 * 1024; // 128 KB buffer for managed fallback

        /// <summary>
        /// 指定されたパスの拡張子が対応アーカイブ形式かどうかを判定
        /// </summary>
        public static bool IsSupportedArchive(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            return SupportedArchiveExtensions.Contains(ext);
        }

        /// <summary>
        /// アーカイブファイルのパスから、拡張子（二重拡張子 .tar.gz 等も考慮）を除いたベース名を取得
        /// </summary>
        public static string GetArchiveBaseName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "アーカイブ";
            string fileName = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(fileName)) return "アーカイブ";

            string[] doubleExts =
            [
                ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz", ".tar.lzma",
                ".tgz", ".tbz2", ".tbz", ".txz", ".tzst"
            ];

            foreach (var dext in doubleExts)
            {
                if (fileName.EndsWith(dext, StringComparison.OrdinalIgnoreCase))
                {
                    string basePart = fileName[..^dext.Length];
                    return string.IsNullOrWhiteSpace(basePart) ? "アーカイブ" : basePart;
                }
            }

            string singleExtBase = Path.GetFileNameWithoutExtension(fileName);
            return string.IsNullOrWhiteSpace(singleExtBase) ? "アーカイブ" : singleExtBase;
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
                        CollectDirectoryFiles(rootDi, rootDi.Name, result);
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

        private static string GetOutputBaseName(List<string> pathList)
        {
            string baseName = pathList.Count == 1
                ? (Directory.Exists(pathList[0])
                    ? Path.GetFileName(pathList[0].TrimEnd('\\', '/'))
                    : Path.GetFileNameWithoutExtension(pathList[0]))
                : "アーカイブ";
            return string.IsNullOrWhiteSpace(baseName) ? "アーカイブ" : baseName;
        }

        #region ZIP Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで ZIP ファイルに圧縮 (Rust ネイティブ優先)
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

            string baseName = GetOutputBaseName(pathList);
            string zipPath = FileOperationService.GetUniqueDestinationPath(destinationDirectory, baseName + ".zip");

            bool success = await Task.Run(() =>
            {
                if (NativeArchiveInterop.IsAvailable)
                {
                    int code = NativeArchiveInterop.CompressZip(pathList, zipPath, level, destinationDirectory, progress, controller, out string? err);
                    if (code == NativeArchiveInterop.FA_OK)
                    {
                        return true;
                    }
                    Debug.WriteLine($"[CompressToZipAsync] Native Rust failed ({code}): {err}");
                    if (code == NativeArchiveInterop.FA_ERR_CANCELLED || controller?.IsCancelled == true)
                    {
                        return false;
                    }
                }

                // マネージド .NET ZipArchive フォールバック
                return CompressToZipManaged(pathList, zipPath, destinationDirectory, level, progress, controller);
            });

            if (!success)
            {
                TryDeleteFile(zipPath);
                return null;
            }

            return zipPath;
        }

        private static bool CompressToZipManaged(
            List<string> pathList,
            string zipPath,
            string destinationDirectory,
            ArchiveCompressionLevel level,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller)
        {
            var netCompressionLevel = level switch
            {
                ArchiveCompressionLevel.Store => CompressionLevel.NoCompression,
                ArchiveCompressionLevel.Fast => CompressionLevel.Fastest,
                ArchiveCompressionLevel.Normal => CompressionLevel.Optimal,
                ArchiveCompressionLevel.Ultra => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal
            };

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
                        if (controller?.IsCancelled == true) return false;

                        progressState.CurrentFileName = Path.GetFileName(file.PhysicalPath);
                        progressState.SourceDirectory = Path.GetDirectoryName(file.PhysicalPath) ?? "";
                        progressState.FilesTransferred = filesTransferred;
                        progressState.BytesTransferred = bytesTransferred;
                        UpdateRateAndEta(progressState, bytesTransferred, totalBytes, sw);
                        progress?.Report(progressState);

                        var entry = archive.CreateEntry(file.EntryName.Replace('\\', '/'), netCompressionLevel);
                        using (var entryStream = entry.Open())
                        using (var sourceStream = new FileStream(file.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            int read;
                            while ((read = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                controller?.WaitIfPaused();
                                if (controller?.IsCancelled == true) return false;

                                entryStream.Write(buffer, 0, read);
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
                }

                progressState.BytesTransferred = totalBytes;
                progressState.FilesTransferred = totalFiles;
                progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                progress?.Report(progressState);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CompressToZipManaged] Error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 7-Zip Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで 7-Zip (.7z) ファイルに圧縮 (Rust ネイティブ LZMA2)
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

            string baseName = GetOutputBaseName(pathList);
            string sevenZipPath = FileOperationService.GetUniqueDestinationPath(destinationDirectory, baseName + ".7z");

            bool success = await Task.Run(() =>
            {
                if (!NativeArchiveInterop.IsAvailable)
                {
                    Debug.WriteLine("[CompressTo7zAsync] fast_archive.dll is unavailable.");
                    return false;
                }

                int code = NativeArchiveInterop.Compress7z(pathList, sevenZipPath, level, destinationDirectory, progress, controller, out string? err);
                if (code != NativeArchiveInterop.FA_OK)
                {
                    Debug.WriteLine($"[CompressTo7zAsync] Native Rust failed ({code}): {err}");
                    return false;
                }
                return true;
            });

            if (!success)
            {
                TryDeleteFile(sevenZipPath);
                return null;
            }

            return sevenZipPath;
        }

        #endregion

        #region TAR & TAR.GZ Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を TAR (.tar) ファイルに圧縮 (Rust ネイティブ優先)
        /// </summary>
        public static async Task<string?> CompressToTarAsync(
            IEnumerable<string> paths,
            string destinationDirectory,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0 || !Directory.Exists(destinationDirectory)) return null;

            string baseName = GetOutputBaseName(pathList);
            string tarPath = FileOperationService.GetUniqueDestinationPath(destinationDirectory, baseName + ".tar");

            bool success = await Task.Run(() =>
            {
                if (NativeArchiveInterop.IsAvailable)
                {
                    int code = NativeArchiveInterop.CompressTar(pathList, tarPath, gzipLevel: -1, "TARに圧縮中", destinationDirectory, progress, controller, out string? err);
                    if (code == NativeArchiveInterop.FA_OK) return true;
                    Debug.WriteLine($"[CompressToTarAsync] Native Rust failed ({code}): {err}");
                    if (code == NativeArchiveInterop.FA_ERR_CANCELLED || controller?.IsCancelled == true) return false;
                }

                return CompressToTarManaged(pathList, tarPath, destinationDirectory, gzipLevel: null, progress, controller);
            });

            if (!success)
            {
                TryDeleteFile(tarPath);
                return null;
            }

            return tarPath;
        }

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで TAR.GZ (.tar.gz) ファイルに圧縮 (Rust ネイティブ優先)
        /// </summary>
        public static async Task<string?> CompressToTarGzAsync(
            IEnumerable<string> paths,
            string destinationDirectory,
            ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0 || !Directory.Exists(destinationDirectory)) return null;

            string baseName = GetOutputBaseName(pathList);
            string tarGzPath = FileOperationService.GetUniqueDestinationPath(destinationDirectory, baseName + ".tar.gz");

            bool success = await Task.Run(() =>
            {
                if (NativeArchiveInterop.IsAvailable)
                {
                    int code = NativeArchiveInterop.CompressTar(pathList, tarGzPath, gzipLevel: (int)level, "TAR.GZに圧縮中", destinationDirectory, progress, controller, out string? err);
                    if (code == NativeArchiveInterop.FA_OK) return true;
                    Debug.WriteLine($"[CompressToTarGzAsync] Native Rust failed ({code}): {err}");
                    if (code == NativeArchiveInterop.FA_ERR_CANCELLED || controller?.IsCancelled == true) return false;
                }

                return CompressToTarManaged(pathList, tarGzPath, destinationDirectory, gzipLevel: level, progress, controller);
            });

            if (!success)
            {
                TryDeleteFile(tarGzPath);
                return null;
            }

            return tarGzPath;
        }

        private static bool CompressToTarManaged(
            List<string> pathList,
            string outputPath,
            string destinationDirectory,
            ArchiveCompressionLevel? gzipLevel,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller)
        {
            var sourceFiles = CollectSourceFiles(pathList);
            long totalBytes = sourceFiles.Sum(f => f.Size);
            int totalFiles = sourceFiles.Count;

            var progressState = new FileTransferProgress
            {
                CustomTitle = gzipLevel.HasValue ? "TAR.GZに圧縮中" : "TARに圧縮中",
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
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Stream targetStream = fileStream;
                GZipStream? gzStream = null;
                if (gzipLevel.HasValue)
                {
                    var netLevel = gzipLevel.Value switch
                    {
                        ArchiveCompressionLevel.Store => CompressionLevel.NoCompression,
                        ArchiveCompressionLevel.Fast => CompressionLevel.Fastest,
                        ArchiveCompressionLevel.Normal => CompressionLevel.Optimal,
                        ArchiveCompressionLevel.Ultra => CompressionLevel.SmallestSize,
                        _ => CompressionLevel.Optimal
                    };
                    gzStream = new GZipStream(fileStream, netLevel, leaveOpen: false);
                    targetStream = gzStream;
                }

                try
                {
                    using var writer = new System.Formats.Tar.TarWriter(targetStream, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: false);
                    foreach (var file in sourceFiles)
                    {
                        controller?.WaitIfPaused();
                        if (controller?.IsCancelled == true) return false;

                        progressState.CurrentFileName = Path.GetFileName(file.PhysicalPath);
                        progressState.SourceDirectory = Path.GetDirectoryName(file.PhysicalPath) ?? "";
                        progressState.FilesTransferred = filesTransferred;
                        progressState.BytesTransferred = bytesTransferred;
                        UpdateRateAndEta(progressState, bytesTransferred, totalBytes, sw);
                        progress?.Report(progressState);

                        writer.WriteEntry(file.PhysicalPath, file.EntryName.Replace('\\', '/'));

                        bytesTransferred += file.Size;
                        filesTransferred++;
                    }
                }
                finally
                {
                    gzStream?.Dispose();
                }

                progressState.BytesTransferred = totalBytes;
                progressState.FilesTransferred = totalFiles;
                progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                progress?.Report(progressState);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CompressToTarManaged] Error: {ex.Message}");
                return false;
            }
        }

        #endregion

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
