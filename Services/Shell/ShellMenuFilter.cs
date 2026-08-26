using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    public class VendorRule
    {
        public string Key { get; }
        public string DisplayName { get; }
        public string Glyph { get; }
        public string[] Keywords { get; }
        public string[] VerbPrefixes { get; }
        public bool IsClusterable { get; }

        public VendorRule(string key, string displayName, string glyph, string[] keywords, string[] verbPrefixes, bool isClusterable = false)
        {
            Key = key;
            DisplayName = displayName;
            Glyph = glyph;
            Keywords = keywords;
            VerbPrefixes = verbPrefixes;
            IsClusterable = isClusterable;
        }

        public bool Matches(string text, string? verb = null)
        {
            if (!string.IsNullOrEmpty(verb))
            {
                string lowerVerb = verb.ToLowerInvariant();
                foreach (var vp in VerbPrefixes)
                {
                    if (lowerVerb.StartsWith(vp.ToLowerInvariant())) return true;
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                string lowerText = text.ToLowerInvariant();
                foreach (var kw in Keywords)
                {
                    if (lowerText.Contains(kw.ToLowerInvariant())) return true;
                }
            }

            return false;
        }
    }

    public static class ShellMenuFilter
    {
        public static readonly List<VendorRule> VendorRules = new()
        {
            new VendorRule(
                "GoogleDrive", "Google ドライブ", "\uE721",
                new[] {
                    "google ドライブ", "google drive", "gdrive",
                    "google ドキュメント", "google docs", "ドキュメントで開く",
                    "google スプレッドシート", "google sheets", "スプレッドシートで開く", "スプレッドシート",
                    "google スライド", "google slides", "スライドで開く",
                    "google フォーム", "google forms",
                    "gemini",
                    "リンクをクリップボードにコピー", "診断情報をクリップボードにコピー",
                    "オフライン アクセス", "オフラインで使用可能", "オンラインのみ",
                    "copy link to clipboard", "copy diagnostic information"
                },
                new[] { "googledrive", "gdrive", "googledocs", "googlesheets", "googleslides" },
                isClusterable: true
            ),
            new VendorRule(
                "GoogleSearch", "Google 検索", "\uE721",
                new[] { "search with google", "google で検索", "google 検索" },
                new[] { "googlesearch" },
                isClusterable: false
            ),
            new VendorRule(
                "PeaZip", "PeaZip", "\uE8B7",
                new[] { "peazip" },
                new[] { "peazip" },
                isClusterable: true
            ),
            new VendorRule(
                "SevenZip", "7-Zip", "\uE8B7",
                new[] { "7-zip", "7zip", "winrar", "nanazip", "bandizip" },
                new[] { "7-zip", "7zip", "winrar", "nanazip", "bandizip" },
                isClusterable: true
            ),
            new VendorRule(
                "QuickShare", "Quick Share", "\uE72D",
                new[] { "quick share", "quickshare" },
                new[] { "quickshare" },
                isClusterable: false
            ),
            new VendorRule(
                "PowerRename", "PowerRename", "\uE8AC",
                new[] { "powerrename" },
                new[] { "powerrename" },
                isClusterable: false
            ),
            new VendorRule(
                "DefenderScan", "Microsoft Defender", "\uE8B8",
                new[] { "defender", "スキャン" },
                new[] { "defender" },
                isClusterable: false
            ),
            new VendorRule(
                "RotateImage", "画像回転", "\uE7AD",
                new[] { "回転", "rotate" },
                Array.Empty<string>(),
                isClusterable: false
            ),
            new VendorRule(
                "PhotoEdit", "画像編集", "\uEB9F",
                new[] { "フォト", "designer", "ペイント" },
                Array.Empty<string>(),
                isClusterable: false
            )
        };

        public static VendorRule? FindMatchingVendorRule(string text, string? verb = null)
        {
            foreach (var rule in VendorRules)
            {
                if (rule.Matches(text, verb)) return rule;
            }
            return null;
        }

        public static bool HasAnyShellExtractionEnabled(ShellMenuConfig config)
        {
            if (config == null) return false;
            return config.ShowAllShellItems || config.ItemVisibilityState.Any(kvp => kvp.Value) || !string.IsNullOrWhiteSpace(config.CustomShellKeywords);
        }

        public static bool IsBuiltinDuplicate(string text)
        {
            string lower = text.ToLowerInvariant();
            if (lower is "開く" or "open" or "切り取り" or "cut" or "コピー" or "copy"
                or "削除" or "delete" or "名前の変更" or "rename" or "プロパティ" or "properties"
                or "プログラムから開く" or "open with" or "その他のオプションを表示"
                or "送る" or "send to" or "デバイス キャスト" or "cast to device")
            {
                return true;
            }

            if (lower.StartsWith("プログラムから開く") || lower.StartsWith("open with") ||
                lower.StartsWith("送る") || lower.StartsWith("send to") ||
                lower.StartsWith("デバイス キャスト") || lower.StartsWith("cast to device"))
            {
                return true;
            }

            // スタートメニューへのピン留め / タスクバーへのピン留め (Windows 10/11 のセキュリティ制限によりサードパーティ製アプリからは OS レベルでブロックされる項目を除外)
            if ((lower.Contains("ピン留め") || lower.Contains("pin")) &&
                (lower.Contains("スタート") || lower.Contains("start") || lower.Contains("タスク") || lower.Contains("taskbar")))
            {
                return true;
            }

            return false;
        }

        public static bool IsExcluded(string text, string? excludedKeywords)
        {
            if (string.IsNullOrWhiteSpace(excludedKeywords)) return false;
            var exKeywords = excludedKeywords.Split(new[] { ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string lower = text.ToLowerInvariant();
            foreach (var kw in exKeywords)
            {
                if (lower.Contains(kw.ToLowerInvariant())) return true;
            }
            return false;
        }

        public static bool MatchesShellConfig(string itemText, ShellMenuConfig config, out string glyph, string? verb = null)
        {
            glyph = GetSmartGlyph(itemText, verb);
            string cleanText = itemText.Replace("&", "").Trim();
            string lower = cleanText.ToLowerInvariant();

            // 1. 個別項目の状態辞書チェック (ユーザーが明示的に設定した項目は最優先)
            if (config.ItemVisibilityState.TryGetValue(cleanText, out bool isItemEnabled))
            {
                if (!isItemEnabled) return false;
                if (IsExcluded(lower, config.ExcludedShellKeywords)) return false;
                return true;
            }

            // 2. 除外キーワードチェック
            if (IsExcluded(lower, config.ExcludedShellKeywords))
            {
                return false;
            }

            // 3. すべて自動表示が ON の場合 (新規検出項目の自動表示)
            if (config.ShowAllShellItems) return true;

            // 4. 優先抽出キーワードチェック
            if (!string.IsNullOrWhiteSpace(config.CustomShellKeywords))
            {
                var keywords = config.CustomShellKeywords.Split(new[] { ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var kw in keywords)
                {
                    if (lower.Contains(kw.ToLowerInvariant()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool MatchesShellConfig(string parentLabel, string childText, ShellMenuConfig config, out string glyph, string? verb = null)
        {
            glyph = GetSmartGlyph(childText, verb);
            string cleanChild = childText.Replace("&", "").Trim();
            string fullCleanText = $"{parentLabel} → {cleanChild}";
            string combinedText = $"{parentLabel} {cleanChild}".ToLowerInvariant();

            // 1. 個別項目の状態辞書チェック (フルパスまたは子単体)
            if (config.ItemVisibilityState.TryGetValue(fullCleanText, out bool isFullEnabled))
            {
                if (!isFullEnabled) return false;
                if (IsExcluded(combinedText, config.ExcludedShellKeywords)) return false;
                return true;
            }

            if (config.ItemVisibilityState.TryGetValue(cleanChild, out bool isChildEnabled))
            {
                if (!isChildEnabled) return false;
                if (IsExcluded(combinedText, config.ExcludedShellKeywords)) return false;
                return true;
            }

            // 2. 除外キーワードチェック
            if (IsExcluded(combinedText, config.ExcludedShellKeywords))
            {
                return false;
            }

            // 3. 親項目全体が OFF の場合は除外
            if (config.ItemVisibilityState.TryGetValue(parentLabel, out bool isParentEnabled) && !isParentEnabled)
            {
                return false;
            }

            // 4. すべて自動表示が ON の場合
            if (config.ShowAllShellItems) return true;

            // 5. 優先抽出キーワードチェック
            if (!string.IsNullOrWhiteSpace(config.CustomShellKeywords))
            {
                var keywords = config.CustomShellKeywords.Split(new[] { ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var kw in keywords)
                {
                    if (combinedText.Contains(kw.ToLowerInvariant()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static string GetSmartGlyph(string text, string? verb = null)
        {
            var vendor = FindMatchingVendorRule(text, verb);
            if (vendor != null) return vendor.Glyph;

            string lower = text.ToLowerInvariant();
            if (lower.Contains("検索") || lower.Contains("search") || lower.Contains("find") || lower.Contains("google")) return "\uE721";
            if (lower.Contains("共有") || lower.Contains("share") || lower.Contains("送る") || lower.Contains("send")) return "\uE72D";
            if (lower.Contains("印刷") || lower.Contains("print")) return "\uE749";
            if (lower.Contains("背景") || lower.Contains("background") || lower.Contains("wallpaper")) return "\uE7F4";
            if (lower.Contains("編集") || lower.Contains("edit") || lower.Contains("作成") || lower.Contains("create") || lower.Contains("draw") || lower.Contains("designer")) return "\uE70F";
            if (lower.Contains("フォト") || lower.Contains("photo") || lower.Contains("image") || lower.Contains("画像") || lower.Contains("ペイント")) return "\uEB9F";
            if (lower.Contains("回転") || lower.Contains("rotate")) return "\uE7AD";
            if (lower.Contains("圧縮") || lower.Contains("解凍") || lower.Contains("展開") || lower.Contains("zip") || lower.Contains("archive") || lower.Contains("extract")) return "\uE8B7";
            if (lower.Contains("スキャン") || lower.Contains("scan") || lower.Contains("defender") || lower.Contains("security") || lower.Contains("ウイルス")) return "\uE8B8";
            if (lower.Contains("ターミナル") || lower.Contains("terminal") || lower.Contains("cmd") || lower.Contains("powershell") || lower.Contains("bash")) return "\uE756";
            if (lower.Contains("コピー") || lower.Contains("copy")) return "\uE8C8";
            if (lower.Contains("削除") || lower.Contains("delete") || lower.Contains("ゴミ箱")) return "\uE74D";
            if (lower.Contains("名前") || lower.Contains("rename")) return "\uE8AC";
            if (lower.Contains("設定") || lower.Contains("setting") || lower.Contains("config") || lower.Contains("option")) return "\uE713";

            return "\uE712";
        }

        public static string? FindArchiverExe(string menuLabel)
        {
            string lower = menuLabel.ToLowerInvariant();

            if (lower.Contains("peazip"))
                return FindExe("peazip.exe",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\peazip.exe",
                    GetProgramFilesPaths(@"PeaZip\peazip.exe"));

            if (lower.Contains("7-zip") || lower.Contains("7zip"))
                return FindExe("7zFM.exe",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\7zFM.exe",
                    GetProgramFilesPaths(@"7-Zip\7zFM.exe"));

            if (lower.Contains("winrar"))
                return FindExe("WinRAR.exe",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe",
                    GetProgramFilesPaths(@"WinRAR\WinRAR.exe"));

            if (lower.Contains("bandizip"))
                return FindExe("Bandizip.exe",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Bandizip.exe",
                    GetProgramFilesPaths(@"Bandizip\Bandizip.exe"));

            return null;
        }

        private static string[] GetProgramFilesPaths(string subPath)
        {
            var list = new List<string>();

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf)) list.Add(Path.Combine(pf, subPath));

            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86)) list.Add(Path.Combine(pf86, subPath));

            string? pfw64 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(pfw64)) list.Add(Path.Combine(pfw64, subPath));

            string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            list.Add(Path.Combine(sysDrive, "Program Files", subPath));
            list.Add(Path.Combine(sysDrive, "Program Files (x86)", subPath));

            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string? FindExe(string exeName, string regPath, params string[] fallbackPaths)
        {
            // 1. HKCU App Paths
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regPath);
                var val = key?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(val) && File.Exists(val)) return val;
            }
            catch { }

            // 2. HKLM App Paths
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                var val = key?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(val) && File.Exists(val)) return val;
            }
            catch { }

            // 3. Fallback paths (Program Files etc.)
            foreach (var p in fallbackPaths)
                if (File.Exists(p)) return p;

            // 4. PATH environment variable
            try
            {
                var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in envPath.Split(';'))
                {
                    var trimmed = dir.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    var full = Path.Combine(trimmed, exeName);
                    if (File.Exists(full)) return full;
                }
            }
            catch { }

            return null;
        }
    }
}
