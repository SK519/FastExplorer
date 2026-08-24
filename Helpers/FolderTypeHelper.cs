using System;
using System.Collections.Generic;
using System.IO;
using FastExplorer.Models;

namespace FastExplorer.Helpers
{
    public static class FolderTypeHelper
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".ico", ".tiff", ".tif",
            ".svg", ".avif", ".raw", ".cr2", ".nef", ".arw", ".dng", ".heic", ".heif",
            ".psd", ".ai", ".clip"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts", ".m2ts"
        };

        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".alac", ".opus"
        };

        private static readonly string[] ImageFolderKeywords =
        {
            "picture", "pictures", "ピクチャ", "photo", "photos", "写真",
            "image", "images", "画像", "screenshot", "screenshots", "スクリーンショット",
            "camera", "カメラ", "camera roll", "カメラ ロール", "dcim",
            "wallpaper", "wallpapers", "壁紙", "illust", "イラスト",
            "album", "アルバム", "gallery", "ギャラリー", "art", "cg", "pixiv", "graphics", "scan", "スキャン"
        };

        /// <summary>
        /// パスやフォルダー名から画像フォルダーであるかを判定します
        /// </summary>
        public static bool IsImageFolderByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string folderName = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName))
            {
                folderName = path;
            }

            foreach (var keyword in ImageFolderKeywords)
            {
                if (folderName.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
                    folderName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // パス全体（親フォルダー含む）に "Pictures", "ピクチャ", "Photos" などが含まれているかチェック
            string lowerPath = path.ToLowerInvariant();
            if (lowerPath.Contains(@"\pictures\") || lowerPath.EndsWith(@"\pictures") ||
                lowerPath.Contains(@"\ピクチャ\") || lowerPath.EndsWith(@"\ピクチャ") ||
                lowerPath.Contains(@"\photos\") || lowerPath.EndsWith(@"\photos") ||
                lowerPath.Contains(@"\dcim\") || lowerPath.EndsWith(@"\dcim"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// フォルダー内のアイテム構成（画像ファイル数や割合）からフォルダー種別を判定し、最適な表示モードを返します
        /// </summary>
        public static FolderViewMode? DetectViewModeFromContent(IReadOnlyList<FileItem> items)
        {
            if (items == null || items.Count == 0) return null;

            int totalFiles = 0;
            int imageCount = 0;
            int videoCount = 0;
            int audioCount = 0;

            foreach (var item in items)
            {
                if (item.IsDirectory) continue;

                totalFiles++;
                string ext = item.Extension;
                if (string.IsNullOrEmpty(ext)) continue;

                if (ImageExtensions.Contains(ext))
                {
                    imageCount++;
                }
                else if (VideoExtensions.Contains(ext))
                {
                    videoCount++;
                }
                else if (AudioExtensions.Contains(ext))
                {
                    audioCount++;
                }
            }

            if (totalFiles == 0) return null;

            // 画像ファイルが 3 件以上、またはファイル全体の 30% 以上を占める場合、画像フォルダーと判定
            double imageRatio = (double)imageCount / totalFiles;
            if (imageCount >= 3 || (imageCount >= 1 && imageRatio >= 0.3))
            {
                return FolderViewMode.LargeIcons;
            }

            // 動画ファイルが多数を占める場合も大アイコン
            double videoRatio = (double)videoCount / totalFiles;
            if (videoCount >= 3 || (videoCount >= 1 && videoRatio >= 0.4))
            {
                return FolderViewMode.LargeIcons;
            }

            return null;
        }
    }
}
