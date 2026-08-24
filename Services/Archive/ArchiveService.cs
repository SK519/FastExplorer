using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        /// <summary>
        /// 指定されたパスの拡張子が対応アーカイブ形式かどうかを判定
        /// </summary>
        public static bool IsSupportedArchive(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            return SupportedArchiveExtensions.Contains(ext);
        }

        #region ZIP Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで ZIP ファイルに圧縮
        /// </summary>
        public static async Task<string?> CompressToZipAsync(
            IEnumerable<string> paths,
            string destinationDirectory,
            ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0 || !Directory.Exists(destinationDirectory)) return null;

            string baseName = pathList.Count == 1
                ? Path.GetFileNameWithoutExtension(pathList[0])
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

            await Task.Run(() =>
            {
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false, System.Text.Encoding.UTF8);

                foreach (var path in pathList)
                {
                    if (File.Exists(path))
                    {
                        archive.CreateEntryFromFile(path, Path.GetFileName(path), netCompressionLevel);
                    }
                    else if (Directory.Exists(path))
                    {
                        string rootDirName = new DirectoryInfo(path).Name;
                        AddDirectoryToZip(archive, path, rootDirName, netCompressionLevel);
                    }
                }
            });

            return zipPath;
        }

        private static void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix, CompressionLevel level)
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string entryName = Path.Combine(entryPrefix, Path.GetFileName(file));
                archive.CreateEntryFromFile(file, entryName, level);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = new DirectoryInfo(dir).Name;
                string newPrefix = Path.Combine(entryPrefix, dirName);
                AddDirectoryToZip(archive, dir, newPrefix, level);
            }
        }

        #endregion

        #region 7-Zip Compression

        /// <summary>
        /// 指定されたファイル・フォルダー群を指定された圧縮レベルで 7-Zip (.7z) ファイルに圧縮
        /// </summary>
        public static async Task<string?> CompressTo7zAsync(
            IEnumerable<string> paths,
            string destinationDirectory,
            ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0 || !Directory.Exists(destinationDirectory)) return null;

            string baseName = pathList.Count == 1
                ? Path.GetFileNameWithoutExtension(pathList[0])
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

            await Task.Run(() =>
            {
                var writerOptions = new SevenZipWriterOptions(CompressionType.LZMA2)
                {
                    CompressionLevel = compLevel
                };

                using var writer = (SevenZipWriter)WriterFactory.OpenWriter(sevenZipPath, ArchiveType.SevenZip, writerOptions);

                foreach (var path in pathList)
                {
                    if (File.Exists(path))
                    {
                        writer.Write(Path.GetFileName(path), path);
                    }
                    else if (Directory.Exists(path))
                    {
                        string rootDirName = new DirectoryInfo(path).Name;
                        AddDirectoryTo7z(writer, path, rootDirName);
                    }
                }
            });

            return sevenZipPath;
        }

        private static void AddDirectoryTo7z(SevenZipWriter writer, string sourceDir, string entryPrefix)
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string entryName = Path.Combine(entryPrefix, Path.GetFileName(file));
                writer.Write(entryName, file);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = new DirectoryInfo(dir).Name;
                string newPrefix = Path.Combine(entryPrefix, dirName);
                AddDirectoryTo7z(writer, dir, newPrefix);
            }
        }

        #endregion

        #region Extraction (ZIP, 7-Zip, RAR, TAR, GZ, etc.)

        /// <summary>
        /// アーカイブファイルを指定されたディレクトリに解凍・展開
        /// </summary>
        /// <param name="archivePath">アーカイブファイルへのパス</param>
        /// <param name="destinationDirectory">展開先親ディレクトリ</param>
        /// <param name="createSubFolder">ファイル名と同名のサブフォルダーを作成してその中に展開するかどうか</param>
        public static async Task<bool> ExtractAsync(string archivePath, string destinationDirectory, bool createSubFolder = true)
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

            return await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(targetDir);

                    ArchiveFactory.WriteToDirectory(archivePath, targetDir, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });

                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ArchiveService.ExtractAsync] SharpCompress error: {ex.Message}");

                    // SharpCompress で失敗した場合、標準 ZIP であれば ZipFile によるフォールバックを試みる
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
                            return true;
                        }
                        catch (Exception fallbackEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ArchiveService.ExtractAsync] Fallback ZipFile error: {fallbackEx.Message}");
                        }
                    }

                    return false;
                }
            });
        }

        #endregion
    }
}

