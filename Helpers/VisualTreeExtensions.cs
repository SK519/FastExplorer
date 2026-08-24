using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer.Helpers
{
    public static class VisualTreeExtensions
    {
        public static T? FindParent<T>(this DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T target) return target;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        public static T? FindDescendant<T>(this DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target) return target;
                var descendant = FindDescendant<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }
    }
}
