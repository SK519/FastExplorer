using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    public static class RecycleBinService
    {
        public const string RecycleBinUri = "shell:RecycleBinFolder";
        public const string RecycleBinGuid = "::{645FF040-5081-101B-9F08-00AA002F954E}";

        public static bool IsRecycleBinPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string p = path.Trim().TrimEnd('\\', '/');
            return p.Equals("RecycleBin", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("shell:::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("ごみ箱", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("ゴミ箱", StringComparison.OrdinalIgnoreCase) ||
                   p.Equals("Trash", StringComparison.OrdinalIgnoreCase) ||
                   p.EndsWith("$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
        }

        public static List<FileItem> GetRecycleBinItems()
        {
            var results = new List<FileItem>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var directItems = ScanDirectRecycleBinDrives();
                foreach (var item in directItems)
                {
                    string key = $"{item.OriginalLocation}\\{item.Name}";
                    if (seenKeys.Add(key))
                    {
                        if (!string.IsNullOrEmpty(item.FullPath))
                        {
                            seenKeys.Add(item.FullPath);
                        }
                        results.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RecycleBin] Direct scan exception: {ex.Message}");
            }

            return results;
        }

        private static List<FileItem> ScanDirectRecycleBinDrives()
        {
            var list = new List<FileItem>();
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable));
                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = (FileAttributes)0 // Hidden や System フォルダ・ファイルをスキップしない
                };

                foreach (var drive in drives)
                {
                    string recycleBinPath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                    if (!Directory.Exists(recycleBinPath)) continue;

                    try
                    {
                        var sidDirs = Directory.GetDirectories(recycleBinPath, "*", enumOptions);
                        foreach (var sidDir in sidDirs)
                        {
                            try
                            {
                                var iFiles = Directory.GetFiles(sidDir, "$I*", enumOptions);
                                foreach (var iFile in iFiles)
                                {
                                    try
                                    {
                                        var parsed = ParseIFile(iFile);
                                        if (parsed != null)
                                        {
                                            list.Add(parsed);
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static FileItem? ParseIFile(string iFilePath)
        {
            try
            {
                if (!File.Exists(iFilePath)) return null;

                using var fs = new FileStream(iFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(fs);

                if (fs.Length < 24) return null;

                long version = reader.ReadInt64();
                long originalSize = reader.ReadInt64();
                long deletedTimeFileTime = reader.ReadInt64();
                DateTime deletedDate = DateTime.MinValue;
                try
                {
                    if (deletedTimeFileTime > 0)
                    {
                        deletedDate = DateTime.FromFileTimeUtc(deletedTimeFileTime).ToLocalTime();
                    }
                }
                catch { }

                string originalPath = string.Empty;
                if (version == 1 && fs.Length >= 24 + 520)
                {
                    byte[] pathBytes = reader.ReadBytes(520);
                    originalPath = System.Text.Encoding.Unicode.GetString(pathBytes).TrimEnd('\0');
                }
                else if (version == 2 && fs.Length >= 28)
                {
                    int pathCharCount = reader.ReadInt32();
                    if (pathCharCount > 0 && pathCharCount <= 2048 && fs.Length >= 28 + (pathCharCount * 2))
                    {
                        byte[] pathBytes = reader.ReadBytes(pathCharCount * 2);
                        originalPath = System.Text.Encoding.Unicode.GetString(pathBytes).TrimEnd('\0');
                    }
                }

                if (string.IsNullOrWhiteSpace(originalPath)) return null;

                string fileName = Path.GetFileName(originalPath);
                string originalDir = Path.GetDirectoryName(originalPath) ?? string.Empty;

                // 対応する $R ファイル (実体)
                string dirName = Path.GetDirectoryName(iFilePath) ?? string.Empty;
                string rFileName = "$R" + Path.GetFileName(iFilePath).Substring(2);
                string rFilePath = Path.Combine(dirName, rFileName);

                bool isDir = Directory.Exists(rFilePath);
                long actualSize = originalSize;
                if (File.Exists(rFilePath))
                {
                    try { actualSize = new FileInfo(rFilePath).Length; } catch { }
                }

                string ext = Path.GetExtension(originalPath);
                string fileType = isDir ? "フォルダー" : (string.IsNullOrEmpty(ext) ? "ファイル" : $"{ext.TrimStart('.').ToUpperInvariant()} ファイル");

                return new FileItem
                {
                    Name = fileName,
                    FullPath = rFilePath,
                    IsDirectory = isDir,
                    SizeInBytes = actualSize,
                    FileType = fileType,
                    DateModified = deletedDate,
                    DateDeleted = deletedDate,
                    OriginalLocation = originalDir,
                    IsRecycleBinItem = true,
                    GlyphIcon = isDir ? "\uE8B7" : NativeFileScanner.GetGlyphIconForExtension(ext)
                };
            }
            catch
            {
                return null;
            }
        }

        public static bool RestoreItems(IEnumerable<string> itemPaths)
        {
            var pathList = itemPaths.ToList();
            if (pathList.Count == 0) return true;

            bool anyRestored = false;

            // 物理ファイル ($R...) の直接復元
            foreach (var path in pathList)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path) ?? string.Empty;
                    string fileName = Path.GetFileName(path);
                    if (fileName.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
                    {
                        string iFileName = "$I" + fileName.Substring(2);
                        string iFilePath = Path.Combine(dir, iFileName);
                        if (File.Exists(iFilePath))
                        {
                            var parsed = ParseIFile(iFilePath);
                            if (parsed != null && !string.IsNullOrEmpty(parsed.OriginalLocation))
                            {
                                string targetDir = parsed.OriginalLocation;
                                string targetPath = Path.Combine(targetDir, parsed.Name);

                                if (!Directory.Exists(targetDir))
                                {
                                    Directory.CreateDirectory(targetDir);
                                }

                                if (Directory.Exists(path))
                                {
                                    Directory.Move(path, targetPath);
                                }
                                else if (File.Exists(path))
                                {
                                    File.Move(path, targetPath, overwrite: true);
                                }

                                try { File.Delete(iFilePath); } catch { }
                                anyRestored = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RecycleBin] Direct restore failed for {path}: {ex.Message}");
                }
            }

            return anyRestored;
        }

        public static bool DeletePermanently(IEnumerable<string> itemPaths)
        {
            var pathList = itemPaths.ToList();
            if (pathList.Count == 0) return true;

            // 物理ファイル ($R... / $I...) の直接完全消去
            foreach (var path in pathList)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path) ?? string.Empty;
                    string fileName = Path.GetFileName(path);
                    if (fileName.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
                    {
                        string iFileName = "$I" + fileName.Substring(2);
                        string iFilePath = Path.Combine(dir, iFileName);
                        if (File.Exists(iFilePath))
                        {
                            try { File.Delete(iFilePath); } catch { }
                        }

                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        else if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                }
                catch { }
            }

            return true;
        }

        public static bool EmptyRecycleBin(nint hwnd, bool showConfirmation = true)
        {
            try
            {
                uint flags = 0;
                if (!showConfirmation)
                {
                    flags |= Win32Interop.SHERB_NOCONFIRMATION;
                }

                int result = Win32Interop.SHEmptyRecycleBinW(hwnd, null, flags);
                return result == 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RecycleBin] EmptyRecycleBin error: {ex.Message}");
                return false;
            }
        }

        public static (long TotalBytes, long NumItems) GetRecycleBinInfo()
        {
            try
            {
                var rbInfo = new Win32Interop.SHQUERYRBINFO
                {
                    cbSize = Marshal.SizeOf<Win32Interop.SHQUERYRBINFO>()
                };

                int hr = Win32Interop.SHQueryRecycleBinW(null, ref rbInfo);
                if (hr == 0)
                {
                    return (rbInfo.i64Size, rbInfo.i64NumItems);
                }
            }
            catch { }

            return (0, 0);
        }
    }
}
