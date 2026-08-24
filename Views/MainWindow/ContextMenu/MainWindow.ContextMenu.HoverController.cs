using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu & Submenu Hover Controller

        private sealed class OpenSubmenuEntry
        {
            public int Level { get; init; }
            public FrameworkElement TriggerElement { get; init; } = null!;
            public MenuFlyout Flyout { get; init; } = null!;
        }

        private readonly List<OpenSubmenuEntry> _activeSubmenuChain = [];
        private DispatcherTimer? _submenuOpenTimer;
        private DispatcherTimer? _submenuCloseCheckTimer;
        private int _outOfBoundsTicks;
        private int _hoverOnOtherItemTicks;
        private FrameworkElement? _pendingTriggerElement;
        private MenuFlyout? _pendingChildFlyout;
        private int _pendingLevel;

        private Win32Interop.SUBCLASSPROC? _contextMenuSubclassProc;
        private bool _isSubclassInstalled;
        private Win32Interop.LowLevelMouseProc? _mouseHookProc;
        private nint _hMouseHook = 0;

        private void InitSubmenuHoverTimers()
        {
            if (_submenuOpenTimer == null)
            {
                _submenuOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
                _submenuOpenTimer.Tick += (s, e) =>
                {
                    _submenuOpenTimer.Stop();
                    if (_pendingChildFlyout != null && _pendingTriggerElement != null)
                    {
                        ShowSubmenuImmediateInternal(_pendingLevel, _pendingTriggerElement, _pendingChildFlyout);
                    }
                };
            }

            if (_submenuCloseCheckTimer == null)
            {
                _submenuCloseCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _submenuCloseCheckTimer.Tick += SubmenuCloseCheckTimer_Tick;
            }
        }

        private void CloseSubmenusFromLevel(int level)
        {
            _hoverOnOtherItemTicks = 0;
            for (int i = _activeSubmenuChain.Count - 1; i >= 0; i--)
            {
                if (_activeSubmenuChain[i].Level >= level)
                {
                    var entry = _activeSubmenuChain[i];
                    _activeSubmenuChain.RemoveAt(i);
                    try
                    {
                        entry.Flyout.Hide();
                    }
                    catch { }
                }
            }

            if (_activeSubmenuChain.Count == 0)
            {
                _submenuCloseCheckTimer?.Stop();
                _outOfBoundsTicks = 0;
            }
        }

        private void HideActiveSubmenu()
        {
            CancelSubmenuOpen();
            _hoverOnOtherItemTicks = 0;
            CloseSubmenusFromLevel(1);
        }

        private void CloseAllSubmenus()
        {
            HideActiveSubmenu();
        }

        private void CancelSubmenuOpen()
        {
            _submenuOpenTimer?.Stop();
            _pendingTriggerElement = null;
            _pendingChildFlyout = null;
            _pendingLevel = 0;
        }

        private void CancelPendingSubmenuOpen() => CancelSubmenuOpen();

        private void InstallMouseHook()
        {
            if (_hMouseHook != 0) return;
            _mouseHookProc = LowLevelMouseHookCallback;
            _hMouseHook = Win32Interop.SetWindowsHookExW(Win32Interop.WH_MOUSE_LL, _mouseHookProc, 0, 0);
        }

        private void UninstallMouseHook()
        {
            if (_hMouseHook != 0)
            {
                Win32Interop.UnhookWindowsHookEx(_hMouseHook);
                _hMouseHook = 0;
                _mouseHookProc = null;
            }
        }

        private nint LowLevelMouseHookCallback(int nCode, nuint wParam, nint lParam)
        {
            if (nCode >= 0 && (uint)wParam == Win32Interop.WM_MOUSEWHEEL)
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<Win32Interop.MSLLHOOKSTRUCT>(lParam);
                    var cursorPos = hookStruct.pt;
                    short delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);

                    if (ContextMenuItemsPanel != null && TryGetElementScreenRect(ContextMenuItemsPanel, out var parentMenuRect))
                    {
                        // マウスがメインメニュー（親メニュー）領域内にある場合
                        if (cursorPos.X >= parentMenuRect.Left - 15 && cursorPos.X <= parentMenuRect.Right + 15 &&
                            cursorPos.Y >= parentMenuRect.Top - 60 && cursorPos.Y <= parentMenuRect.Bottom + 15)
                        {
                            // 開いているサブメニューを即座に閉じる
                            if (_activeSubmenuChain.Count > 0)
                            {
                                HideActiveSubmenu();
                            }

                            // メインメニューの ScrollViewer をスクロール
                            if (ContextMenuScrollViewer != null && delta != 0)
                            {
                                double newOffset = ContextMenuScrollViewer.VerticalOffset - (delta * 0.4);
                                ContextMenuScrollViewer.ChangeView(null, newOffset, null, true);
                            }

                            return (nint)1;
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return Win32Interop.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
        }

        private void EnsureWindowSubclass()
        {
            if (_isSubclassInstalled || WindowHandle == 0) return;
            _contextMenuSubclassProc = ContextMenuWndProc;
            Win32Interop.SetWindowSubclass(WindowHandle, _contextMenuSubclassProc, 101, 0);
            _isSubclassInstalled = true;
        }

        private nint ContextMenuWndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
        {
            if (uMsg == Win32Interop.WM_MOUSEWHEEL)
            {
                if (Win32Interop.GetCursorPos(out var cursorPos) && ContextMenuItemsPanel != null && TryGetElementScreenRect(ContextMenuItemsPanel, out var parentMenuRect))
                {
                    // マウスがメインメニュー（親メニュー）領域内にある場合
                    if (cursorPos.X >= parentMenuRect.Left - 15 && cursorPos.X <= parentMenuRect.Right + 15 &&
                        cursorPos.Y >= parentMenuRect.Top - 60 && cursorPos.Y <= parentMenuRect.Bottom + 15)
                    {
                        // 開いているサブメニューを即座に閉じる
                        if (_activeSubmenuChain.Count > 0)
                        {
                            HideActiveSubmenu();
                        }

                        // メインメニューの ScrollViewer をスクロール
                        short delta = (short)((wParam >> 16) & 0xFFFF);
                        if (ContextMenuScrollViewer != null && delta != 0)
                        {
                            double newOffset = ContextMenuScrollViewer.VerticalOffset - (delta * 0.4);
                            ContextMenuScrollViewer.ChangeView(null, newOffset, null, true);
                        }
                    }
                }
            }

            return Win32Interop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void ContextMenuScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // スクロール操作時は開いているサブメニューを即座に閉じる
            HideActiveSubmenu();
        }

        private void ScheduleSubmenuOpen(int level, FrameworkElement trigger, MenuFlyout childFlyout)
        {
            InitSubmenuHoverTimers();

            // すでにこのレベルで同じフライアウトが開いている場合
            if (_activeSubmenuChain.Any(e => e.Level == level && e.Flyout == childFlyout))
            {
                // これより深いレベルのサブメニューがあれば閉じる
                CloseSubmenusFromLevel(level + 1);
                CancelSubmenuOpen();
                return;
            }

            // すでに同じトリガーでホバー待機中なら何もしない（ちらつき防止）
            if (_pendingLevel == level && _pendingTriggerElement == trigger && _pendingChildFlyout == childFlyout)
            {
                return;
            }

            // このレベル以上の開いているサブメニューを閉じる
            CloseSubmenusFromLevel(level);

            _pendingLevel = level;
            _pendingTriggerElement = trigger;
            _pendingChildFlyout = childFlyout;

            _submenuOpenTimer?.Stop();
            _submenuOpenTimer?.Start();
        }

        private void ShowSubmenuImmediateInternal(int level, FrameworkElement trigger, MenuFlyout childFlyout)
        {
            CancelSubmenuOpen();
            CloseSubmenusFromLevel(level);

            _activeSubmenuChain.Add(new OpenSubmenuEntry
            {
                Level = level,
                TriggerElement = trigger,
                Flyout = childFlyout
            });

            _outOfBoundsTicks = 0;

            childFlyout.ShowAt(trigger, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop
            });

            _submenuCloseCheckTimer?.Start();
        }

        private void SubmenuCloseCheckTimer_Tick(object? sender, object e)
        {
            if (_activeSubmenuChain.Count == 0)
            {
                _submenuCloseCheckTimer?.Stop();
                _outOfBoundsTicks = 0;
                return;
            }

            if (!Win32Interop.GetCursorPos(out var cursorPos))
            {
                return;
            }

            if (!TryGetElementScreenRect(ContextMenuItemsPanel, out var parentMenuRect))
            {
                return;
            }

            // 1. 開いている各サブメニュー階層の判定（最も深いレベルから順に評価）
            // サブメニュー内にカーソルがある場合は親メニューの判定よりも優先する（重なりによる誤クローズ防止）
            for (int lvl = _activeSubmenuChain.Count; lvl >= 1; lvl--)
            {
                var entry = _activeSubmenuChain[lvl - 1];
                if (TryGetElementScreenRect(entry.TriggerElement, out var tRect))
                {
                    // トリガー要素自体の真上にある場合
                    if (cursorPos.X >= tRect.Left - 2 && cursorPos.X <= tRect.Right + 2 &&
                        cursorPos.Y >= tRect.Top - 2 && cursorPos.Y <= tRect.Bottom + 2)
                    {
                        _outOfBoundsTicks = 0;
                        CloseSubmenusFromLevel(entry.Level + 1);
                        return;
                    }

                    // このトリガーから展開されたサブメニュー領域
                    bool inSubmenuBounds = false;

                    // 右展開 (上方向の展開にも対応できるよう subTop の余裕を大きめに確保)
                    double subRightMin = tRect.Right - 40;
                    double subRightMax = tRect.Right + 450;
                    double subTop = Math.Max(0, tRect.Top - 350);
                    double subBottom = tRect.Bottom + 650;
                    if (cursorPos.X >= subRightMin && cursorPos.X <= subRightMax &&
                        cursorPos.Y >= subTop && cursorPos.Y <= subBottom)
                    {
                        inSubmenuBounds = true;
                    }

                    // 左展開
                    double subLeftMin = tRect.Left - 450;
                    double subLeftMax = tRect.Left + 40;
                    if (cursorPos.X >= subLeftMin && cursorPos.X <= subLeftMax &&
                        cursorPos.Y >= subTop && cursorPos.Y <= subBottom)
                    {
                        inSubmenuBounds = true;
                    }

                    if (inSubmenuBounds)
                    {
                        _outOfBoundsTicks = 0;
                        _hoverOnOtherItemTicks = 0;

                        // カーソルがこの階層（Level lvl）内にいる場合:
                        // もしさらに深い階層（Level lvl+1）が開いているなら、その子トリガーの上にカーソルがあるか確認
                        if (_activeSubmenuChain.Count > lvl)
                        {
                            var childEntry = _activeSubmenuChain[lvl];
                            if (TryGetElementScreenRect(childEntry.TriggerElement, out var childTRect))
                            {
                                bool onChildTrigger = cursorPos.X >= childTRect.Left - 2 && cursorPos.X <= childTRect.Right + 2 &&
                                                      cursorPos.Y >= childTRect.Top - 2 && cursorPos.Y <= childTRect.Bottom + 2;
                                if (!onChildTrigger)
                                {
                                    CloseSubmenusFromLevel(lvl + 1);
                                }
                            }
                            else
                            {
                                CloseSubmenusFromLevel(lvl + 1);
                            }
                        }
                        return;
                    }
                }
            }

            // 2. メインメニュー（親メニュー）領域内の判定
            if (cursorPos.X >= parentMenuRect.Left - 6 && cursorPos.X <= parentMenuRect.Right + 6 &&
                cursorPos.Y >= parentMenuRect.Top - 50 && cursorPos.Y <= parentMenuRect.Bottom + 6)
            {
                // ヘッダーバー上の場合
                if (cursorPos.Y < parentMenuRect.Top)
                {
                    HideActiveSubmenu();
                    return;
                }

                var hoveredItem = FindMenuItemUnderCursor(cursorPos);
                var activeLevel1Trigger = _activeSubmenuChain.FirstOrDefault(x => x.Level == 1)?.TriggerElement;

                if (hoveredItem != null)
                {
                    if (hoveredItem == activeLevel1Trigger)
                    {
                        // メインメニューのアクティブトリガー上：Level 2以降の深いサブメニューがあれば閉じる
                        _outOfBoundsTicks = 0;
                        _hoverOnOtherItemTicks = 0;
                        CloseSubmenusFromLevel(2);
                        return;
                    }
                    else
                    {
                        // メインメニューの別の項目上
                        if (hoveredItem is Button btn && btn.Tag is MenuFlyout otherSubFlyout)
                        {
                            ScheduleSubmenuOpen(1, btn, otherSubFlyout);
                            _hoverOnOtherItemTicks = 0;
                        }
                        else
                        {
                            // 斜め移動中の誤クローズを防止するため約240ms（4フレーム）滞在した場合のみ閉じる
                            _hoverOnOtherItemTicks++;
                            if (_hoverOnOtherItemTicks >= 4)
                            {
                                HideActiveSubmenu();
                                _hoverOnOtherItemTicks = 0;
                            }
                        }
                        return;
                    }
                }
            }

            // 3. いずれのメニュー領域外にもカーソルが出た場合（約300msの猶予を持たせて閉じる）
            _outOfBoundsTicks++;
            if (_outOfBoundsTicks >= 5)
            {
                HideActiveSubmenu();
            }
        }

        private FrameworkElement? FindMenuItemUnderCursor(Win32Interop.POINT cursorPos)
        {
            if (ContextMenuItemsPanel == null) return null;

            foreach (var child in ContextMenuItemsPanel.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                {
                    if (TryGetElementScreenRect(fe, out var rect))
                    {
                        if (cursorPos.X >= rect.Left - 2 && cursorPos.X <= rect.Right + 2 &&
                            cursorPos.Y >= rect.Top && cursorPos.Y <= rect.Bottom)
                        {
                            return fe;
                        }
                    }
                }
            }
            return null;
        }

        private bool TryGetElementScreenRect(FrameworkElement fe, out Windows.Foundation.Rect rect)
        {
            rect = Windows.Foundation.Rect.Empty;
            if (fe == null || !fe.IsLoaded || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return false;
            if (this.Content?.XamlRoot == null) return false;

            try
            {
                var transform = fe.TransformToVisual(this.Content);
                var r = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                double scale = this.Content.XamlRoot.RasterizationScale;
                if (scale <= 0) scale = 1.0;

                var origin = new Win32Interop.POINT { X = 0, Y = 0 };
                Win32Interop.ClientToScreen(this.WindowHandle, ref origin);

                rect = new Windows.Foundation.Rect(
                    origin.X + (r.X * scale),
                    origin.Y + (r.Y * scale),
                    r.Width * scale,
                    r.Height * scale
                );
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ContextMenuHeader_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            CancelSubmenuOpen();
            HideActiveSubmenu();
        }

        #endregion
    }
}
