using System;

namespace FastExplorer.Models
{
    public class TypedPathSuggestionItem
    {
        public string Path { get; set; } = string.Empty;
        public string Glyph { get; set; } = "\uE81C"; // 履歴時計アイコン (Segoe Fluent Icons)

        public TypedPathSuggestionItem()
        {
        }

        public TypedPathSuggestionItem(string path)
        {
            Path = path;
            Glyph = "\uE81C";
        }
    }
}
