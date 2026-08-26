using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer.DragDrop;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Tab Management

        public void CreateNewTab(string? initialPath = null, string? selectItemName = null)
        {
            string path = string.IsNullOrEmpty(initialPath)
                ? ConfigService.Current.Startup.DefaultPath
                : initialPath;

            var tab = new NavigationTabItem
            {
                DispatcherQueue = this.DispatcherQueue,
                PendingSelectedItemName = selectItemName
            };

            var tabViewItem = new TabViewItem
            {
                Header = tab.Header,
                IconSource = IconThumbnailService.GetIconSourceForNavigationPath(path)
            };

            AttachTab(tabViewItem, tab);
            tab.NavigateTo(path);
        }

        public void AttachTab(TabViewItem tabViewItem, NavigationTabItem? navTab = null, int insertIndex = -1)
        {
            if (navTab == null && tabViewItem.DataContext is NavigationTabItem dataTab)
            {
                navTab = dataTab;
            }

            if (navTab != null)
            {
                navTab.DispatcherQueue = this.DispatcherQueue;
                tabViewItem.DataContext = navTab;
                HookTabEvents(navTab, tabViewItem);

                if (insertIndex >= 0 && insertIndex < _tabs.Count)
                {
                    _tabs.Insert(insertIndex, navTab);
                }
                else
                {
                    _tabs.Add(navTab);
                }
            }
            else if (tabViewItem.Tag as string == "SettingsTab")
            {
                if (tabViewItem.DataContext == null)
                {
                    tabViewItem.DataContext = new Views.Settings.SettingsControl();
                }
            }

            if (insertIndex >= 0 && insertIndex < MainTabView.TabItems.Count)
            {
                MainTabView.TabItems.Insert(insertIndex, tabViewItem);
            }
            else
            {
                MainTabView.TabItems.Add(tabViewItem);
            }

            SyncTabsOrder();
            MainTabView.SelectedItem = tabViewItem;
            BindCurrentTabToUi();
        }

        private void SyncTabsOrder()
        {
            _tabs.Clear();
            foreach (var item in MainTabView.TabItems.OfType<TabViewItem>())
            {
                if (item.DataContext is NavigationTabItem navTab)
                {
                    _tabs.Add(navTab);
                }
            }
        }

        private void HookTabEvents(NavigationTabItem tab, TabViewItem tabViewItem)
        {
            tab.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NavigationTabItem.Header))
                {
                    tabViewItem.Header = tab.Header;
                }
                if (s == CurrentTab)
                {
                    UpdateToolbarState();
                }
            };

            tab.Navigated += (navTab) =>
            {
                tabViewItem.IconSource = IconThumbnailService.GetIconSourceForNavigationPath(navTab.CurrentPath);

                if (CurrentTab == navTab)
                {
                    AddressBar?.SwitchToBreadcrumbs();
                    AddressBar?.SetBreadcrumbs(navTab.Breadcrumbs);
                    AddressBar?.SetSearchFilterText(navTab.FilterText);
                    ApplyViewMode(navTab.ViewMode, navTab.ViewScale, saveConfig: false);
                    UpdateHomeViewVisibility();
                    UpdateToolbarState();
                    UpdateSelectionVisuals();
                    UpdatePreviewPane();
                }
            };

            tab.ItemSelectionRequested += (navTab, selectName) =>
            {
                if (CurrentTab == navTab && !string.IsNullOrEmpty(selectName))
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        var targetItem = navTab.Items.FirstOrDefault(i => i.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
                        if (targetItem != null)
                        {
                            FileListView?.SelectedItems.Clear();
                            FileListView?.SelectedItems.Add(targetItem);
                            FileListView?.ScrollIntoView(targetItem);

                            FileGridView?.SelectedItems.Clear();
                            FileGridView?.SelectedItems.Add(targetItem);
                            FileGridView?.ScrollIntoView(targetItem);

                            UpdateSelectionVisuals();
                            UpdatePreviewPane();
                        }
                    });
                }
            };
        }

        public void DetachTab(TabViewItem tabViewItem, bool disposeModel = false)
        {
            if (tabViewItem.Tag as string == "SettingsTab")
            {
                SettingsTabHostGrid.Children.Clear();
            }

            int index = MainTabView.TabItems.IndexOf(tabViewItem);
            if (index >= 0)
            {
                MainTabView.TabItems.RemoveAt(index);
            }

            if (tabViewItem.DataContext is NavigationTabItem navTab)
            {
                _tabs.Remove(navTab);
                if (disposeModel)
                {
                    navTab.Dispose();
                }
            }

            SyncTabsOrder();

            if (MainTabView.TabItems.Count == 0)
            {
                this.Close();
                return;
            }

            if (MainTabView.SelectedItem == null && MainTabView.TabItems.Count > 0)
            {
                int newIndex = Math.Min(index, MainTabView.TabItems.Count - 1);
                MainTabView.SelectedIndex = Math.Max(0, newIndex);
            }

            BindCurrentTabToUi();
        }

        public void OpenSettingsTab()
        {
            try
            {
                foreach (var item in MainTabView.TabItems.OfType<TabViewItem>())
                {
                    if (item.Tag as string == "SettingsTab")
                    {
                        MainTabView.SelectedItem = item;
                        BindCurrentTabToUi();
                        if (item.DataContext is Views.Settings.SettingsControl sc)
                        {
                            sc.ReloadSettings();
                        }
                        return;
                    }
                }

                var settingsControl = new Views.Settings.SettingsControl();
                var settingsTabItem = new TabViewItem
                {
                    Header = "設定",
                    IconSource = new FontIconSource { Glyph = "\uE713" },
                    Tag = "SettingsTab",
                    DataContext = settingsControl
                };

                MainTabView.TabItems.Add(settingsTabItem);
                MainTabView.SelectedItem = settingsTabItem;
                BindCurrentTabToUi();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenSettingsTab] Exception: {ex}");
            }
        }

        private void CloseTab(TabViewItem tabViewItem)
        {
            if (MainTabView.TabItems.Count <= 1)
            {
                this.Close();
                return;
            }

            DetachTab(tabViewItem, disposeModel: true);
        }

        private void MainTabView_AddTabButtonClick(TabView sender, object args)
        {
            CreateNewTab();
        }

        private void MainTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is TabViewItem tabItem)
            {
                CloseTab(tabItem);
            }
        }

        private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BindCurrentTabToUi();
        }

        private void BindCurrentTabToUi()
        {
            CancelActiveRenaming();
            if (MainTabView.SelectedItem is TabViewItem tabViewItem)
            {
                if (tabViewItem.Tag as string == "SettingsTab")
                {
                    ExplorerContentGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    SettingsTabHostGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    if (tabViewItem.DataContext is Microsoft.UI.Xaml.UIElement settingsElement)
                    {
                        if (!SettingsTabHostGrid.Children.Contains(settingsElement))
                        {
                            SettingsTabHostGrid.Children.Clear();
                            SettingsTabHostGrid.Children.Add(settingsElement);
                        }
                    }
                    try
                    {
                        if (tabViewItem.DataContext is Views.Settings.SettingsControl sc)
                        {
                            sc.ReloadSettings();
                        }
                    }
                    catch { }

                    AddressBar?.SwitchToBreadcrumbs();
                    AddressBar?.SetBreadcrumbs(new[] { new BreadcrumbItem { Label = "設定", FullPath = "FastExplorer://Settings", Glyph = "\uE713" } });
                    AddressBar?.SetSearchFilterText(string.Empty);
                    if (StatusBar != null) StatusBar.StatusText = "設定";
                    AddressBar?.UpdateNavigationButtons(false, false, false);
                    return;
                }

                ExplorerContentGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                SettingsTabHostGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

                if (tabViewItem.DataContext is NavigationTabItem tab)
                {
                    FileListView.ItemsSource = tab.Items;
                    FileGridView.ItemsSource = tab.Items;
                    AddressBar?.SwitchToBreadcrumbs();
                    AddressBar?.SetBreadcrumbs(tab.Breadcrumbs);
                    AddressBar?.SetSearchFilterText(tab.FilterText);
                    ApplyViewMode(tab.ViewMode, tab.ViewScale, saveConfig: false);
                    UpdateHomeViewVisibility();
                    UpdateCutVisuals();
                    UpdateToolbarState();
                    UpdateSelectionVisuals();
                    UpdatePreviewPane();
                }
            }
        }

        private void UpdateToolbarState()
        {
            if (CurrentTab == null) return;
            AddressBar?.UpdateNavigationButtons(CurrentTab.CanGoBack, CurrentTab.CanGoForward, CurrentTab.CanGoUp);
            if (StatusBar != null) StatusBar.StatusText = CurrentTab.StatusText;
            UpdateActionToolbarButtons();
        }

        #endregion

        #region Tab Drag, Move, Tear-off & Docking

        private void MainTabView_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
        {
            var tabItem = args.Tab ?? (args.Item as TabViewItem);
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
                var tabItem = GetTabViewItemAtPosition(e.GetPosition(MainTabView));
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
            else
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
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
            if (TabDragDropService.IsDragging && TabDragDropService.DraggedTabViewItem != null)
            {
                var sourceWindow = TabDragDropService.SourceWindow;
                var draggedItem = TabDragDropService.DraggedTabViewItem;
                int targetIndex = CalculateTabDropIndex(e);

                if (sourceWindow == this)
                {
                    // 同一ウィンドウ内のドラッグ＆ドロップ（並び替え）
                    int oldIndex = MainTabView.TabItems.IndexOf(draggedItem);
                    if (oldIndex >= 0 && oldIndex != targetIndex)
                    {
                        MainTabView.TabItems.RemoveAt(oldIndex);
                        if (targetIndex > oldIndex) targetIndex--;
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
                var tabItem = GetTabViewItemAtPosition(e.GetPosition(MainTabView));
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

        private TabViewItem? GetTabViewItemAtPosition(Windows.Foundation.Point dropPos)
        {
            for (int i = 0; i < MainTabView.TabItems.Count; i++)
            {
                if (MainTabView.ContainerFromIndex(i) is TabViewItem tabItem)
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
                if (MainTabView.ContainerFromIndex(i) is TabViewItem tabItem)
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
            var tabItem = args.Tab ?? (args.Item as TabViewItem);
            if (tabItem == null) return;

            // ウィンドウ内にタブが1つしかない場合は分離しない
            if (MainTabView.TabItems.Count <= 1)
            {
                TabDragDropService.Clear();
                return;
            }

            var navTab = tabItem.DataContext as NavigationTabItem;

            // 元ウィンドウからタブをデタッチ
            DetachTab(tabItem, disposeModel: false);

            // マウスカーソル位置を取得
            Win32Interop.GetCursorPos(out var cursorPos);

            // 新しいウィンドウを生成してクリーンなタブをアタッチ
            var newWindow = new MainWindow(createInitialTab: false);
            var cleanItem = CreateCleanTabViewItem(tabItem, navTab);
            newWindow.AttachTab(cleanItem, navTab);

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

            newWindow.Activate();
            TabDragDropService.Clear();
        }

        private void MainTabView_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
        {
            TabDragDropService.Clear();
        }

        #endregion
    }
}
