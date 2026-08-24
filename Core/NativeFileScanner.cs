using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FastExplorer.Models;

namespace FastExplorer.Core
{
    public static class NativeFileScanner
    {
        public static List<FileItem> ScanDirectory(string directoryPath, bool showHiddenFiles = false)
        {
            var items = new List<FileItem>(1024);

            if (string.IsNullOrWhiteSpace(directoryPath))
                return items;

            // ディレクトリの正規化 (UNC パス \\wsl.localhost 等を考慮)
            string normalizedPath = directoryPath;
            try
            {
                if (!directoryPath.StartsWith(@"\\"))
                {
                    normalizedPath = Path.GetFullPath(directoryPath);
                }
            }
            catch
            {
                normalizedPath = directoryPath;
            }

            if (!normalizedPath.EndsWith('\\'))
            {
                normalizedPath += "\\";
            }

            string searchPattern = normalizedPath + "*";

            nint hFind = Win32Interop.FindFirstFileExW(
                searchPattern,
                Win32Interop.FINDEX_INFO_LEVELS.FindExInfoBasic,
                out Win32Interop.WIN32_FIND_DATAW findData,
                Win32Interop.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                nint.Zero,
                Win32Interop.FIND_FIRST_EX_LARGE_FETCH);

            if (hFind == nint.Zero || hFind == (nint)(-1))
            {
                // アクセス拒否等の場合は .NET API でフォールバック試行
                try
                {
                    var dirInfo = new DirectoryInfo(directoryPath);
                    foreach (var dir in dirInfo.EnumerateDirectories())
                    {
                        bool isHidden = (dir.Attributes & FileAttributes.Hidden) != 0;
                        if (!showHiddenFiles && isHidden) continue;

                        items.Add(new FileItem
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            DateModified = dir.LastWriteTime,
                            FileType = "フォルダ",
                            GlyphIcon = "\uE8B7"
                        });
                    }
                    foreach (var file in dirInfo.EnumerateFiles())
                    {
                        bool isHidden = (file.Attributes & FileAttributes.Hidden) != 0;
                        if (!showHiddenFiles && isHidden) continue;

                        items.Add(new FileItem
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            IsDirectory = false,
                            SizeInBytes = file.Length,
                            DateModified = file.LastWriteTime,
                            FileType = GetFileTypeDescription(file.Extension),
                            GlyphIcon = GetGlyphIconForExtension(file.Extension)
                        });
                    }
                }
                catch
                {
                    // ignored
                }

                return items;
            }

            try
            {
                do
                {
                    ReadOnlySpan<char> nameSpan = findData.FileNameSpan;
                    if (nameSpan.IsEmpty)
                        continue;

                    // "." と ".." は Filter-first で最優先スキップ (文字列アロケーションゼロ)
                    if (nameSpan.Length == 1 && nameSpan[0] == '.')
                        continue;
                    if (nameSpan.Length == 2 && nameSpan[0] == '.' && nameSpan[1] == '.')
                        continue;

                    bool isHidden = (findData.dwFileAttributes & Win32Interop.FILE_ATTRIBUTE_HIDDEN) != 0;
                    bool isSystem = (findData.dwFileAttributes & Win32Interop.FILE_ATTRIBUTE_SYSTEM) != 0;

                    if (!showHiddenFiles && (isHidden || isSystem))
                        continue;

                    bool isDirectory = (findData.dwFileAttributes & Win32Interop.FILE_ATTRIBUTE_DIRECTORY) != 0;
                    bool isReadOnly = (findData.dwFileAttributes & Win32Interop.FILE_ATTRIBUTE_READONLY) != 0;

                    long fileSize = isDirectory
                        ? 0
                        : ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;

                    DateTime lastModified = Win32Interop.ToDateTime(findData.ftLastWriteTime);

                    string fileName = nameSpan.ToString();
                    string fullPath = string.Concat(normalizedPath, fileName);

                    ReadOnlySpan<char> extSpan = Path.GetExtension(nameSpan);
                    string fileType = isDirectory ? "フォルダ" : GetFileTypeDescription(extSpan);
                    string glyphIcon = isDirectory ? "\uE8B7" : GetGlyphIconForExtension(extSpan);

                    items.Add(new FileItem
                    {
                        Name = fileName,
                        FullPath = fullPath,
                        IsDirectory = isDirectory,
                        SizeInBytes = fileSize,
                        DateModified = lastModified,
                        FileType = fileType,
                        IsHidden = isHidden,
                        IsReadOnly = isReadOnly,
                        GlyphIcon = glyphIcon
                    });

                } while (Win32Interop.FindNextFileW(hFind, out findData));
            }
            finally
            {
                Win32Interop.FindClose(hFind);
            }

            return items;
        }

        public static List<FileItem> GetDrives()
        {
            var driveItems = new List<FileItem>();

            char[] buffer = new char[512];
            uint length = Win32Interop.GetLogicalDriveStringsW((uint)buffer.Length, buffer);

            if (length > 0)
            {
                string rawDrives = new(buffer, 0, (int)length);
                string[] driveRoots = rawDrives.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                foreach (string root in driveRoots)
                {
                    uint driveType = Win32Interop.GetDriveTypeW(root);
                    string typeName = driveType switch
                    {
                        Win32Interop.DRIVE_FIXED => "ローカル ディスク",
                        Win32Interop.DRIVE_REMOVABLE => "USB ドライブ",
                        Win32Interop.DRIVE_REMOTE => "ネットワーク ドライブ",
                        Win32Interop.DRIVE_CDROM => "CD/DVD ドライブ",
                        Win32Interop.DRIVE_RAMDISK => "RAM ディスク",
                        _ => "ドライブ"
                    };

                    string driveLetter = root.TrimEnd('\\');
                    string label = $"{typeName} ({driveLetter})";

                    driveItems.Add(new FileItem
                    {
                        Name = label,
                        FullPath = root,
                        IsDirectory = true,
                        FileType = "ドライブ",
                        GlyphIcon = "\uEDA2"
                    });
                }
            }

            return driveItems;
        }

        public static List<FileItem> GetNetworkPlaces()
        {
            var items = new List<FileItem>();
            try
            {
                var drives = GetDrives();
                foreach (var drive in drives)
                {
                    if (drive.FileType.Contains("ネットワーク") || drive.Name.Contains("ネットワーク"))
                    {
                        items.Add(drive);
                    }
                }
            }
            catch { }
            return items;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _fileTypeCache = new(StringComparer.OrdinalIgnoreCase);

        public static string GetFileTypeDescription(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return "ファイル";

            return GetFileTypeDescription(extension.AsSpan());
        }

        public static string GetFileTypeDescription(ReadOnlySpan<char> extension)
        {
            if (extension.IsEmpty)
                return "ファイル";

            // 高速なインライン判定 (大文字小文字を問わない判定)
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) return "テキスト ドキュメント";
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) return "Markdown ドキュメント";
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return "JSON ファイル";
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return "XML ファイル";
            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return "C# ソース ファイル";
            if (extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".c", StringComparison.OrdinalIgnoreCase) || extension.Equals(".h", StringComparison.OrdinalIgnoreCase)) return "C/C++ ソース ファイル";
            if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)) return "Python スクリプト";
            if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)) return "JavaScript / TypeScript ファイル";
            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)) return "HTML ドキュメント";
            if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)) return "スタイルシート";
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return "画像ファイル";
            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)) return "動画ファイル";
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)) return "音声ファイル";
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase) || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)) return "圧縮フォルダー";
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return "アプリケーション";
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return "アプリケーション拡張";
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "PDF ドキュメント";
            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)) return "Word ドキュメント";
            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return "Excel / CSV ワークシート";
            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase)) return "PowerPoint プレゼンテーション";

            string extStr = extension.ToString();
            if (_fileTypeCache.TryGetValue(extStr, out var cached))
                return cached;

            string desc = extStr.Length > 1 ? $"{extStr[1..].ToUpperInvariant()} ファイル" : "ファイル";
            _fileTypeCache[extStr] = desc;
            return desc;
        }

        public static string GetGlyphIconForExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return "\uE7C3"; // Document
            return GetGlyphIconForExtension(extension.AsSpan());
        }

        public static string GetGlyphIconForExtension(ReadOnlySpan<char> extension)
        {
            if (extension.IsEmpty) return "\uE7C3"; // Document

            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tif", StringComparison.OrdinalIgnoreCase))
                return "\uEB9F"; // Image

            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".flv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
                return "\uE714"; // Video

            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wma", StringComparison.OrdinalIgnoreCase))
                return "\uE8D6"; // Audio

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tar", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                return "\uF012"; // Zip/Archive

            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".vbs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".reg", StringComparison.OrdinalIgnoreCase))
                return "\uE756"; // Application/Script

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return "\uEA90"; // PDF

            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".odt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                return "\uE8A5"; // Document

            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
                return "\uE80A"; // Spreadsheet

            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
                return "\uE8A5"; // Presentation

            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".website", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                return "\uE774"; // Web link

            if (extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".inf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
                return "\uE713"; // Config

            if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".woff", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase))
                return "\uE8D2"; // Font

            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".go", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".java", StringComparison.OrdinalIgnoreCase))
                return "\uE943"; // Code

            return "\uE7C3"; // Generic File
        }
    }
}
