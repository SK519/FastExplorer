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
                    string fileType;
                    string glyphIcon;
                    if (isDirectory)
                    {
                        fileType = "フォルダ";
                        glyphIcon = "\uE8B7";
                    }
                    else
                    {
                        GetFileInfoForExtension(extSpan, out fileType, out glyphIcon);
                    }

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

        public static List<FileItem> GetDrives() => GetDrivesInternal(false);
        public static List<FileItem> GetNetworkPlaces() => GetDrivesInternal(true);

        private static List<FileItem> GetDrivesInternal(bool networkOnly)
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
                    if (networkOnly && driveType != Win32Interop.DRIVE_REMOTE)
                        continue;

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

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string FileType, string GlyphIcon)> _fileInfoCache = new(StringComparer.OrdinalIgnoreCase);

        public static void GetFileInfoForExtension(ReadOnlySpan<char> extension, out string fileType, out string glyphIcon)
        {
            if (extension.IsEmpty)
            {
                fileType = "ファイル";
                glyphIcon = "\uE7C3";
                return;
            }

            // 画像
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
            {
                fileType = "画像ファイル";
                glyphIcon = "\uEB9F";
                return;
            }

            // 動画
            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".flv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "動画ファイル";
                glyphIcon = "\uE714";
                return;
            }

            // 音声
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".wma", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "音声ファイル";
                glyphIcon = "\uE8D6";
                return;
            }

            // 圧縮
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tar", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "圧縮フォルダー";
                glyphIcon = "\uF012";
                return;
            }

            // 実行 / アプリケーション / スクリプト
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "アプリケーション";
                glyphIcon = "\uE756";
                return;
            }
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "アプリケーション拡張";
                glyphIcon = "\uE756";
                return;
            }
            if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".vbs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".reg", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "スクリプト / コマンド";
                glyphIcon = "\uE756";
                return;
            }

            // ドキュメント
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "PDF ドキュメント";
                glyphIcon = "\uEA90";
                return;
            }
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "テキスト ドキュメント";
                glyphIcon = "\uE8A5";
                return;
            }
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "Markdown ドキュメント";
                glyphIcon = "\uE8A5";
                return;
            }
            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".odt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "Word ドキュメント";
                glyphIcon = "\uE8A5";
                return;
            }
            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "Excel / CSV ワークシート";
                glyphIcon = "\uE80A";
                return;
            }
            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "PowerPoint プレゼンテーション";
                glyphIcon = "\uE8A5";
                return;
            }

            // Web / マークアップ
            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "HTML ドキュメント";
                glyphIcon = "\uE774";
                return;
            }
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".website", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "Web ショートカット";
                glyphIcon = "\uE774";
                return;
            }

            // 構成 / データ
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "JSON ファイル";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "XML ファイル";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".inf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "構成ファイル";
                glyphIcon = "\uE713";
                return;
            }

            // フォント
            if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".woff", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "フォント ファイル";
                glyphIcon = "\uE8D2";
                return;
            }

            // ソースコード
            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "C# ソース ファイル";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "C/C++ ソース ファイル";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "Python スクリプト";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "JavaScript / TypeScript ファイル";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "スタイルシート";
                glyphIcon = "\uE943";
                return;
            }
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".rs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".go", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".java", StringComparison.OrdinalIgnoreCase))
            {
                fileType = "開発ソース ファイル";
                glyphIcon = "\uE943";
                return;
            }

            string extStr = extension.ToString();
            if (_fileInfoCache.TryGetValue(extStr, out var cached))
            {
                fileType = cached.FileType;
                glyphIcon = cached.GlyphIcon;
                return;
            }

            string desc = extStr.Length > 1 ? $"{extStr[1..].ToUpperInvariant()} ファイル" : "ファイル";
            glyphIcon = "\uE7C3";
            _fileInfoCache[extStr] = (desc, glyphIcon);
            fileType = desc;
        }

        public static string GetFileTypeDescription(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return "ファイル";
            return GetFileTypeDescription(extension.AsSpan());
        }

        public static string GetFileTypeDescription(ReadOnlySpan<char> extension)
        {
            GetFileInfoForExtension(extension, out string fileType, out _);
            return fileType;
        }

        public static string GetGlyphIconForExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return "\uE7C3";
            return GetGlyphIconForExtension(extension.AsSpan());
        }

        public static string GetGlyphIconForExtension(ReadOnlySpan<char> extension)
        {
            GetFileInfoForExtension(extension, out _, out string glyphIcon);
            return glyphIcon;
        }
    }
}
