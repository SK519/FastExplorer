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

        #region Splitters Drag Resize (Preview & Sidebar)

        private bool _isResizingPreviewPane = false;
        private double _previewResizeStartPointX;
        private double _previewResizeStartWidth;

        private void PreviewSplitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement el)
            {
                _isResizingPreviewPane = true;
                el.CapturePointer(e.Pointer);
                var pt = e.GetCurrentPoint(this.Content);
                _previewResizeStartPointX = pt.Position.X;
                _previewResizeStartWidth = PreviewPaneColumn.ActualWidth > 0 ? PreviewPaneColumn.ActualWidth : 280;
                e.Handled = true;
            }
        }

        private void PreviewSplitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizingPreviewPane)
            {
                var pt = e.GetCurrentPoint(this.Content);
                double delta = _previewResizeStartPointX - pt.Position.X;
                double newWidth = Math.Clamp(_previewResizeStartWidth + delta, 180, 600);
                PreviewPaneColumn.Width = new GridLength(newWidth, GridUnitType.Pixel);
                e.Handled = true;
            }
        }

        private void PreviewSplitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizingPreviewPane)
            {
                _isResizingPreviewPane = false;
                if (sender is UIElement el)
                {
                    el.ReleasePointerCapture(e.Pointer);
                }
                e.Handled = true;
            }
        }

        private bool _isResizingSidebar = false;
        private double _sidebarResizeStartPointX;
        private double _sidebarResizeStartWidth;

        private void SidebarSplitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement el)
            {
                _isResizingSidebar = true;
                el.CapturePointer(e.Pointer);
                var pt = e.GetCurrentPoint(this.Content);
                _sidebarResizeStartPointX = pt.Position.X;
                _sidebarResizeStartWidth = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : 220;
                e.Handled = true;
            }
        }

        private void SidebarSplitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizingSidebar)
            {
                var pt = e.GetCurrentPoint(this.Content);
                double delta = pt.Position.X - _sidebarResizeStartPointX;
                double newWidth = Math.Clamp(_sidebarResizeStartWidth + delta, 140, 500);
                SidebarColumn.Width = new GridLength(newWidth, GridUnitType.Pixel);
                e.Handled = true;
            }
        }

        private void SidebarSplitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizingSidebar)
            {
                _isResizingSidebar = false;
                if (sender is UIElement el)
                {
                    el.ReleasePointerCapture(e.Pointer);
                }
                e.Handled = true;
            }
        }

        #endregion
    }
}
