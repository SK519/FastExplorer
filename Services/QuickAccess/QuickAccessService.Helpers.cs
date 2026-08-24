using System;
using System.IO;

namespace FastExplorer.Services
{
    public static partial class QuickAccessService
    {
        public static bool IsWslPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.StartsWith(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\\wsl$", StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatDisplayName(string name, string path)
        {
            string displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path.TrimEnd('\\', '/')) : name;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = path;

            if (IsWslPath(path))
            {
                if (!displayName.Contains("(WSL)", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = $"{displayName} (WSL)";
                }
            }
            return displayName;
        }

        private static void NotifyPinnedChanged()
        {
            PinnedItemsChanged?.Invoke();
        }

        private static string ResolveFolderGlyph(string name, string path)
        {
            string lowerName = name.ToLowerInvariant();
            string lowerPath = path.ToLowerInvariant();

            if (lowerName.Contains("download") || lowerName.Contains("ダウンロード")) return "\uE896";
            if (lowerName.Contains("desktop") || lowerName.Contains("デスクトップ")) return "\uE8B7";
            if (lowerName.Contains("document") || lowerName.Contains("ドキュメント")) return "\uE8A5";
            if (lowerName.Contains("picture") || lowerName.Contains("ピクチャ") || lowerName.Contains("photo") || lowerName.Contains("画像")) return "\uEB9F";
            if (lowerName.Contains("music") || lowerName.Contains("ミュージック")) return "\uE8D6";
            if (lowerName.Contains("video") || lowerName.Contains("ビデオ") || lowerName.Contains("動画")) return "\uE714";
            if (lowerName.Contains("drive") || lowerName.Contains("ドライブ") || lowerPath.Contains("google")) return "\uEDA2";
            if (lowerPath.Length <= 3 && lowerPath.EndsWith(":\\")) return "\uEDA2"; // ドライブレター
            if (lowerPath.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) return "\uE77B"; // ユーザーフォルダ

            return "\uE838"; // フォルダアイコン (Fluent folder)
        }

        public static string ResolveLocationSubtitle(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "ローカルに保存済み";
            string lower = path.ToLowerInvariant();
            if (lower.Length <= 3 && lower.EndsWith(":\\")) return "PC";
            if (lower.StartsWith(@"\\wsl")) return "Linux";
            if (lower.StartsWith(@"\\")) return "ネットワーク";
            if (lower.Contains("google drive") || lower.Contains("googledrive"))
            {
                int idx = path.IndexOf("Google Drive", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string tail = path[idx..];
                    if (tail.Length > 24) tail = tail[..22] + "...";
                    return tail;
                }
                return "Google Drive";
            }
            if (lower.Contains("onedrive")) return "OneDrive";
            if (lower.Contains("dropbox")) return "Dropbox";
            return "ローカルに保存済み";
        }
    }
}
