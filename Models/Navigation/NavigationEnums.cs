namespace FastExplorer
{
    public enum SortColumn
    {
        Name,
        DateModified,
        FileType,
        Size
    }

    public enum ViewScaleLevel
    {
        Compact = 0,
        Normal = 1,
        Large = 2,
        ExtraLarge = 3
    }

    public enum FolderViewMode
    {
        ExtraLargeIcons,
        LargeIcons,
        MediumIcons,
        SmallIcons,
        List,
        Details,
        Tiles,
        Content
    }
}
