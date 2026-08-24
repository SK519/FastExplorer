using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Models;
using Windows.System;

namespace FastExplorer.Services
{
    public class ShortcutActionDef
    {
        public string Id { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DefaultKey { get; set; } = string.Empty;
        public string? SecondaryDefaultKey { get; set; }
    }

    public static class ShortcutService
    {
        public const string CategoryFileOps = "ファイル・フォルダー操作";
        public const string CategoryNavigation = "ナビゲーション";
        public const string CategoryTabs = "タブ・ウィンドウ";
        public const string CategoryView = "表示・ツール";

        public static readonly IReadOnlyList<ShortcutActionDef> AllActions = new List<ShortcutActionDef>
        {
            // ファイル・フォルダー操作
            new() { Id = "NewFolder", Category = CategoryFileOps, Name = "新規フォルダー作成", Description = "現在のフォルダー内に新規フォルダーを作成します", DefaultKey = "Ctrl+Shift+N" },
            new() { Id = "Rename", Category = CategoryFileOps, Name = "名前の変更", Description = "選択した項目の名前を変更します", DefaultKey = "F2" },
            new() { Id = "Delete", Category = CategoryFileOps, Name = "削除 (ゴミ箱)", Description = "選択した項目をごみ箱に移動します", DefaultKey = "Delete" },
            new() { Id = "DeletePermanently", Category = CategoryFileOps, Name = "完全削除", Description = "選択した項目をごみ箱に入れず完全に削除します", DefaultKey = "Shift+Delete" },
            new() { Id = "Copy", Category = CategoryFileOps, Name = "コピー", Description = "選択した項目をクリップボードにコピーします", DefaultKey = "Ctrl+C" },
            new() { Id = "Cut", Category = CategoryFileOps, Name = "切り取り", Description = "選択した項目を切り取ります", DefaultKey = "Ctrl+X" },
            new() { Id = "Paste", Category = CategoryFileOps, Name = "貼り付け", Description = "クリップボードの項目を現在のフォルダーに貼り付けます", DefaultKey = "Ctrl+V" },
            new() { Id = "SelectAll", Category = CategoryFileOps, Name = "すべて選択", Description = "フォルダー内のすべてのファイルとフォルダーを選択します", DefaultKey = "Ctrl+A" },
            new() { Id = "Properties", Category = CategoryFileOps, Name = "プロパティ", Description = "選択項目のプロパティダイアログを開きます", DefaultKey = "Alt+Enter" },

            // ナビゲーション
            new() { Id = "GoUp", Category = CategoryNavigation, Name = "上の階層へ移動", Description = "親フォルダーに移動します", DefaultKey = "Alt+Up", SecondaryDefaultKey = "Backspace" },
            new() { Id = "GoBack", Category = CategoryNavigation, Name = "戻る", Description = "履歴の前フォルダーに戻ります", DefaultKey = "Alt+Left" },
            new() { Id = "GoForward", Category = CategoryNavigation, Name = "進む", Description = "履歴の次フォルダーに進みます", DefaultKey = "Alt+Right" },
            new() { Id = "Refresh", Category = CategoryNavigation, Name = "最新の情報に更新", Description = "現在のフォルダー内容を再読み込みします", DefaultKey = "F5" },
            new() { Id = "Search", Category = CategoryNavigation, Name = "フィルター検索", Description = "フィルター検索ボックスにフォーカスします", DefaultKey = "Ctrl+F" },
            new() { Id = "AddressBar", Category = CategoryNavigation, Name = "アドレス入力バー", Description = "アドレス入力バーにフォーカスします", DefaultKey = "Ctrl+L" },

            // タブ・ウィンドウ
            new() { Id = "NewTab", Category = CategoryTabs, Name = "新規タブを開く", Description = "新しいタブを追加します", DefaultKey = "Ctrl+T" },
            new() { Id = "CloseTab", Category = CategoryTabs, Name = "タブを閉じる", Description = "現在のタブを閉じます", DefaultKey = "Ctrl+W" },
            new() { Id = "NextTab", Category = CategoryTabs, Name = "次のタブへ切替", Description = "右隣のタブに切り替えます", DefaultKey = "Ctrl+Tab" },
            new() { Id = "PrevTab", Category = CategoryTabs, Name = "前のタブへ切替", Description = "左隣のタブに切り替えます", DefaultKey = "Ctrl+Shift+Tab" },

            // 表示・ツール
            new() { Id = "ToggleHiddenFiles", Category = CategoryView, Name = "隠しファイルの表示切替", Description = "隠しファイルの表示/非表示を切り替えます", DefaultKey = "Ctrl+H" },
            new() { Id = "TogglePreview", Category = CategoryView, Name = "プレビュー枠の表示切替", Description = "右側プレビューパネルの表示/非表示を切り替えます", DefaultKey = "Alt+P", SecondaryDefaultKey = "Space" },
            new() { Id = "Settings", Category = CategoryView, Name = "設定を開く", Description = "設定タブを開きます", DefaultKey = "Ctrl+," },
            new() { Id = "ZoomIn", Category = CategoryView, Name = "アイコン拡大 (ズームイン)", Description = "表示アイテムサイズを拡大します", DefaultKey = "Ctrl++" },
            new() { Id = "ZoomOut", Category = CategoryView, Name = "アイコン縮小 (ズームアウト)", Description = "表示アイテムサイズを縮小します", DefaultKey = "Ctrl+-" },
            new() { Id = "ZoomReset", Category = CategoryView, Name = "拡大率リセット", Description = "表示アイテムサイズを規定値に戻します", DefaultKey = "Ctrl+0" }
        };

        public static string GetCurrentShortcut(string actionId)
        {
            var def = AllActions.FirstOrDefault(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
            if (def == null) return string.Empty;

            var customDict = ConfigService.Current.Shortcuts?.CustomShortcuts;
            if (customDict != null && customDict.TryGetValue(actionId, out var custom) && !string.IsNullOrWhiteSpace(custom))
            {
                return NormalizeKeyString(custom);
            }

            return def.DefaultKey;
        }

        public static bool IsCustomized(string actionId)
        {
            var customDict = ConfigService.Current.Shortcuts?.CustomShortcuts;
            return customDict != null && customDict.ContainsKey(actionId) && !string.IsNullOrWhiteSpace(customDict[actionId]);
        }

        public static void SetCustomShortcut(string actionId, string keyCombination)
        {
            if (ConfigService.Current.Shortcuts == null)
            {
                ConfigService.Current.Shortcuts = new ShortcutConfig();
            }

            string normalized = NormalizeKeyString(keyCombination);
            var def = AllActions.FirstOrDefault(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
            if (def != null && string.Equals(normalized, def.DefaultKey, StringComparison.OrdinalIgnoreCase))
            {
                ConfigService.Current.Shortcuts.CustomShortcuts.Remove(actionId);
            }
            else
            {
                ConfigService.Current.Shortcuts.CustomShortcuts[actionId] = normalized;
            }

            ConfigService.Save();
        }

        public static void ResetShortcut(string actionId)
        {
            if (ConfigService.Current.Shortcuts?.CustomShortcuts != null)
            {
                ConfigService.Current.Shortcuts.CustomShortcuts.Remove(actionId);
                ConfigService.Save();
            }
        }

        public static void ResetAll()
        {
            if (ConfigService.Current.Shortcuts != null)
            {
                ConfigService.Current.Shortcuts.CustomShortcuts.Clear();
                ConfigService.Save();
            }
        }

        public static ShortcutActionDef? FindConflict(string actionId, string keyCombination)
        {
            string normalized = NormalizeKeyString(keyCombination);
            if (string.IsNullOrWhiteSpace(normalized)) return null;

            foreach (var action in AllActions)
            {
                if (action.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase)) continue;

                string current = GetCurrentShortcut(action.Id);
                if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return action;
                }
            }
            return null;
        }

        public static bool Matches(string actionId, VirtualKey key, bool isCtrl, bool isShift, bool isAlt)
        {
            var def = AllActions.FirstOrDefault(a => a.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
            if (def == null) return false;

            string current = GetCurrentShortcut(actionId);
            if (MatchesKeyString(current, key, isCtrl, isShift, isAlt))
            {
                return true;
            }

            // セカンダリキー判定（カスタムされていない場合のみ有効）
            if (!IsCustomized(actionId) && !string.IsNullOrEmpty(def.SecondaryDefaultKey))
            {
                if (MatchesKeyString(def.SecondaryDefaultKey, key, isCtrl, isShift, isAlt))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesKeyString(string keyString, VirtualKey key, bool isCtrl, bool isShift, bool isAlt)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return false;

            string normalized = NormalizeKeyString(keyString);
            string inputNormalized = FormatKeyCombination(key, isCtrl, isShift, isAlt);

            if (string.Equals(normalized, inputNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // '+' や '-'、',' などの特殊記号キーのマッピング
            if (normalized.EndsWith("+", StringComparison.Ordinal) || normalized.EndsWith("-", StringComparison.Ordinal) || normalized.EndsWith(",", StringComparison.Ordinal))
            {
                bool reqCtrl = normalized.Contains("Ctrl+", StringComparison.OrdinalIgnoreCase);
                bool reqShift = normalized.Contains("Shift+", StringComparison.OrdinalIgnoreCase);
                bool reqAlt = normalized.Contains("Alt+", StringComparison.OrdinalIgnoreCase);

                if (reqCtrl != isCtrl || reqShift != isShift || reqAlt != isAlt) return false;

                if (normalized.EndsWith("+", StringComparison.Ordinal) && (key == VirtualKey.Add || (int)key == 187))
                    return true;
                if (normalized.EndsWith("-", StringComparison.Ordinal) && (key == VirtualKey.Subtract || (int)key == 189))
                    return true;
                if (normalized.EndsWith(",", StringComparison.Ordinal) && ((int)key == 188))
                    return true;
            }

            return false;
        }

        public static string FormatKeyCombination(VirtualKey key, bool isCtrl, bool isShift, bool isAlt)
        {
            var parts = new List<string>();
            if (isCtrl) parts.Add("Ctrl");
            if (isAlt) parts.Add("Alt");
            if (isShift) parts.Add("Shift");

            string keyName = KeyToDisplayString(key);
            if (!string.IsNullOrEmpty(keyName))
            {
                parts.Add(keyName);
            }

            return string.Join("+", parts);
        }

        public static string NormalizeKeyString(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return string.Empty;

            var tokens = keyString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool isCtrl = false;
            bool isAlt = false;
            bool isShift = false;
            string mainKey = "";

            // 特殊ケース: "Ctrl++" など最後のトークンが "+" の場合
            if (keyString.EndsWith("++", StringComparison.Ordinal) || (keyString.Trim().EndsWith("+", StringComparison.Ordinal) && tokens.Length > 0))
            {
                if (keyString.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) isCtrl = true;
                if (keyString.Contains("Alt", StringComparison.OrdinalIgnoreCase)) isAlt = true;
                if (keyString.Contains("Shift", StringComparison.OrdinalIgnoreCase)) isShift = true;
                mainKey = "+";
            }
            else
            {
                foreach (var t in tokens)
                {
                    if (t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || t.Equals("Control", StringComparison.OrdinalIgnoreCase))
                        isCtrl = true;
                    else if (t.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                        isAlt = true;
                    else if (t.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                        isShift = true;
                    else
                        mainKey = t;
                }
            }

            var parts = new List<string>();
            if (isCtrl) parts.Add("Ctrl");
            if (isAlt) parts.Add("Alt");
            if (isShift) parts.Add("Shift");
            if (!string.IsNullOrEmpty(mainKey)) parts.Add(mainKey);

            return string.Join("+", parts);
        }

        private static string KeyToDisplayString(VirtualKey key)
        {
            return key switch
            {
                VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "",
                VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "",
                VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "", // Alt
                VirtualKey.Add => "+",
                VirtualKey.Subtract => "-",
                VirtualKey.Number0 or VirtualKey.NumberPad0 => "0",
                VirtualKey.Number1 or VirtualKey.NumberPad1 => "1",
                VirtualKey.Number2 or VirtualKey.NumberPad2 => "2",
                VirtualKey.Number3 or VirtualKey.NumberPad3 => "3",
                VirtualKey.Number4 or VirtualKey.NumberPad4 => "4",
                VirtualKey.Number5 or VirtualKey.NumberPad5 => "5",
                VirtualKey.Number6 or VirtualKey.NumberPad6 => "6",
                VirtualKey.Number7 or VirtualKey.NumberPad7 => "7",
                VirtualKey.Number8 or VirtualKey.NumberPad8 => "8",
                VirtualKey.Number9 or VirtualKey.NumberPad9 => "9",
                VirtualKey.Back => "Backspace",
                (VirtualKey)188 => ",",
                (VirtualKey)187 => "+",
                (VirtualKey)189 => "-",
                _ => key.ToString()
            };
        }
    }
}
