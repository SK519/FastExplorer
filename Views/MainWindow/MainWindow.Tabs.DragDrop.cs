using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer.DragDrop;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Tab Drag, Move, Tear-off & Docking

        private void MainTabView_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
        {
            var tabItem = _lastPressedTabViewItem
                          ?? TabDragDropService.DraggedTabViewItem
                          ?? (MainTabView.SelectedItem as TabViewItem)
                          ?? args.Tab
                          ?? (args.Item as TabViewItem);

            if (tabItem != null)
            {
                TabDragDropService.SetDraggingTab(this, tabItem);
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                args.Data.SetData(TabDragDropService.TabDataFormat, "tab");
            }
        }

        private void MainTabView_TabStripDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
        {
            if (TabDragDropService.IsDragging)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
            }
            else if (IsDataPackageSupported(e.DataView))
            {
                e.Handled = true; // WinUI TabView のタブ並び替え・ドッキングアニメーション誤発火を防止
                var tabItem = GetTabViewItemAtPosition(e.GetPosition(MainTabView), e);
                if (tabItem?.DataContext is NavigationTabItem navTab && Directory.Exists(navTab.CurrentPath))
                {
                    bool isCtrl = e.Modifiers.HasFlag(DragDropModifiers.Control);
                    var op = isCtrl ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                    e.AcceptedOperation = op;
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.IsGlyphVisible = true;
                    e.DragUIOverride.Caption = $"{navTab.Header} に{(op == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move ? "移動" : "コピー")}";
                }
                else
                {
                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                }
            }
        }

        private TabViewItem CreateCleanTabViewItem(TabViewItem sourceItem, NavigationTabItem? navTab)
        {
            var cleanItem = new TabViewItem
            {
                Header = sourceItem.Header,
                IconSource = sourceItem.IconSource ?? new FontIconSource { Glyph = "\uE8B7" },
                Tag = sourceItem.Tag,
                DataContext = (sourceItem.Tag as string == "SettingsTab")
                    ? (sourceItem.DataContext ?? new Views.Settings.SettingsControl())
                    : (navTab ?? (sourceItem.DataContext as NavigationTabItem))
            };
            return cleanItem;
        }

        private async void MainTabView_TabStripDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
        {
            if (TabDragDropService.IsDragging)
            {
                var draggedItem = TabDragDropService.DraggedTabViewItem;
                var sourceWindow = TabDragDropService.SourceWindow;
                if (draggedItem == null) return;

                int targetIndex = CalculateTabDropIndex(e);

                if (sourceWindow == this)
                {
                    // ウィンドウ内のタブ並び替え
                    int oldIndex = MainTabView.TabItems.IndexOf(draggedItem);
                    if (oldIndex != -1 && oldIndex != targetIndex)
                    {
                        MainTabView.TabItems.RemoveAt(oldIndex);
                        if (targetIndex >= MainTabView.TabItems.Count)
                        {
                            MainTabView.TabItems.Add(draggedItem);
                        }
                        else
                        {
                            MainTabView.TabItems.Insert(Math.Max(0, targetIndex), draggedItem);
                        }
                        MainTabView.SelectedItem = draggedItem;
                    }
                    SyncTabsOrder();
                }
                else if (sourceWindow != null)
                {
                    // ウィンドウ間のタブ結合（移動）
                    var navTab = TabDragDropService.DraggedNavTab;
                    sourceWindow.DetachTab(draggedItem, disposeModel: false);
                    var cleanItem = CreateCleanTabViewItem(draggedItem, navTab);
                    this.AttachTab(cleanItem, navTab, targetIndex);
                    this.MainTabView.UpdateLayout();
                    this.Activate();
                }

                TabDragDropService.Clear();
            }
            else if (IsDataPackageSupported(e.DataView))
            {
                e.Handled = true;
                var tabItem = GetTabViewItemAtPosition(e.GetPosition(MainTabView), e);
                if (tabItem?.DataContext is NavigationTabItem navTab && Directory.Exists(navTab.CurrentPath))
                {
                    var def = e.GetDeferral();
                    try
                    {
                        var paths = await ExtractPathsFromDataPackageAsync(e.DataView);
                        if (paths.Count > 0)
                        {
                            bool isMove = (e.AcceptedOperation == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) ||
                                          e.Modifiers.HasFlag(DragDropModifiers.Shift);

                            bool success = await PerformFileTransferWithDialogAsync(paths, navTab.CurrentPath, isMove);
                            if (success && (CurrentTab == navTab || CurrentTab?.CurrentPath.Equals(navTab.CurrentPath, StringComparison.OrdinalIgnoreCase) == true))
                            {
                                CurrentTab.Refresh();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[TabStripDrop] File drop error: {ex.Message}");
                    }
                    finally
                    {
                        def.Complete();
                    }
                }
            }
        }

        private TabViewItem? GetTabViewItemAtPosition(Windows.Foundation.Point dropPos, Microsoft.UI.Xaml.DragEventArgs? e = null)
        {
            if (e != null)
            {
                try
                {
                    var hitElements = VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), MainTabView);
                    foreach (var el in hitElements)
                    {
                        if (el is DependencyObject dep)
                        {
                            var item = dep.FindParent<TabViewItem>();
                            if (item != null && MainTabView.TabItems.Contains(item))
                            {
                                return item;
                            }
                        }
                    }
                }
                catch { }
            }

            for (int i = 0; i < MainTabView.TabItems.Count; i++)
            {
                var tabItem = (MainTabView.TabItems[i] as TabViewItem) ?? (MainTabView.ContainerFromIndex(i) as TabViewItem);
                if (tabItem != null)
                {
                    try
                    {
                        var bounds = tabItem.TransformToVisual(MainTabView).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, tabItem.ActualWidth, tabItem.ActualHeight));
                        if (bounds.Contains(dropPos))
                        {
                            return tabItem;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            return null;
        }

        private int CalculateTabDropIndex(Microsoft.UI.Xaml.DragEventArgs e)
        {
            var dropPos = e.GetPosition(MainTabView);
            for (int i = 0; i < MainTabView.TabItems.Count; i++)
            {
                var tabItem = (MainTabView.TabItems[i] as TabViewItem) ?? (MainTabView.ContainerFromIndex(i) as TabViewItem);
                if (tabItem != null)
                {
                    try
                    {
                        var bounds = tabItem.TransformToVisual(MainTabView).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, tabItem.ActualWidth, tabItem.ActualHeight));
                        if (dropPos.X < bounds.X + bounds.Width / 2)
                        {
                            return i;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            return MainTabView.TabItems.Count;
        }

        private void MainTabView_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
        {
            // ウィンドウ内にタブが1つしかない場合は分離しない
            if (MainTabView.TabItems.Count <= 1)
            {
                TabDragDropService.Clear();
                _lastPressedTabViewItem = null;
                return;
            }

            // 1. 直前にクリックされたタブ、またはドラッグ保存タブを最優先参照
            var tabItem = _lastPressedTabViewItem
                          ?? TabDragDropService.DraggedTabViewItem
                          ?? (MainTabView.SelectedItem as TabViewItem)
                          ?? args.Tab
                          ?? (args.Item as TabViewItem);

            if (tabItem == null && args.Item != null)
            {
                tabItem = MainTabView.ContainerFromItem(args.Item) as TabViewItem;
            }

            var navTab = (tabItem?.DataContext as NavigationTabItem) ?? TabDragDropService.DraggedNavTab;
            if (tabItem == null && navTab != null)
            {
                tabItem = MainTabView.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.DataContext == navTab);
            }

            if (tabItem == null)
            {
                TabDragDropService.Clear();
                _lastPressedTabViewItem = null;
                return;
            }

            // MainTabView.TabItems に含まれる実インスタンスを確定
            if (!MainTabView.TabItems.Contains(tabItem))
            {
                var matchedItem = MainTabView.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t == tabItem || (navTab != null && t.DataContext == navTab));
                if (matchedItem != null)
                {
                    tabItem = matchedItem;
                }
            }

            // 元ウィンドウからタブをデタッチ
            DetachTab(tabItem, disposeModel: false);

            // マウスカーソル位置を取得
            Win32Interop.GetCursorPos(out var cursorPos);

            // 新しいウィンドウを生成して追跡登録
            var newWindow = new MainWindow(createInitialTab: false);
            App.RegisterWindow(newWindow);

            var cleanItem = CreateCleanTabViewItem(tabItem, navTab);
            newWindow.AttachTab(cleanItem, navTab);

            // XAML アイランドと DWM を完全に初期化するため、Move 前に Activate を実行
            newWindow.Activate();
            newWindow.ApplyTheme(ConfigService.Current.Ui.Theme);

            // 新しいウィンドウの位置をカーソル位置に設定
            try
            {
                var appWindow = newWindow.AppWindow;
                appWindow.Move(new Windows.Graphics.PointInt32(
                    Math.Max(0, cursorPos.X - 120),
                    Math.Max(0, cursorPos.Y - 24)));
            }
            catch
            {
                // ignored
            }

            Win32Interop.ForceForegroundWindow(newWindow.WindowHandle);

            TabDragDropService.Clear();
            _lastPressedTabViewItem = null;
        }

        private void MainTabView_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
        {
            TabDragDropService.Clear();
        }

        #endregion
    }
}
