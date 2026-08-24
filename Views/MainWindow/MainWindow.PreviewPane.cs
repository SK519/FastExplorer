using System;
using System.Linq;
using FastExplorer.Models;
using Microsoft.UI.Xaml;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Preview & Quick Properties Pane

        public bool IsPreviewPaneVisible
        {
            get => PreviewPane?.Visibility == Visibility.Visible;
            set
            {
                if (PreviewPane != null && PreviewSplitter != null && PreviewPaneColumn != null && PreviewSplitterColumn != null)
                {
                    if (value)
                    {
                        PreviewPaneColumn.MinWidth = 220;
                        PreviewPaneColumn.Width = new GridLength(300, GridUnitType.Pixel);
                        PreviewSplitterColumn.Width = GridLength.Auto;
                        PreviewPane.Visibility = Visibility.Visible;
                        PreviewSplitter.Visibility = Visibility.Visible;
                        UpdatePreviewPane();
                    }
                    else
                    {
                        PreviewPaneColumn.MinWidth = 0;
                        PreviewPaneColumn.Width = new GridLength(0, GridUnitType.Pixel);
                        PreviewSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                        PreviewPane.Visibility = Visibility.Collapsed;
                        PreviewSplitter.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private void TogglePreviewPane_Click(object sender, RoutedEventArgs e)
        {
            IsPreviewPaneVisible = !IsPreviewPaneVisible;
        }

        private void ClosePreviewPane_Click(object sender, RoutedEventArgs e)
        {
            IsPreviewPaneVisible = false;
        }

        public void UpdatePreviewPane()
        {
            if (PreviewPane == null || PreviewPane.Visibility == Visibility.Collapsed) return;

            var selectedItems = ActiveListControl?.SelectedItems?.OfType<FileItem>().ToList() ?? [];
            PreviewPane.UpdatePreview(selectedItems, CurrentTab?.CurrentPath);
        }

        #endregion
    }
}
