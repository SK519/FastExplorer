namespace FastExplorer.Services
{
    public class QuickAccessFolderItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string GlyphIcon { get; set; } = "\uE8B7";
        public string Subtitle { get; set; } = "ローカルに保存済み";
        public bool IsPinned { get; set; } = true;
    }
}
