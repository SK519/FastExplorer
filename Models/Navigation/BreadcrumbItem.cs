using System;

namespace FastExplorer
{
    public class BreadcrumbItem
    {
        public string Label { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Glyph { get; set; } = "\uE8B7";
    }
}
