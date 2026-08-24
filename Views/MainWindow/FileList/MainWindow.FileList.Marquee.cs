using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Helpers;
using FastExplorer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Marquee Selection (Rubber-Band Selection Rectangle)

        private bool _isMarqueeSelecting = false;
        private Windows.Foundation.Point _marqueeStartPoint;
        private HashSet<FileItem> _marqueeInitialSelection = [];

        private void InitializeMarqueeSelection()
        {
            FileListContainer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(FileListContainer_PointerPressed), true);
            FileListContainer.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(FileListContainer_PointerMoved), true);
            FileListContainer.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FileListContainer_PointerReleased), true);
            FileListContainer.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(FileListContainer_PointerCanceled), true);
            FileListContainer.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(FileListContainer_PointerCaptureLost), true);
        }

        private void FileListContainer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ptrPt = e.GetCurrentPoint(FileListContainer);
            if (!ptrPt.Properties.IsLeftButtonPressed) return;

            // スクロールバーやボタン等の操作時は除外
            if (e.OriginalSource is FrameworkElement fe)
            {
                if (fe.FindParent<ScrollBar>() != null || fe.FindParent<Button>() != null) return;
            }

            // アイテム上でのクリックか判定
            bool clickedOnItem = false;
            if (e.OriginalSource is DependencyObject dep)
            {
                var lvi = dep.FindParent<ListViewItem>();
                var gvi = dep.FindParent<GridViewItem>();
                if (lvi != null || gvi != null)
                {
                    clickedOnItem = true;
                }
            }

            bool isCtrl = IsCtrlPressed();
            bool isShift = IsShiftPressed();

            // アイテム以外の余白領域・空白行でのドラッグ開始
            if (!clickedOnItem)
            {
                if (!isCtrl && !isShift)
                {
                    ClearAllSelections();
                }

                _isMarqueeSelecting = true;
                _marqueeStartPoint = e.GetCurrentPoint(SelectionCanvas).Position;
                _marqueeInitialSelection = isCtrl ? ActiveListControl.SelectedItems.OfType<FileItem>().ToHashSet() : [];
                FileListContainer.CapturePointer(e.Pointer);

                SelectionBox.Width = 0;
                SelectionBox.Height = 0;
                SelectionBox.Visibility = Visibility.Collapsed;
            }
        }

        private void FileListContainer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isMarqueeSelecting || CurrentTab == null) return;

            var curPt = e.GetCurrentPoint(SelectionCanvas).Position;
            double x = Math.Min(_marqueeStartPoint.X, curPt.X);
            double y = Math.Min(_marqueeStartPoint.Y, curPt.Y);
            double w = Math.Abs(curPt.X - _marqueeStartPoint.X);
            double h = Math.Abs(curPt.Y - _marqueeStartPoint.Y);

            if (w > 3 || h > 3)
            {
                SelectionBox.Visibility = Visibility.Visible;
                Canvas.SetLeft(SelectionBox, x);
                Canvas.SetTop(SelectionBox, y);
                SelectionBox.Width = w;
                SelectionBox.Height = h;

                var marqueeRect = new Windows.Foundation.Rect(x, y, w, h);
                var activeList = ActiveListControl;

                foreach (var item in CurrentTab.Items)
                {
                    if (activeList.ContainerFromItem(item) is FrameworkElement container && container.ActualHeight > 0)
                    {
                        try
                        {
                            var transform = container.TransformToVisual(SelectionCanvas);
                            var itemBounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));

                            bool intersects = !(itemBounds.Right < marqueeRect.Left ||
                                                itemBounds.Left > marqueeRect.Right ||
                                                itemBounds.Bottom < marqueeRect.Top ||
                                                itemBounds.Top > marqueeRect.Bottom);

                            if (intersects)
                            {
                                if (!activeList.SelectedItems.Contains(item))
                                {
                                    activeList.SelectedItems.Add(item);
                                }
                            }
                            else
                            {
                                if (!_marqueeInitialSelection.Contains(item) && activeList.SelectedItems.Contains(item))
                                {
                                    activeList.SelectedItems.Remove(item);
                                }
                            }
                        }
                        catch { }
                    }
                }

                UpdateActionToolbarButtons();
            }
        }

        private void FileListContainer_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndMarqueeSelection(e.Pointer);
        }

        private void FileListContainer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            EndMarqueeSelection(e.Pointer);
        }

        private void FileListContainer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            EndMarqueeSelection(e.Pointer);
        }

        private void EndMarqueeSelection(Pointer? pointer)
        {
            if (_isMarqueeSelecting)
            {
                _isMarqueeSelecting = false;
                SelectionBox.Visibility = Visibility.Collapsed;
                SelectionBox.Width = 0;
                SelectionBox.Height = 0;
                _marqueeInitialSelection.Clear();

                if (pointer != null)
                {
                    try
                    {
                        FileListContainer.ReleasePointerCapture(pointer);
                    }
                    catch { }
                }

                UpdateActionToolbarButtons();
                UpdatePreviewPane();
            }
        }

        #endregion
    }
}
