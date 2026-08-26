using System;
using System.Collections.Generic;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        // ドラッグ並び替え用の状態管理
        private readonly List<FrameworkElement> _renderedCards = new();
        private readonly List<string> _renderedKeys = new();
        private readonly Dictionary<FrameworkElement, double> _initialCardTops = new();
        private FrameworkElement? _draggingCard;
        private string? _draggingKey;
        private bool _isDragging = false;
        private int _draggedIndex = -1;
        private int _currentTargetIndex = -1;
        private double _dragStartScrollY;
        private double _dragStartPointerScrollY;
        private double _dragCardTopY;
        private double _dragCardHeight;
        private DispatcherTimer? _autoScrollTimer;
        private double _autoScrollSpeed = 0;
        private Point _lastPointerScrollPos;

        private void InitAutoScrollTimer()
        {
            _autoScrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _autoScrollTimer.Tick += (s, e) =>
            {
                if (!_isDragging || _draggingCard == null || Math.Abs(_autoScrollSpeed) < 0.1) return;

                double oldOffset = MainScrollViewer.VerticalOffset;
                double targetOffset = Math.Max(0, Math.Min(MainScrollViewer.ScrollableHeight, oldOffset + _autoScrollSpeed));

                if (Math.Abs(targetOffset - oldOffset) > 0.01)
                {
                    MainScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
                    UpdateDragPositions();
                }
            };
        }

        private static void EnsureMenuOrderInitialized()
        {
            var config = ConfigService.Current.ShellMenu;
            if (config.MenuOrder == null)
            {
                config.MenuOrder = new List<string>();
            }

            // 旧バージョンの標準機能キーが混ざっていた場合はクリーンアップ
            var builtinKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Open", "OpenWith", "EditWithEditor", "OpenInTerminal", "CopyPath", "ZipOptions", "OsStandard"
            };
            config.MenuOrder.RemoveAll(k => builtinKeys.Contains(k));

            foreach (var key in config.ItemVisibilityState.Keys)
            {
                string rootName = key.Contains(" → ") ? key.Split(new[] { " → " }, 2, StringSplitOptions.None)[0] : key;
                var vendor = ShellMenuFilter.FindMatchingVendorRule(rootName);
                string effectiveKey = (vendor != null && vendor.IsClusterable) ? vendor.DisplayName : rootName;
                if (!config.MenuOrder.Contains(effectiveKey))
                {
                    config.MenuOrder.Add(effectiveKey);
                }
            }
        }

        private int GetItemOrderRank(string key)
        {
            var menuOrder = ConfigService.Current.ShellMenu.MenuOrder;
            int idx = menuOrder.IndexOf(key);
            return idx >= 0 ? idx : int.MaxValue / 2;
        }

        private FrameworkElement CreateDragHandle(FrameworkElement card, string key)
        {
            var handle = new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Padding = new Thickness(4, 6, 10, 6),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                CanDrag = false
            };

            var icon = new FontIcon
            {
                Glyph = "\uE700", // ≡ (GlobalNavigation / Hamburger / Drag Handle)
                FontSize = 14,
                Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray))
            };

            handle.Child = icon;
            ToolTipService.SetToolTip(handle, "ドラッグして順番を並び替え");

            handle.PointerEntered += (s, e) =>
            {
                icon.Foreground = GetThemeBrush("AccentTextFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue));
            };

            handle.PointerExited += (s, e) =>
            {
                if (!_isDragging || _draggingCard != card)
                {
                    icon.Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray));
                }
            };

            handle.PointerPressed += (s, e) =>
            {
                var pt = e.GetCurrentPoint(DetectedItemsContainer);
                var ptInScroll = e.GetCurrentPoint(MainScrollViewer);
                if (pt.Properties.IsLeftButtonPressed)
                {
                    _isDragging = true;
                    _draggingCard = card;
                    _draggingKey = key;
                    _draggedIndex = _renderedCards.IndexOf(card);
                    _currentTargetIndex = _draggedIndex;

                    _dragStartScrollY = MainScrollViewer.VerticalOffset;
                    _dragStartPointerScrollY = ptInScroll.Position.Y;
                    _lastPointerScrollPos = ptInScroll.Position;

                    // 元の位置と高さを記録
                    _initialCardTops.Clear();
                    for (int i = 0; i < _renderedCards.Count; i++)
                    {
                        var c = _renderedCards[i];
                        try
                        {
                            var transform = c.TransformToVisual(DetectedItemsContainer);
                            var topPoint = transform.TransformPoint(new Point(0, 0));
                            double ttY = (c.RenderTransform as TranslateTransform)?.Y ?? 0;
                            _initialCardTops[c] = topPoint.Y - ttY;
                        }
                        catch
                        {
                            _initialCardTops[c] = i * 44.0;
                        }
                    }

                    if (_initialCardTops.TryGetValue(card, out var topY))
                    {
                        _dragCardTopY = topY;
                    }
                    else
                    {
                        _dragCardTopY = pt.Position.Y;
                    }

                    _dragCardHeight = card.ActualHeight + 4.0;

                    card.Opacity = 0.8;
                    Canvas.SetZIndex(card, 1000);
                    handle.CapturePointer(e.Pointer);
                    _autoScrollTimer?.Start();
                    e.Handled = true;
                }
            };

            handle.PointerMoved += (s, e) =>
            {
                if (!_isDragging || _draggingCard == null || _draggingCard != card) return;

                var ptInScroll = e.GetCurrentPoint(MainScrollViewer);
                _lastPointerScrollPos = ptInScroll.Position;

                // オートスクロール領域のチェック (上端・下端 60px ゾーン)
                double scrollH = MainScrollViewer.ActualHeight;
                double edgeZone = 60.0;

                if (ptInScroll.Position.Y < edgeZone)
                {
                    _autoScrollSpeed = -Math.Min(22.0, (edgeZone - ptInScroll.Position.Y) * 0.45);
                }
                else if (ptInScroll.Position.Y > scrollH - edgeZone)
                {
                    _autoScrollSpeed = Math.Min(22.0, (ptInScroll.Position.Y - (scrollH - edgeZone)) * 0.45);
                }
                else
                {
                    _autoScrollSpeed = 0;
                }

                UpdateDragPositions();
                e.Handled = true;
            };

            void EndDrag(PointerRoutedEventArgs e)
            {
                if (_isDragging && _draggingCard == card)
                {
                    _isDragging = false;
                    _autoScrollTimer?.Stop();
                    _autoScrollSpeed = 0;

                    card.Opacity = 1.0;
                    Canvas.SetZIndex(card, 0);
                    icon.Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray));
                    handle.ReleasePointerCapture(e.Pointer);

                    int fromIndex = _draggedIndex;
                    int toIndex = _currentTargetIndex;
                    string movedKey = _draggingKey ?? key;

                    _draggingCard = null;
                    _draggingKey = null;
                    _draggedIndex = -1;
                    _currentTargetIndex = -1;

                    if (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex && fromIndex < _renderedKeys.Count && toIndex < _renderedKeys.Count)
                    {
                        // 順序リストを更新して保存 & 再描画
                        var currentOrder = ConfigService.Current.ShellMenu.MenuOrder;
                        int idxInOrder = currentOrder.IndexOf(movedKey);
                        if (idxInOrder >= 0)
                        {
                            currentOrder.RemoveAt(idxInOrder);
                        }

                        string refKey = _renderedKeys[toIndex];
                        int refIdxInOrder = currentOrder.IndexOf(refKey);

                        if (fromIndex < toIndex)
                        {
                            if (refIdxInOrder >= 0)
                                currentOrder.Insert(refIdxInOrder + 1, movedKey);
                            else
                                currentOrder.Add(movedKey);
                        }
                        else
                        {
                            if (refIdxInOrder >= 0)
                                currentOrder.Insert(refIdxInOrder, movedKey);
                            else
                                currentOrder.Insert(0, movedKey);
                        }

                        ConfigService.Save();
                        RenderDetectedItemsList(SearchFilterBox.Text);
                    }
                    else
                    {
                        // 移動なし: アニメーションで元の位置に戻す
                        for (int i = 0; i < _renderedCards.Count; i++)
                        {
                            if (_renderedCards[i].RenderTransform is TranslateTransform tt)
                            {
                                AnimateTranslateY(tt, 0);
                            }
                        }
                    }

                    e.Handled = true;
                }
            }

            handle.PointerReleased += (s, e) => EndDrag(e);
            handle.PointerCaptureLost += (s, e) => EndDrag(e);

            return handle;
        }

        private void UpdateDragPositions()
        {
            if (!_isDragging || _draggingCard == null) return;

            double currentScrollY = MainScrollViewer.VerticalOffset;
            double deltaScrollY = currentScrollY - _dragStartScrollY;
            double deltaPointerY = _lastPointerScrollPos.Y - _dragStartPointerScrollY;

            // スクロール分＋Pointer移動分の完全追従デルタY
            double deltaY = deltaPointerY + deltaScrollY;

            if (_draggingCard.RenderTransform is TranslateTransform myTt)
            {
                myTt.Y = deltaY;
            }

            double currentMidY = _dragCardTopY + (_draggingCard.ActualHeight / 2.0) + deltaY;

            int newTarget = _draggedIndex;
            for (int i = 0; i < _renderedCards.Count; i++)
            {
                if (i == _draggedIndex) continue;
                var other = _renderedCards[i];
                if (!_initialCardTops.TryGetValue(other, out double otherTop)) continue;
                double otherMidY = otherTop + (other.ActualHeight / 2.0);

                if (_draggedIndex < i && currentMidY > otherMidY)
                {
                    newTarget = i;
                }
                else if (_draggedIndex > i && currentMidY < otherMidY)
                {
                    if (newTarget == _draggedIndex || i < newTarget)
                    {
                        newTarget = i;
                    }
                }
            }

            if (newTarget != _currentTargetIndex)
            {
                _currentTargetIndex = newTarget;

                for (int i = 0; i < _renderedCards.Count; i++)
                {
                    if (i == _draggedIndex) continue;
                    var other = _renderedCards[i];
                    if (other.RenderTransform is not TranslateTransform otherTt) continue;

                    double targetOffset = 0;
                    if (_draggedIndex < _currentTargetIndex)
                    {
                        if (i > _draggedIndex && i <= _currentTargetIndex)
                        {
                            targetOffset = -_dragCardHeight;
                        }
                    }
                    else if (_draggedIndex > _currentTargetIndex)
                    {
                        if (i >= _currentTargetIndex && i < _draggedIndex)
                        {
                            targetOffset = _dragCardHeight;
                        }
                    }

                    AnimateTranslateY(otherTt, targetOffset);
                }
            }
        }

        private static void AnimateTranslateY(TranslateTransform tt, double toValue)
        {
            if (Math.Abs(tt.Y - toValue) < 0.1) return;

            var anim = new DoubleAnimation
            {
                To = toValue,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var sb = new Storyboard();
            sb.Children.Add(anim);
            Storyboard.SetTarget(anim, tt);
            Storyboard.SetTargetProperty(anim, "Y");
            sb.Begin();
        }
    }
}
