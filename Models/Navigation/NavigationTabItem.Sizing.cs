using System;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
        public void UpdateItemSizes(int size)
        {
            CustomSize = size;
            bool isGrid = ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons;

            foreach (var item in Items)
            {
                ApplySizeToItem(item, size, isGrid, ViewMode);
            }
        }

        public static void ApplySizeToItem(FileItem item, int size, bool isGrid, FolderViewMode mode)
        {
            if (isGrid)
            {
                if (mode == FolderViewMode.SmallIcons)
                {
                    item.ItemIconSize = Math.Clamp(size * 0.35 + 8, 14, 32);
                    item.GlyphFontSize = Math.Max(12, item.ItemIconSize - 2);
                    item.ItemWidth = Math.Clamp(size * 2.5 + 40, 120, 300);
                    item.ItemHeight = Math.Clamp(size * 0.4 + 18, 26, 54);
                    item.ItemFontSize = Math.Clamp(size * 0.03 + 10, 10, 13.5);
                }
                else
                {
                    // Icon Grid Mode (Medium / Large / ExtraLarge)
                    double iconSize = Math.Clamp(size * 0.9, 24, 150);
                    item.ItemIconSize = iconSize;
                    item.GlyphFontSize = Math.Max(18, iconSize * 0.7);
                    item.ItemWidth = Math.Max(iconSize + 36, 72);
                    item.ItemHeight = iconSize + 48;
                    item.ItemFontSize = Math.Clamp(size * 0.025 + 9.5, 10, 14);
                }
            }
            else if (mode == FolderViewMode.List)
            {
                item.ItemIconSize = Math.Clamp(size * 0.25 + 10, 14, 28);
                item.GlyphFontSize = Math.Max(12, item.ItemIconSize - 2);
                item.ItemWidth = Math.Clamp(size * 1.5 + 100, 140, 320);
                item.ItemHeight = Math.Clamp(size * 0.3 + 16, 24, 52);
                item.ItemFontSize = Math.Clamp(size * 0.025 + 10, 10, 13.5);
            }
            else if (mode == FolderViewMode.Tiles)
            {
                item.ItemIconSize = Math.Clamp(size * 0.35 + 16, 24, 56);
                item.GlyphFontSize = Math.Max(18, item.ItemIconSize - 8);
                item.ItemWidth = Math.Clamp(size * 1.8 + 150, 180, 380);
                item.ItemHeight = Math.Clamp(size * 0.4 + 28, 38, 80);
                item.ItemFontSize = Math.Clamp(size * 0.025 + 10.5, 10.5, 14);
            }
            else if (mode == FolderViewMode.Content)
            {
                item.ItemIconSize = Math.Clamp(size * 0.35 + 14, 22, 52);
                item.GlyphFontSize = Math.Max(16, item.ItemIconSize - 6);
                item.ItemWidth = double.NaN; // Stretch
                item.ItemHeight = Math.Clamp(size * 0.35 + 26, 36, 76);
                item.ItemFontSize = Math.Clamp(size * 0.025 + 10.5, 10.5, 14);
            }
            else // Details
            {
                item.ItemIconSize = Math.Clamp(size * 0.2 + 10, 14, 28);
                item.GlyphFontSize = Math.Max(12, item.ItemIconSize - 2);
                item.ItemWidth = double.NaN; // Stretch
                item.ItemHeight = Math.Clamp(size * 0.2 + 18, 26, 52);
                item.ItemFontSize = Math.Clamp(size * 0.025 + 10, 10.5, 13.5);
            }
        }
    }
}
