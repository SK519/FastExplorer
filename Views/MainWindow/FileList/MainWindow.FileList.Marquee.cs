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
        private Windows.Foundation.Point _marqueeCurrentPoint;
        private double _marqueeStartVerticalOffset;
        private double _marqueeStartHorizontalOffset;
        private double _marqueeAutoScrollSpeed;
        private DispatcherTimer? _marqueeAutoScrollTimer;
        private HashSet<FileItem> _marqueeInitialSelection = [];

        private ScrollViewer? GetActiveScrollViewer()
        {
            return ActiveListControl?.FindDescendant<ScrollViewer>();
        }

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

                var sv = GetActiveScrollViewer();
                _marqueeStartVerticalOffset = sv?.VerticalOffset ?? 0;
                _marqueeStartHorizontalOffset = sv?.HorizontalOffset ?? 0;

                _isMarqueeSelecting = true;
                _marqueeStartPoint = e.GetCurrentPoint(SelectionCanvas).Position;
                _marqueeCurrentPoint = _marqueeStartPoint;
                _marqueeAutoScrollSpeed = 0;
                _marqueeInitialSelection = isCtrl ? ActiveListControl.SelectedItems.OfType<FileItem>().ToHashSet() : [];

                try
                {
                    FileListContainer.CapturePointer(e.Pointer);
                }
                catch { }

                SelectionBox.Width = 0;
                SelectionBox.Height = 0;
                SelectionBox.Visibility = Visibility.Collapsed;

                StartMarqueeAutoScrollTimer();
            }
        }

        private void FileListContainer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isMarqueeSelecting || CurrentTab == null) return;

            var props = e.GetCurrentPoint(FileListContainer).Properties;
            if (!props.IsLeftButtonPressed)
            {
                EndMarqueeSelection(e.Pointer);
                return;
            }

            _marqueeCurrentPoint = e.GetCurrentPoint(SelectionCanvas).Position;

            // 上下端にポインターが近づいたときの自動スクロール速度計算
            var containerPt = e.GetCurrentPoint(FileListContainer).Position;
            double height = FileListContainer.ActualHeight;
            double scrollZone = 44.0;

            if (containerPt.Y < scrollZone)
            {
                double ratio = Math.Clamp((scrollZone - containerPt.Y) / scrollZone, 0.1, 3.0);
                _marqueeAutoScrollSpeed = -Math.Max(5.0, ratio * 22.0);
            }
            else if (containerPt.Y > height - scrollZone && height > 0)
            {
                double ratio = Math.Clamp((containerPt.Y - (height - scrollZone)) / scrollZone, 0.1, 3.0);
                _marqueeAutoScrollSpeed = Math.Max(5.0, ratio * 22.0);
            }
            else
            {
                _marqueeAutoScrollSpeed = 0;
            }

            UpdateMarqueeSelection();
        }

        private void StartMarqueeAutoScrollTimer()
        {
            if (_marqueeAutoScrollTimer == null)
            {
                _marqueeAutoScrollTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16) // ~60fps
                };
                _marqueeAutoScrollTimer.Tick += MarqueeAutoScrollTimer_Tick;
            }
            _marqueeAutoScrollTimer.Start();
        }

        private void StopMarqueeAutoScrollTimer()
        {
            _marqueeAutoScrollTimer?.Stop();
            _marqueeAutoScrollSpeed = 0;
        }

        private void MarqueeAutoScrollTimer_Tick(object? sender, object e)
        {
            if (!_isMarqueeSelecting)
            {
                StopMarqueeAutoScrollTimer();
                return;
            }

            if (Math.Abs(_marqueeAutoScrollSpeed) > 0.1)
            {
                var sv = GetActiveScrollViewer();
                if (sv != null && sv.ScrollableHeight > 0)
                {
                    double targetOffset = Math.Clamp(sv.VerticalOffset + _marqueeAutoScrollSpeed, 0, sv.ScrollableHeight);
                    if (Math.Abs(targetOffset - sv.VerticalOffset) > 0.1)
                    {
                        sv.ChangeView(null, targetOffset, null, true);
                        UpdateMarqueeSelection();
                    }
                }
            }
        }

        public void UpdateMarqueeSelection()
        {
            if (!_isMarqueeSelecting || CurrentTab == null) return;

            var sv = GetActiveScrollViewer();
            double currentVOffset = sv?.VerticalOffset ?? 0;
            double currentHOffset = sv?.HorizontalOffset ?? 0;

            // スクロールに追従して始点の Canvas 座標をリアルタイム補正
            double adjustedStartX = _marqueeStartPoint.X - (currentHOffset - _marqueeStartHorizontalOffset);
            double adjustedStartY = _marqueeStartPoint.Y - (currentVOffset - _marqueeStartVerticalOffset);

            double curX = _marqueeCurrentPoint.X;
            double curY = _marqueeCurrentPoint.Y;

            double x = Math.Min(adjustedStartX, curX);
            double y = Math.Min(adjustedStartY, curY);
            double w = Math.Abs(curX - adjustedStartX);
            double h = Math.Abs(curY - adjustedStartY);

            if (w > 3 || h > 3)
            {
                SelectionBox.Visibility = Visibility.Visible;
                Canvas.SetLeft(SelectionBox, Math.Max(0, x));
                Canvas.SetTop(SelectionBox, Math.Max(0, y));
                SelectionBox.Width = w;
                SelectionBox.Height = h;

                var marqueeRect = new Windows.Foundation.Rect(Math.Max(0, x), Math.Max(0, y), w, h);
                var activeList = ActiveListControl;
                if (activeList == null || CurrentTab?.Items == null || CurrentTab.Items.Count == 0) return;

                int firstIdx = 0;
                int lastIdx = CurrentTab.Items.Count - 1;
                if (activeList.ItemsPanelRoot is ItemsStackPanel stackPanel && stackPanel.FirstVisibleIndex >= 0)
                {
                    firstIdx = Math.Max(0, stackPanel.FirstVisibleIndex - 2);
                    lastIdx = Math.Min(CurrentTab.Items.Count - 1, stackPanel.LastVisibleIndex + 2);
                }
                else if (activeList.ItemsPanelRoot is ItemsWrapGrid wrapGrid && wrapGrid.FirstVisibleIndex >= 0)
                {
                    firstIdx = Math.Max(0, wrapGrid.FirstVisibleIndex - 4);
                    lastIdx = Math.Min(CurrentTab.Items.Count - 1, wrapGrid.LastVisibleIndex + 4);
                }

                for (int i = firstIdx; i <= lastIdx; i++)
                {
                    var item = CurrentTab.Items[i];
                    if (activeList.ContainerFromIndex(i) is FrameworkElement container && container.ActualHeight > 0)
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

                // ドラッグ中はステータスバーのみ軽量更新し、プレビューや全ボタンの再描画はドラッグ終了時 (EndMarqueeSelection) に集約
                int selCount = activeList.SelectedItems.Count;
                if (StatusBar != null)
                {
                    StatusBar.StatusText = selCount > 0 ? $"{selCount} 個の項目を選択" : (CurrentTab?.StatusText ?? "");
                }
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
            // ポインターキャプチャが子要素（ListViewItem等）へ移動しても、マウス左ボタンが押下されている間は選択を継続
            var pt = e.GetCurrentPoint(FileListContainer);
            if (!pt.Properties.IsLeftButtonPressed)
            {
                EndMarqueeSelection(e.Pointer);
            }
        }

        private void EndMarqueeSelection(Pointer? pointer)
        {
            if (_isMarqueeSelecting)
            {
                _isMarqueeSelecting = false;
                StopMarqueeAutoScrollTimer();

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
