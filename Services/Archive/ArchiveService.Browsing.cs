using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using FastExplorer.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace FastExplorer.Services
{
    public static partial class ArchiveService
    {
        #region Archive Preview

        /// <summary>
        /// アーカイブファイル内のエントリ一覧のプレビューテキストを非同期で生成
        /// </summary>
        public static async Task<string?> GetArchivePreviewTextAsync(string archivePath, int maxEntries = 100)
        {
            if (!File.Exists(archivePath)) return null;

            return await Task.Run(() =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    var readerOptions = new ReaderOptions
                    {
                        ArchiveEncoding = new ArchiveEncoding { Default = System.Text.Encoding.UTF8 }
                    };

                    using var archive = ArchiveFactory.OpenArchive(archivePath, readerOptions);
                    var entries = archive.Entries.ToList();

                    int totalEntries = entries.Count;
                    int fileCount = entries.Count(e => !e.IsDirectory);
                    int dirCount = entries.Count(e => e.IsDirectory);
                    long totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

                    sb.AppendLine($"📦 アーカイブ内容 (全 {fileCount} ファイル, {dirCount} フォルダー / {FileItem.FormatFileSize(totalSize)}):");
                    sb.AppendLine(new string('-', 50));

                    int count = 0;
                    foreach (var entry in entries)
                    {
                        if (count >= maxEntries)
                        {
                            sb.AppendLine($"... 他 {totalEntries - maxEntries} 項目省略");
                            break;
                        }

                        string key = entry.Key ?? "";
                        if (entry.IsDirectory)
                        {
                            sb.AppendLine($"📁 {key.TrimEnd('/', '\\')}/");
                        }
                        else
                        {
                            string sizeStr = FileItem.FormatFileSize(entry.Size);
                            sb.AppendLine($"📄 {key} ({sizeStr})");
                        }
                        count++;
                    }

                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ArchiveService.GetArchivePreviewTextAsync] SharpCompress error: {ex.Message}");

                    // ZIP フォールバック
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var zip = ZipFile.OpenRead(archivePath);
                            var sb = new System.Text.StringBuilder();
                            int total = zip.Entries.Count;
                            long totalSize = zip.Entries.Sum(e => e.Length);
                            sb.AppendLine($"📦 ZIP アーカイブ内容 (全 {total} 項目 / {FileItem.FormatFileSize(totalSize)}):");
                            sb.AppendLine(new string('-', 50));

                            int count = 0;
                            foreach (var entry in zip.Entries)
                            {
                                if (count >= maxEntries)
                                {
                                    sb.AppendLine($"... 他 {total - maxEntries} 項目省略");
                                    break;
                                }

                                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                                {
                                    sb.AppendLine($"📁 {entry.FullName.TrimEnd('/', '\\')}/");
                                }
                                else
                                {
                                    string sizeStr = FileItem.FormatFileSize(entry.Length);
                                    sb.AppendLine($"📄 {entry.FullName} ({sizeStr})");
                                }
                                count++;
                            }
                            return sb.ToString();
                        }
                        catch { }
                    }

                    return null;
                }
            });
        }

        #endregion

        #region Virtual Archive Browsing (Folder-like Preview)

        /// <summary>
        /// 指定されたパスがアーカイブファイル自体、またはアーカイブ内部の仮想パスかを判定
        /// </summary>
        public static bool IsArchiveOrSubPath(string? path, out string archivePath, out string internalSubPath)
        {
            archivePath = string.Empty;
            internalSubPath = string.Empty;

            if (string.IsNullOrWhiteSpace(path)) return false;

            // 1. パス自体が既存のアーカイブファイルである場合
            if (File.Exists(path) && IsSupportedArchive(path))
            {
                archivePath = path;
                internalSubPath = string.Empty;
                return true;
            }

            // 2. パスの祖先にアーカイブファイルが存在するか確認 (例: C:\path\file.zip\folder\sub)
            string current = path;
            var subParts = new List<string>();

            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) && IsSupportedArchive(current))
                {
                    archivePath = current;
                    subParts.Reverse();
                    internalSubPath = string.Join("/", subParts).Replace('\\', '/').Trim('/');
                    return true;
                }

                string? parent = Path.GetDirectoryName(current);
                string part = Path.GetFileName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                if (!string.IsNullOrEmpty(part)) subParts.Add(part);
                current = parent;
            }

            return false;
        }

        /// <summary>
        /// アーカイブ内の指定されたサブパス直下のファイル・フォルダー一覧を取得
        /// </summary>
        public static List<FileItem> GetArchiveFolderItems(string archiveFile, string internalSubPath)
        {
            var results = new List<FileItem>();
            if (!File.Exists(archiveFile)) return results;

            string normalizedPrefix = string.IsNullOrEmpty(internalSubPath) ? "" : internalSubPath.Replace('\\', '/').Trim('/') + "/";

            try
            {
                var readerOptions = new ReaderOptions
                {
                    ArchiveEncoding = new ArchiveEncoding { Default = System.Text.Encoding.UTF8 }
                };

                var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using var archive = ArchiveFactory.OpenArchive(archiveFile, readerOptions);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    string key = entry.Key.Replace('\\', '/').TrimStart('/');

                    if (string.IsNullOrEmpty(normalizedPrefix))
                    {
                        // ルート階層
                        int slashIndex = key.IndexOf('/');
                        if (slashIndex >= 0)
                        {
                            string topFolder = key[..slashIndex];
                            if (folderSet.Add(topFolder))
                            {
                                results.Add(new FileItem
                                {
                                    Name = topFolder,
                                    FullPath = Path.Combine(archiveFile, topFolder),
                                    IsDirectory = true,
                                    FileType = "フォルダー",
                                    GlyphIcon = "\uE8B7",
                                    DateModified = entry.LastModifiedTime ?? DateTime.MinValue
                                });
                            }
                        }
                        else
                        {
                            if (!entry.IsDirectory)
                            {
                                results.Add(CreateArchiveFileItem(archiveFile, key, entry.Size, entry.LastModifiedTime));
                            }
                        }
                    }
                    else if (key.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = key[normalizedPrefix.Length..];
                        if (string.IsNullOrEmpty(sub)) continue;

                        int slashIndex = sub.IndexOf('/');
                        if (slashIndex >= 0)
                        {
                            string subFolder = sub[..slashIndex];
                            if (folderSet.Add(subFolder))
                            {
                                string relativeFolderPath = normalizedPrefix.Replace('/', '\\') + subFolder;
                                results.Add(new FileItem
                                {
                                    Name = subFolder,
                                    FullPath = Path.Combine(archiveFile, relativeFolderPath),
                                    IsDirectory = true,
                                    FileType = "フォルダー",
                                    GlyphIcon = "\uE8B7",
                                    DateModified = entry.LastModifiedTime ?? DateTime.MinValue
                                });
                            }
                        }
                        else
                        {
                            if (!entry.IsDirectory)
                            {
                                string relativeFilePath = normalizedPrefix.Replace('/', '\\') + sub;
                                results.Add(CreateArchiveFileItem(archiveFile, relativeFilePath, entry.Size, entry.LastModifiedTime));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ArchiveService.GetArchiveFolderItems] SharpCompress error: {ex.Message}");

                // ZIP フォールバック
                if (archiveFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var zip = ZipFile.OpenRead(archiveFile);
                        var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var entry in zip.Entries)
                        {
                            string key = entry.FullName.Replace('\\', '/').TrimStart('/');
                            if (string.IsNullOrEmpty(normalizedPrefix))
                            {
                                int slashIndex = key.IndexOf('/');
                                if (slashIndex >= 0)
                                {
                                    string topFolder = key[..slashIndex];
                                    if (folderSet.Add(topFolder))
                                    {
                                        results.Add(new FileItem
                                        {
                                            Name = topFolder,
                                            FullPath = Path.Combine(archiveFile, topFolder),
                                            IsDirectory = true,
                                            FileType = "フォルダー",
                                            GlyphIcon = "\uE8B7",
                                            DateModified = entry.LastWriteTime.DateTime
                                        });
                                    }
                                }
                                else if (!key.EndsWith('/'))
                                {
                                    results.Add(CreateArchiveFileItem(archiveFile, key, entry.Length, entry.LastWriteTime.DateTime));
                                }
                            }
                            else if (key.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                string sub = key[normalizedPrefix.Length..];
                                if (string.IsNullOrEmpty(sub)) continue;

                                int slashIndex = sub.IndexOf('/');
                                if (slashIndex >= 0)
                                {
                                    string subFolder = sub[..slashIndex];
                                    if (folderSet.Add(subFolder))
                                    {
                                        string relativeFolderPath = normalizedPrefix.Replace('/', '\\') + subFolder;
                                        results.Add(new FileItem
                                        {
                                            Name = subFolder,
                                            FullPath = Path.Combine(archiveFile, relativeFolderPath),
                                            IsDirectory = true,
                                            FileType = "フォルダー",
                                            GlyphIcon = "\uE8B7",
                                            DateModified = entry.LastWriteTime.DateTime
                                        });
                                    }
                                }
                                else if (!sub.EndsWith('/'))
                                {
                                    string relativeFilePath = normalizedPrefix.Replace('/', '\\') + sub;
                                    results.Add(CreateArchiveFileItem(archiveFile, relativeFilePath, entry.Length, entry.LastWriteTime.DateTime));
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            return results;
        }

        private static FileItem CreateArchiveFileItem(string archiveFile, string relativeKey, long size, DateTime? dateModified)
        {
            string fileName = Path.GetFileName(relativeKey);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();

            string glyph = ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".svg" or ".ico" => "\uEB9F",
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => "\uE714",
                ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" => "\uEC4F",
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "\uF126",
                ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" => "\uE756",
                ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv" or ".ini" => "\uE8A5",
                ".pdf" => "\uEA90",
                ".doc" or ".docx" => "\uE8A5",
                ".xls" or ".xlsx" => "\uE80A",
                ".ppt" or ".pptx" => "\uE8A5",
                _ => "\uE8A5"
            };

            string fileType = string.IsNullOrEmpty(ext) ? "ファイル" : $"{ext.TrimStart('.').ToUpperInvariant()} ファイル";

            return new FileItem
            {
                Name = fileName,
                FullPath = Path.Combine(archiveFile, relativeKey),
                IsDirectory = false,
                SizeInBytes = size,
                FileType = fileType,
                GlyphIcon = glyph,
                DateModified = dateModified ?? DateTime.MinValue
            };
        }

        /// <summary>
        /// アーカイブ内の特定エントリを一時フォルダーに展開
        /// </summary>
        public static async Task<string?> ExtractEntryToTempAsync(string archiveFile, string entryKey)
        {
            if (!File.Exists(archiveFile)) return null;

            return await Task.Run(() =>
            {
                try
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "FastExplorer", "ArchiveExtract", Path.GetFileNameWithoutExtension(archiveFile));
                    Directory.CreateDirectory(tempDir);

                    string normalizedKey = entryKey.Replace('\\', '/').TrimStart('/');
                    string destPath = Path.Combine(tempDir, Path.GetFileName(normalizedKey));

                    var readerOptions = new ReaderOptions
                    {
                        ArchiveEncoding = new ArchiveEncoding { Default = System.Text.Encoding.UTF8 }
                    };

                    using var archive = ArchiveFactory.OpenArchive(archiveFile, readerOptions);
                    var entry = archive.Entries.FirstOrDefault(e => e.Key?.Replace('\\', '/').TrimStart('/').Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) == true);
                    if (entry != null)
                    {
                        using var entryStream = entry.OpenEntryStream();
                        using var outStream = File.Create(destPath);
                        entryStream.CopyTo(outStream);
                        return destPath;
                    }

                    // ZIP フォールバック
                    if (archiveFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = ZipFile.OpenRead(archiveFile);
                        var zipEntry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').TrimStart('/').Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) == true);
                        if (zipEntry != null)
                        {
                            zipEntry.ExtractToFile(destPath, overwrite: true);
                            return destPath;
                        }
                    }

                    return null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ArchiveService.ExtractEntryToTempAsync] error: {ex.Message}");
                    return null;
                }
            });
        }

        #endregion
    }
}
