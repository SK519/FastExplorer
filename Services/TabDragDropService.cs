using FastExplorer.Models;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer.Services
{
    public static class TabDragDropService
    {
        public const string TabDataFormat = "FastExplorer.TabViewItem";

        public static MainWindow? SourceWindow { get; set; }
        public static TabViewItem? DraggedTabViewItem { get; set; }
        public static NavigationTabItem? DraggedNavTab { get; set; }
        public static bool IsSettingsTab { get; set; }

        public static bool IsDragging => DraggedTabViewItem != null;

        public static void SetDraggingTab(MainWindow sourceWindow, TabViewItem tabViewItem)
        {
            SourceWindow = sourceWindow;
            DraggedTabViewItem = tabViewItem;
            DraggedNavTab = tabViewItem.DataContext as NavigationTabItem;
            IsSettingsTab = tabViewItem.Tag as string == "SettingsTab";
        }

        public static void Clear()
        {
            SourceWindow = null;
            DraggedTabViewItem = null;
            DraggedNavTab = null;
            IsSettingsTab = false;
        }
    }
}
