using System;
using System.Linq;
using FastExplorer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        private enum ResizeColumnTarget
        {
            None,
            Name,
            Date,
            Type,
            Size
        }

        private ResizeColumnTarget _resizingColumn = ResizeColumnTarget.None;
        private double _resizeStartX;
        private double _resizeStartWidth;

        private PointerEventHandler? _globalResizeMovedHandler;
        private PointerEventHandler? _globalResizeReleasedHandler;

        public void InitializeColumnResize()
        {
            _globalResizeMovedHandler = new PointerEventHandler(GlobalResize_PointerMoved);
            _globalResizeReleasedHandler = new PointerEventHandler(GlobalResize_PointerReleased);

            if (FileListHeader != null)
            {
                FileListHeader.SortByNameRequested += (s, e) => SortByName_Click(s, e);
                FileListHeader.SortByDateRequested += (s, e) => SortByDate_Click(s, e);
                FileListHeader.SortByTypeRequested += (s, e) => SortByType_Click(s, e);
                FileListHeader.SortBySizeRequested += (s, e) => SortBySize_Click(s, e);

                FileListHeader.ResizeHandleNamePointerPressed += (s, e) => StartColumnResize(ResizeColumnTarget.Name, FileListHeader.ContainerName, e);
                FileListHeader.ResizeHandleDatePointerPressed += (s, e) => StartColumnResize(ResizeColumnTarget.Date, FileListHeader.ContainerDate, e);
                FileListHeader.ResizeHandleTypePointerPressed += (s, e) => StartColumnResize(ResizeColumnTarget.Type, FileListHeader.ContainerType, e);
                FileListHeader.ResizeHandleSizePointerPressed += (s, e) => StartColumnResize(ResizeColumnTarget.Size, FileListHeader.ContainerSize, e);

                FileListHeader.SelectAllRequested += isChecked =>
                {
                    if (isChecked)
                    {
                        ActiveListControl?.SelectAll();
                    }
                    else
                    {
                        ActiveListControl?.SelectedItems.Clear();
                    }
                };

                FileListHeader.ResizeHandleNameDoubleTapped += (s, e) => ResizeHandleName_DoubleTapped(s, e);
                FileListHeader.ResizeHandleDateDoubleTapped += (s, e) => ResizeHandleDate_DoubleTapped(s, e);
                FileListHeader.ResizeHandleTypeDoubleTapped += (s, e) => ResizeHandleType_DoubleTapped(s, e);
                FileListHeader.ResizeHandleSizeDoubleTapped += (s, e) => ResizeHandleSize_DoubleTapped(s, e);
            }
        }

        private void StartColumnResize(ResizeColumnTarget target, FrameworkElement container, PointerRoutedEventArgs e)
        {
            var prop = e.GetCurrentPoint(container).Properties;
            if (!prop.IsLeftButtonPressed) return;

            _resizingColumn = target;
            _resizeStartX = e.GetCurrentPoint(null).Position.X;
            _resizeStartWidth = double.IsNaN(container.Width) || container.Width <= 0 ? container.ActualWidth : container.Width;
            if (_resizeStartWidth <= 0)
            {
                _resizeStartWidth = target switch
                {
                    ResizeColumnTarget.Name => ColumnLayout.NameWidth,
                    ResizeColumnTarget.Date => ColumnLayout.DateWidth,
                    ResizeColumnTarget.Type => ColumnLayout.TypeWidth,
                    ResizeColumnTarget.Size => ColumnLayout.SizeWidth,
                    _ => 100
                };
            }

            if (_globalResizeMovedHandler != null)
            {
                RootGrid.AddHandler(UIElement.PointerMovedEvent, _globalResizeMovedHandler, true);
            }
            if (_globalResizeReleasedHandler != null)
            {
                RootGrid.AddHandler(UIElement.PointerReleasedEvent, _globalResizeReleasedHandler, true);
                RootGrid.AddHandler(UIElement.PointerCaptureLostEvent, _globalResizeReleasedHandler, true);
            }

            if (senderElement(target) is UIElement el)
            {
                el.CapturePointer(e.Pointer);
            }
            e.Handled = true;
        }

        private UIElement? senderElement(ResizeColumnTarget target) => target switch
        {
            ResizeColumnTarget.Name => FileListHeader?.HandleName,
            ResizeColumnTarget.Date => FileListHeader?.HandleDate,
            ResizeColumnTarget.Type => FileListHeader?.HandleType,
            ResizeColumnTarget.Size => FileListHeader?.HandleSize,
            _ => null
        };

        private void GlobalResize_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_resizingColumn == ResizeColumnTarget.None || FileListHeader == null) return;

            var prop = e.GetCurrentPoint(this.Content).Properties;
            if (!prop.IsLeftButtonPressed)
            {
                EndColumnResize(e);
                return;
            }

            double currentX = e.GetCurrentPoint(null).Position.X;
            double deltaX = currentX - _resizeStartX;
            double newWidth = Math.Max(50, _resizeStartWidth + deltaX);

            switch (_resizingColumn)
            {
                case ResizeColumnTarget.Name:
                    newWidth = Math.Max(80, newWidth);
                    ColumnLayout.NameWidth = newWidth;
                    FileListHeader.ContainerName.Width = newWidth;
                    break;
                case ResizeColumnTarget.Date:
                    newWidth = Math.Max(80, newWidth);
                    ColumnLayout.DateWidth = newWidth;
                    FileListHeader.ContainerDate.Width = newWidth;
                    break;
                case ResizeColumnTarget.Type:
                    newWidth = Math.Max(60, newWidth);
                    ColumnLayout.TypeWidth = newWidth;
                    FileListHeader.ContainerType.Width = newWidth;
                    break;
                case ResizeColumnTarget.Size:
                    newWidth = Math.Max(50, newWidth);
                    ColumnLayout.SizeWidth = newWidth;
                    FileListHeader.ContainerSize.Width = newWidth;
                    break;
            }

            ColumnLayout.NotifyChanged();
            RefreshAllItemsColumnWidths();
            e.Handled = true;
        }

        private void GlobalResize_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndColumnResize(e);
        }

        private void EndColumnResize(PointerRoutedEventArgs e)
        {
            if (_resizingColumn != ResizeColumnTarget.None)
            {
                if (senderElement(_resizingColumn) is UIElement el)
                {
                    try { el.ReleasePointerCapture(e.Pointer); } catch { }
                }
                if (_globalResizeMovedHandler != null)
                {
                    RootGrid.RemoveHandler(UIElement.PointerMovedEvent, _globalResizeMovedHandler);
                }
                if (_globalResizeReleasedHandler != null)
                {
                    RootGrid.RemoveHandler(UIElement.PointerReleasedEvent, _globalResizeReleasedHandler);
                    RootGrid.RemoveHandler(UIElement.PointerCaptureLostEvent, _globalResizeReleasedHandler);
                }
                _resizingColumn = ResizeColumnTarget.None;
                e.Handled = true;
            }
        }

        private void RefreshAllItemsColumnWidths()
        {
            if (CurrentTab?.Items != null)
            {
                foreach (var item in CurrentTab.Items)
                {
                    item.RefreshColumnWidths();
                }
            }
        }

        #region Double Tap Auto Fit

        private void ResizeHandleName_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (CurrentTab?.Items == null || CurrentTab.Items.Count == 0 || FileListHeader == null) return;
            int maxLen = CurrentTab.Items.Max(i => i.Name.Length);
            double autoWidth = Math.Clamp(maxLen * 8.5 + 40, 120, 800);
            ColumnLayout.NameWidth = autoWidth;
            FileListHeader.ContainerName.Width = autoWidth;
            RefreshAllItemsColumnWidths();
            e.Handled = true;
        }

        private void ResizeHandleDate_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (FileListHeader == null) return;
            ColumnLayout.DateWidth = 170;
            FileListHeader.ContainerDate.Width = 170;
            RefreshAllItemsColumnWidths();
            e.Handled = true;
        }

        private void ResizeHandleType_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (FileListHeader == null) return;
            ColumnLayout.TypeWidth = 140;
            FileListHeader.ContainerType.Width = 140;
            RefreshAllItemsColumnWidths();
            e.Handled = true;
        }

        private void ResizeHandleSize_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (FileListHeader == null) return;
            ColumnLayout.SizeWidth = 100;
            FileListHeader.ContainerSize.Width = 100;
            RefreshAllItemsColumnWidths();
            e.Handled = true;
        }

        #endregion
    }
}
