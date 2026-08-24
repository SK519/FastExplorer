using System;
using System.Collections.Generic;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    public static class TypedPathsService
    {
        public const int MaxHistoryCount = 20;

        public static IReadOnlyList<string> GetHistory()
        {
            return ConfigService.Current.TypedPaths ?? new List<string>();
        }

        public static void AddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var list = ConfigService.Current.TypedPaths;
            if (list == null)
            {
                list = new List<string>();
                ConfigService.Current.TypedPaths = list;
            }

            string trimmed = path.Trim();

            // 既存の同一パス（大文字小文字無視）を削除
            list.RemoveAll(p => string.Equals(p.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

            // 先頭に挿入
            list.Insert(0, trimmed);

            // 最大件数を超えた分を切り詰め
            if (list.Count > MaxHistoryCount)
            {
                list.RemoveRange(MaxHistoryCount, list.Count - MaxHistoryCount);
            }

            ConfigService.Save();
        }

        public static void RemovePath(string path)
        {
            var list = ConfigService.Current.TypedPaths;
            if (list != null && list.RemoveAll(p => string.Equals(p.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
            {
                ConfigService.Save();
            }
        }

        public static void Clear()
        {
            var list = ConfigService.Current.TypedPaths;
            if (list != null && list.Count > 0)
            {
                list.Clear();
                ConfigService.Save();
            }
        }
    }
}
