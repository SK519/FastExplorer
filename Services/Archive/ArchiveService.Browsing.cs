using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    public static partial class ArchiveService
    {
        #region Archive Preview

        /// <summary>
        /// アーカイブファイル内のエントリ一覧のプレビューテキストを非同期で生成 (Rust ネイティブ優先)
        /// </summary>
        public static async Task<string?> GetArchivePreviewTextAsync(string archivePath, int maxEntries = 100)
        {
            if (!File.Exists(archivePath)) return null;

            return await Task.Run(() =>
            {
                try
                {
                    var nativeEntries = NativeArchiveInterop.ListEntries(archivePath);
                    if (nativeEntries != null)
                    {
                        var sb = new StringBuilder();
                        int totalEntries = nativeEntries.Count;
                        int fileCount = nativeEntries.Count(e => !e.IsDirectory);
                        int dirCount = nativeEntries.Count(e => e.IsDirectory);
                        long totalSize = nativeEntries.Where(e => !e.IsDirectory).Sum(e => e.Size);

                        sb.AppendLine($"📦 アーカイブ内容 (全 {fileCount} ファイル, {dirCount} フォルダー / {FileItem.FormatFileSize(totalSize)}):");
                        sb.AppendLine(new string('-', 50));

                        int count = 0;
                        foreach (var entry in nativeEntries)
                        {
                            if (count >= maxEntries)
                            {
                                sb.AppendLine($"... 他 {totalEntries - maxEntries} 項目省略");
                                break;
                            }

                            if (entry.IsDirectory)
                            {
                                sb.AppendLine($"📁 {entry.Key.TrimEnd('/', '\\')}/");
                            }
                            else
                            {
                                string sizeStr = FileItem.FormatFileSize(entry.Size);
                                sb.AppendLine($"📄 {entry.Key} ({sizeStr})");
                            }
                            count++;
                        }

                        return sb.ToString();
                    }

                    // ZIP フォールバック
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = ZipFile.OpenRead(archivePath);
                        var sb = new StringBuilder();
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

                    return null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ArchiveService.GetArchivePreviewTextAsync] error: {ex.Message}");
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
        /// アーカイブ内の指定されたサブパス直下のファイル・フォルダー一覧を取得 (Rust ネイティブ + キャッシュ)
        /// </summary>
        public static List<FileItem> GetArchiveFolderItems(string archiveFile, string internalSubPath)
        {
            var results = new List<FileItem>();
            if (!File.Exists(archiveFile)) return results;

            string normalizedPrefix = string.IsNullOrEmpty(internalSubPath) ? "" : internalSubPath.Replace('\\', '/').Trim('/') + "/";

            var nativeEntries = NativeArchiveInterop.ListEntries(archiveFile);
            if (nativeEntries != null)
            {
                var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in nativeEntries)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    string key = entry.Key;

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
                                    DateModified = entry.LastModifiedTime ?? DateTime.MinValue
                                });
                            }
                        }
                        else if (entry.IsDirectory)
                        {
                            if (folderSet.Add(key))
                            {
                                results.Add(new FileItem
                                {
                                    Name = key,
                                    FullPath = Path.Combine(archiveFile, key),
                                    IsDirectory = true,
                                    FileType = "フォルダー",
                                    GlyphIcon = "\uE8B7",
                                    DateModified = entry.LastModifiedTime ?? DateTime.MinValue
                                });
                            }
                        }
                        else
                        {
                            results.Add(CreateArchiveFileItem(archiveFile, key, entry.Size, entry.LastModifiedTime));
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
                        else if (entry.IsDirectory)
                        {
                            if (folderSet.Add(sub))
                            {
                                string relativeFolderPath = normalizedPrefix.Replace('/', '\\') + sub;
                                results.Add(new FileItem
                                {
                                    Name = sub,
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
                            string relativeFilePath = normalizedPrefix.Replace('/', '\\') + sub;
                            results.Add(CreateArchiveFileItem(archiveFile, relativeFilePath, entry.Size, entry.LastModifiedTime));
                        }
                    }
                }

                return results;
            }

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
                        string[] segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Any(s => s == ".." || s == "." || s.Contains(':'))) continue;

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
        /// アーカイブ内の特定エントリを一時フォルダーに展開 (Rust ネイティブ優先)
        /// </summary>
        public static async Task<string?> ExtractEntryToTempAsync(string archiveFile, string entryKey)
        {
            if (!File.Exists(archiveFile) || string.IsNullOrWhiteSpace(entryKey)) return null;

            return await Task.Run(() =>
            {
                try
                {
                    string normalizedKey = entryKey.Replace('\\', '/').TrimStart('/');
                    string[] segments = normalizedKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Any(s => s == ".." || s == "." || s.Contains(':')))
                    {
                        return null;
                    }

                    string fileName = Path.GetFileName(normalizedKey);
                    if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        return null;
                    }

                    string tempDir = Path.Combine(Path.GetTempPath(), "FastExplorer", "ArchiveExtract", Path.GetFileNameWithoutExtension(archiveFile));
                    Directory.CreateDirectory(tempDir);
                    string destPath = Path.Combine(tempDir, fileName);

                    const long MaxTempExtractionBytes = 250 * 1024 * 1024; // 250MB 安全上限

                    if (NativeArchiveInterop.IsAvailable)
                    {
                        int code = NativeArchiveInterop.ExtractEntry(archiveFile, normalizedKey, destPath, (ulong)MaxTempExtractionBytes, out string? err);
                        if (code == NativeArchiveInterop.FA_OK)
                        {
                            return destPath;
                        }
                        System.Diagnostics.Debug.WriteLine($"[ArchiveService.ExtractEntryToTempAsync] Native Rust failed ({code}): {err}");
                        if (code == NativeArchiveInterop.FA_ERR_SIZE_LIMIT)
                        {
                            return null;
                        }
                    }

                    // ZIP フォールバック
                    if (archiveFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = ZipFile.OpenRead(archiveFile);
                        var zipEntry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').TrimStart('/').Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) == true);
                        if (zipEntry != null)
                        {
                            if (zipEntry.Length > MaxTempExtractionBytes)
                            {
                                throw new InvalidOperationException("展開サイズが安全上限(250MB)を超過しています。");
                            }

                            using var entryStream = zipEntry.Open();
                            using var outStream = File.Create(destPath);
                            byte[] buffer = new byte[8192];
                            long totalRead = 0;
                            int read;
                            while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                totalRead += read;
                                if (totalRead > MaxTempExtractionBytes)
                                {
                                    throw new InvalidOperationException("展開サイズが安全上限(250MB)を超過しました。");
                                }
                                outStream.Write(buffer, 0, read);
                            }
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
