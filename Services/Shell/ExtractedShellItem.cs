using System.Collections.Generic;

namespace FastExplorer.Services
{
    public class ExtractedShellItem
    {
        public uint CommandId { get; set; }
        public string Text { get; set; } = string.Empty;
        public string CleanText { get; set; } = string.Empty;
        public string Glyph { get; set; } = "\uE712";
        public string? Verb { get; set; }
        /// <summary>null でなければ InvokeCommand ではなくこのパスのプロセスを直接起動する</summary>
        public string? DirectLaunchPath { get; set; }
        /// <summary>DirectLaunchPath 起動時の追加引数テンプレート（{files} がファイルパスに置換）</summary>
        public string? DirectLaunchArgs { get; set; }
        /// <summary>サブメニュー構造を持つ親アイテムかどうか</summary>
        public bool IsSubmenu { get; set; }
        /// <summary>サブメニューの子アイテム群</summary>
        public List<ExtractedShellItem> Children { get; set; } = new();
    }
}
