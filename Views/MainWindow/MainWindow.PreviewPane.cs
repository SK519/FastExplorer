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
                        try
                        {
                            if (AppWindow != null && AppWindow.Size.Width < 760)
                            {
                                int newWidth = Math.Max(760, AppWindow.Size.Width + 280);
                                AppWindow.Resize(new Windows.Graphics.SizeInt32(newWidth, AppWindow.Size.Height));
                            }
                        }
                        catch { }

                        PreviewPaneColumn.MinWidth = 180;
                        PreviewPaneColumn.Width = new GridLength(280, GridUnitType.Pixel);
                        PreviewSplitterColumn.Width = GridLength.Auto;
                        PreviewPane.Visibility = Visibility.Visible;
                        PreviewSplitter.Visibility = Visibility.Visible;
                        ActionToolbar?.UpdatePreviewButtonVisual(true);
                        UpdatePreviewPane();
                    }
                    else
                    {
                        PreviewPaneColumn.MinWidth = 0;
                        PreviewPaneColumn.Width = new GridLength(0, GridUnitType.Pixel);
                        PreviewSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                        PreviewPane.Visibility = Visibility.Collapsed;
                        PreviewSplitter.Visibility = Visibility.Collapsed;
                        ActionToolbar?.UpdatePreviewButtonVisual(false);
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

            var selectedItems = GetCurrentlySelectedItems();
            PreviewPane.UpdatePreview(selectedItems, CurrentTab?.CurrentPath);
        }

        #endregion
    }
}
