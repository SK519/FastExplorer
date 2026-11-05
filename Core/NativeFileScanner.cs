using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FastExplorer.Models;

namespace FastExplorer.Core
{
    public static partial class NativeFileScanner
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
                        bool isReparse = (dir.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (!showHiddenFiles && isHidden) continue;

                        items.Add(new FileItem
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            IsReparsePoint = isReparse,
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
                    bool isReparsePoint = (findData.dwFileAttributes & Win32Interop.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

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
                        IsReparsePoint = isReparsePoint,
                        HasCustomFolderAttribute = isDirectory && (isReadOnly || isSystem),
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

        internal static List<FileItem> GetDrivesInternal(bool networkOnly)
        {
            var driveItems = new List<FileItem>();

            char[] buffer = new char[512];
            uint length = Win32Interop.GetLogicalDriveStringsW((uint)buffer.Length, buffer);

            if (length > 0)
            {
                string rawDrives = new(buffer, 0, (int)length);
                string[] driveRoots = rawDrives.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Array.Sort(driveRoots, StringComparer.OrdinalIgnoreCase);

                var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in driveRoots)
                {
                    if (!seenRoots.Add(root))
                        continue;

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
                    string volumeLabel = string.Empty;
                    long totalBytes = 0;
                    long freeBytes = 0;
                    double drivePercent = 0;
                    string driveSpaceText = string.Empty;

                    try
                    {
                        var driveInfo = new DriveInfo(root);
                        if (driveInfo.IsReady)
                        {
                            volumeLabel = driveInfo.VolumeLabel;
                            totalBytes = driveInfo.TotalSize;
                            freeBytes = driveInfo.AvailableFreeSpace;
                            if (totalBytes > 0)
                            {
                                long usedBytes = totalBytes - freeBytes;
                                drivePercent = Math.Clamp(((double)usedBytes / totalBytes) * 100.0, 0.0, 100.0);
                                string freeStr = FileItem.FormatDiskSpace(freeBytes);
                                string totalStr = FileItem.FormatDiskSpace(totalBytes);
                                driveSpaceText = $"空き領域 {freeStr}/{totalStr}";
                            }
                        }
                    }
                    catch { }

                    string label;
                    if (!string.IsNullOrWhiteSpace(volumeLabel))
                    {
                        label = $"{volumeLabel} ({driveLetter})";
                    }
                    else
                    {
                        label = $"{typeName} ({driveLetter})";
                    }

                    driveItems.Add(new FileItem
                    {
                        Name = label,
                        FullPath = root,
                        IsDirectory = true,
                        IsDrive = true,
                        FileType = typeName,
                        GlyphIcon = "\uEDA2",
                        DrivePercent = drivePercent,
                        DriveSpaceText = driveSpaceText,
                        SizeInBytes = totalBytes
                    });
                }
            }

            driveItems.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
            return driveItems;
        }

        private static readonly FrozenDictionary<string, (string FileType, string GlyphIcon)> KnownExtensions =
            new Dictionary<string, (string FileType, string GlyphIcon)>(StringComparer.OrdinalIgnoreCase)
            {
                // 画像
                [".png"] = ("画像ファイル", "\uEB9F"),
                [".jpg"] = ("画像ファイル", "\uEB9F"),
                [".jpeg"] = ("画像ファイル", "\uEB9F"),
                [".bmp"] = ("画像ファイル", "\uEB9F"),
                [".gif"] = ("画像ファイル", "\uEB9F"),
                [".webp"] = ("画像ファイル", "\uEB9F"),
                [".ico"] = ("画像ファイル", "\uEB9F"),
                [".svg"] = ("画像ファイル", "\uEB9F"),
                [".heic"] = ("画像ファイル", "\uEB9F"),
                [".tiff"] = ("画像ファイル", "\uEB9F"),
                [".tif"] = ("画像ファイル", "\uEB9F"),

                // 動画
                [".mp4"] = ("動画ファイル", "\uE714"),
                [".mkv"] = ("動画ファイル", "\uE714"),
                [".avi"] = ("動画ファイル", "\uE714"),
                [".mov"] = ("動画ファイル", "\uE714"),
                [".wmv"] = ("動画ファイル", "\uE714"),
                [".webm"] = ("動画ファイル", "\uE714"),
                [".flv"] = ("動画ファイル", "\uE714"),
                [".m4v"] = ("動画ファイル", "\uE714"),

                // 音声
                [".mp3"] = ("音声ファイル", "\uE8D6"),
                [".wav"] = ("音声ファイル", "\uE8D6"),
                [".flac"] = ("音声ファイル", "\uE8D6"),
                [".aac"] = ("音声ファイル", "\uE8D6"),
                [".m4a"] = ("音声ファイル", "\uE8D6"),
                [".ogg"] = ("音声ファイル", "\uE8D6"),
                [".wma"] = ("音声ファイル", "\uE8D6"),

                // 圧縮
                [".zip"] = ("圧縮フォルダー", "\uF012"),
                [".7z"] = ("圧縮フォルダー", "\uF012"),
                [".rar"] = ("圧縮フォルダー", "\uF012"),
                [".tar"] = ("圧縮フォルダー", "\uF012"),
                [".gz"] = ("圧縮フォルダー", "\uF012"),
                [".bz2"] = ("圧縮フォルダー", "\uF012"),
                [".iso"] = ("圧縮フォルダー", "\uF012"),

                // 実行 / アプリ / スクリプト
                [".exe"] = ("アプリケーション", "\uE756"),
                [".dll"] = ("アプリケーション拡張", "\uE756"),
                [".msi"] = ("スクリプト / コマンド", "\uE756"),
                [".bat"] = ("スクリプト / コマンド", "\uE756"),
                [".cmd"] = ("スクリプト / コマンド", "\uE756"),
                [".ps1"] = ("スクリプト / コマンド", "\uE756"),
                [".vbs"] = ("スクリプト / コマンド", "\uE756"),
                [".reg"] = ("スクリプト / コマンド", "\uE756"),

                // ドキュメント
                [".pdf"] = ("PDF ドキュメント", "\uEA90"),
                [".txt"] = ("テキスト ドキュメント", "\uE8A5"),
                [".md"] = ("Markdown ドキュメント", "\uE8A5"),
                [".docx"] = ("Word ドキュメント", "\uE8A5"),
                [".doc"] = ("Word ドキュメント", "\uE8A5"),
                [".odt"] = ("Word ドキュメント", "\uE8A5"),
                [".rtf"] = ("Word ドキュメント", "\uE8A5"),
                [".log"] = ("Word ドキュメント", "\uE8A5"),
                [".xlsx"] = ("Excel / CSV ワークシート", "\uE80A"),
                [".xls"] = ("Excel / CSV ワークシート", "\uE80A"),
                [".csv"] = ("Excel / CSV ワークシート", "\uE80A"),
                [".tsv"] = ("Excel / CSV ワークシート", "\uE80A"),
                [".pptx"] = ("PowerPoint プレゼンテーション", "\uE8A5"),
                [".ppt"] = ("PowerPoint プレゼンテーション", "\uE8A5"),
                [".key"] = ("PowerPoint プレゼンテーション", "\uE8A5"),

                // Web / マークアップ
                [".html"] = ("HTML ドキュメント", "\uE774"),
                [".htm"] = ("HTML ドキュメント", "\uE774"),
                [".url"] = ("Web ショートカット", "\uE774"),
                [".website"] = ("Web ショートカット", "\uE774"),

                // 構成 / データ
                [".json"] = ("JSON ファイル", "\uE943"),
                [".xml"] = ("XML ファイル", "\uE943"),
                [".ini"] = ("構成ファイル", "\uE713"),
                [".inf"] = ("構成ファイル", "\uE713"),
                [".cfg"] = ("構成ファイル", "\uE713"),
                [".config"] = ("構成ファイル", "\uE713"),
                [".yaml"] = ("構成ファイル", "\uE713"),
                [".yml"] = ("構成ファイル", "\uE713"),
                [".toml"] = ("構成ファイル", "\uE713"),

                // フォント
                [".ttf"] = ("フォント ファイル", "\uE8D2"),
                [".otf"] = ("フォント ファイル", "\uE8D2"),
                [".woff"] = ("フォント ファイル", "\uE8D2"),
                [".woff2"] = ("フォント ファイル", "\uE8D2"),

                // ソースコード
                [".cs"] = ("C# ソース ファイル", "\uE943"),
                [".cpp"] = ("C/C++ ソース ファイル", "\uE943"),
                [".c"] = ("C/C++ ソース ファイル", "\uE943"),
                [".h"] = ("C/C++ ソース ファイル", "\uE943"),
                [".py"] = ("Python スクリプト", "\uE943"),
                [".js"] = ("JavaScript / TypeScript ファイル", "\uE943"),
                [".ts"] = ("JavaScript / TypeScript ファイル", "\uE943"),
                [".css"] = ("スタイルシート", "\uE943"),
                [".sln"] = ("開発ソース ファイル", "\uE943"),
                [".slnx"] = ("開発ソース ファイル", "\uE943"),
                [".csproj"] = ("開発ソース ファイル", "\uE943"),
                [".xaml"] = ("開発ソース ファイル", "\uE943"),
                [".sql"] = ("開発ソース ファイル", "\uE943"),
                [".rs"] = ("開発ソース ファイル", "\uE943"),
                [".go"] = ("開発ソース ファイル", "\uE943"),
                [".java"] = ("開発ソース ファイル", "\uE943")
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenDictionary<string, (string FileType, string GlyphIcon)>.AlternateLookup<ReadOnlySpan<char>> KnownExtensionsLookup =
            KnownExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string FileType, string GlyphIcon)> _dynamicFileInfoCache = new(StringComparer.OrdinalIgnoreCase);

        public static void GetFileInfoForExtension(ReadOnlySpan<char> extension, out string fileType, out string glyphIcon)
        {
            if (extension.IsEmpty)
            {
                fileType = "ファイル";
                glyphIcon = "\uE7C3";
                return;
            }

            if (KnownExtensionsLookup.TryGetValue(extension, out var known))
            {
                fileType = known.FileType;
                glyphIcon = known.GlyphIcon;
                return;
            }

            string extStr = extension.ToString();
            if (_dynamicFileInfoCache.TryGetValue(extStr, out var cached))
            {
                fileType = cached.FileType;
                glyphIcon = cached.GlyphIcon;
                return;
            }

            string desc = extStr.Length > 1 ? $"{extStr[1..].ToUpperInvariant()} ファイル" : "ファイル";
            glyphIcon = "\uE7C3";
            _dynamicFileInfoCache[extStr] = (desc, glyphIcon);
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
