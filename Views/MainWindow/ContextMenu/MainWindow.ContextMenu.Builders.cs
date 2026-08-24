using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu Submenu Flyout Builders

        private static Style? _submenuPresenterStyle;

        private static Style GetSubmenuPresenterStyle()
        {
            if (_submenuPresenterStyle != null) return _submenuPresenterStyle;
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"]));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            _submenuPresenterStyle = style;
            return style;
        }

        private MenuFlyout CreateShellSubFlyout(int level, ExtractedShellItem parentItem, IReadOnlyList<string> targetPaths, Style? itemStyle = null)
        {
            var flyout = new MenuFlyout
            {
                MenuFlyoutPresenterStyle = GetSubmenuPresenterStyle()
            };

            flyout.Closed += (s, e) =>
            {
                for (int i = _activeSubmenuChain.Count - 1; i >= 0; i--)
                {
                    if (_activeSubmenuChain[i].Flyout == flyout)
                    {
                        _activeSubmenuChain.RemoveAt(i);
                        break;
                    }
                }
                if (_activeSubmenuChain.Count == 0)
                {
                    _submenuCloseCheckTimer?.Stop();
                    _outOfBoundsTicks = 0;
                }
            };

            foreach (var child in parentItem.Children)
            {
                var capturedChild = child;
                if (capturedChild.IsSubmenu && capturedChild.Children.Count > 0)
                {
                    var childSubFlyout = CreateShellSubFlyout(level + 1, capturedChild, targetPaths, itemStyle);

                    var mItem = new MenuFlyoutItem
                    {
                        Text = capturedChild.CleanText,
                        Icon = new FontIcon { Glyph = capturedChild.Glyph },
                        KeyboardAcceleratorTextOverride = "›"
                    };

                    mItem.PointerEntered += (s, args) =>
                    {
                        ScheduleSubmenuOpen(level + 1, mItem, childSubFlyout);
                    };

                    mItem.PointerExited += (s, args) =>
                    {
                        if (_pendingChildFlyout == childSubFlyout)
                        {
                            CancelPendingSubmenuOpen();
                        }
                    };

                    mItem.Click += (s, args) =>
                    {
                        if (_activeSubmenuChain.Any(e => e.Level == level + 1 && e.Flyout == childSubFlyout))
                        {
                            CancelPendingSubmenuOpen();
                            return;
                        }
                        ShowSubmenuImmediateInternal(level + 1, mItem, childSubFlyout);
                    };

                    flyout.Items.Add(mItem);
                }
                else
                {
                    var mItem = new MenuFlyoutItem
                    {
                        Text = capturedChild.CleanText,
                        Icon = new FontIcon { Glyph = capturedChild.Glyph }
                    };

                    mItem.PointerEntered += (s, args) =>
                    {
                        // 通常項目にカーソルが来たら下位のサブメニューを閉じる
                        CancelPendingSubmenuOpen();
                        CloseSubmenusFromLevel(level + 1);
                    };

                    mItem.Click += (s, args) =>
                    {
                        string? workingDir = targetPaths.Count > 0 ? (Directory.Exists(targetPaths[0]) ? targetPaths[0] : Path.GetDirectoryName(targetPaths[0])) : null;
                        bool invoked = _activeShellSession?.InvokeCommand(capturedChild, workingDir) ?? false;
                        CloseAllSubmenus();
                        ItemContextMenu.Hide();
                        if (!invoked)
                        {
                            ShellContextMenuService.InvokeShellCommand(WindowHandle, targetPaths, capturedChild);
                        }
                    };

                    flyout.Items.Add(mItem);
                }
            }

            return flyout;
        }

        private MenuFlyout CreateCompressionSubFlyout(int level, Style? itemStyle = null)
        {
            var flyout = new MenuFlyout
            {
                MenuFlyoutPresenterStyle = GetSubmenuPresenterStyle()
            };

            flyout.Closed += (s, e) =>
            {
                for (int i = _activeSubmenuChain.Count - 1; i >= 0; i--)
                {
                    if (_activeSubmenuChain[i].Flyout == flyout)
                    {
                        _activeSubmenuChain.RemoveAt(i);
                        break;
                    }
                }
                if (_activeSubmenuChain.Count == 0)
                {
                    _submenuCloseCheckTimer?.Stop();
                    _outOfBoundsTicks = 0;
                }
            };

            // --- ZIP セクション ---
            var zipHeader = new MenuFlyoutItem
            {
                Text = "ZIP 形式 (.zip)",
                Icon = new FontIcon { Glyph = "\uE8F1" },
                IsEnabled = false
            };
            zipHeader.PointerEntered += (s, args) =>
            {
                CancelPendingSubmenuOpen();
                CloseSubmenusFromLevel(level + 1);
            };
            flyout.Items.Add(zipHeader);

            AddCompressionLevelItem(flyout, "  💎 最高圧縮 (Ultra)", ArchiveFormat.Zip, ArchiveCompressionLevel.Ultra, level);
            AddCompressionLevelItem(flyout, "  ⚖️ 標準 (Normal)", ArchiveFormat.Zip, ArchiveCompressionLevel.Normal, level);
            AddCompressionLevelItem(flyout, "  ⚡ 高速 (Fast)", ArchiveFormat.Zip, ArchiveCompressionLevel.Fast, level);
            AddCompressionLevelItem(flyout, "  📦 無圧縮 (Store)", ArchiveFormat.Zip, ArchiveCompressionLevel.Store, level);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // --- 7-Zip セクション ---
            var sevenZipHeader = new MenuFlyoutItem
            {
                Text = "7-Zip 形式 (.7z)",
                Icon = new FontIcon { Glyph = "\uF126" },
                IsEnabled = false
            };
            sevenZipHeader.PointerEntered += (s, args) =>
            {
                CancelPendingSubmenuOpen();
                CloseSubmenusFromLevel(level + 1);
            };
            flyout.Items.Add(sevenZipHeader);

            AddCompressionLevelItem(flyout, "  💎 最高圧縮 (Ultra)", ArchiveFormat.SevenZip, ArchiveCompressionLevel.Ultra, level);
            AddCompressionLevelItem(flyout, "  ⚖️ 標準 (Normal)", ArchiveFormat.SevenZip, ArchiveCompressionLevel.Normal, level);
            AddCompressionLevelItem(flyout, "  ⚡ 高速 (Fast)", ArchiveFormat.SevenZip, ArchiveCompressionLevel.Fast, level);
            AddCompressionLevelItem(flyout, "  📦 無圧縮 (Store)", ArchiveFormat.SevenZip, ArchiveCompressionLevel.Store, level);

            return flyout;
        }

        private MenuFlyout CreateOpenWithSubFlyout(int level, string targetPath, IReadOnlyList<string>? targetPaths = null, Style? itemStyle = null)
        {
            var flyout = new MenuFlyout
            {
                MenuFlyoutPresenterStyle = GetSubmenuPresenterStyle()
            };

            flyout.Closed += (s, e) =>
            {
                for (int i = _activeSubmenuChain.Count - 1; i >= 0; i--)
                {
                    if (_activeSubmenuChain[i].Flyout == flyout)
                    {
                        _activeSubmenuChain.RemoveAt(i);
                        break;
                    }
                }
                if (_activeSubmenuChain.Count == 0)
                {
                    _submenuCloseCheckTimer?.Stop();
                    _outOfBoundsTicks = 0;
                }
            };

            var paths = (targetPaths != null && targetPaths.Count > 0) ? targetPaths : new[] { targetPath };
            var apps = OpenWithService.GetOpenWithApps(targetPath);

            foreach (var app in apps)
            {
                var capturedApp = app;
                var item = new MenuFlyoutItem
                {
                    Text = capturedApp.DisplayName
                };

                SetOpenWithItemIcon(item, capturedApp);

                item.PointerEntered += (s, args) =>
                {
                    CancelPendingSubmenuOpen();
                    CloseSubmenusFromLevel(level + 1);
                };

                item.Click += (s, args) =>
                {
                    CloseAllSubmenus();
                    ItemContextMenu.Hide();
                    if (paths.Count > 1)
                    {
                        OpenWithService.LaunchWithApp(capturedApp, paths);
                    }
                    else
                    {
                        OpenWithService.LaunchWithApp(capturedApp, targetPath);
                    }
                };

                flyout.Items.Add(item);
            }

            if (apps.Count > 0)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            // Microsoft Store を検索
            string ext = Path.GetExtension(targetPath);
            if (!string.IsNullOrEmpty(ext))
            {
                var storeItem = new MenuFlyoutItem
                {
                    Text = "Microsoft Store を検索する",
                    Icon = new FontIcon { Glyph = "\uE719" }
                };

                storeItem.PointerEntered += (s, args) =>
                {
                    CancelPendingSubmenuOpen();
                    CloseSubmenusFromLevel(level + 1);
                };

                storeItem.Click += (s, args) =>
                {
                    CloseAllSubmenus();
                    ItemContextMenu.Hide();
                    OpenWithService.SearchMicrosoftStore(targetPath);
                };

                flyout.Items.Add(storeItem);
            }

            // 別のプログラムを選択
            var chooseOtherItem = new MenuFlyoutItem
            {
                Text = "別のプログラムを選択",
                Icon = new FontIcon { Glyph = "\uE7AC" }
            };

            chooseOtherItem.PointerEntered += (s, args) =>
            {
                CancelPendingSubmenuOpen();
                CloseSubmenusFromLevel(level + 1);
            };

            chooseOtherItem.Click += (s, args) =>
            {
                CloseAllSubmenus();
                ItemContextMenu.Hide();
                FileOperationService.OpenWithDialog(targetPath);
            };

            flyout.Items.Add(chooseOtherItem);

            return flyout;
        }

        private async void SetOpenWithItemIcon(MenuFlyoutItem menuItem, OpenWithAppInfo app)
        {
            if (app.IconBitmap != null)
            {
                try
                {
                    var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
                    await source.SetBitmapAsync(app.IconBitmap);
                    menuItem.Icon = new ImageIcon { Source = source };
                    return;
                }
                catch { }
            }

            menuItem.Icon = new FontIcon { Glyph = "\uE7AC" };
        }

        private void AddCompressionLevelItem(MenuFlyout flyout, string text, ArchiveFormat format, ArchiveCompressionLevel compLevel, int level)
        {
            var item = new MenuFlyoutItem
            {
                Text = text
            };

            item.PointerEntered += (s, args) =>
            {
                CancelPendingSubmenuOpen();
                CloseSubmenusFromLevel(level + 1);
            };

            item.Click += async (s, args) =>
            {
                CloseAllSubmenus();
                ItemContextMenu.Hide();
                await PerformCompressAsync(format, compLevel);
            };

            flyout.Items.Add(item);
        }

        private static List<ExtractedShellItem> SortExtractedItems(List<ExtractedShellItem> items, List<string> menuOrder)
        {
            if (items == null || items.Count == 0) return new List<ExtractedShellItem>();
            if (menuOrder == null || menuOrder.Count == 0) return items;

            int GetRank(string name)
            {
                int idx = menuOrder.IndexOf(name);
                return idx >= 0 ? idx : 10000;
            }

            return items.OrderBy(i => GetRank(i.CleanText)).ToList();
        }

        #endregion
    }
}
