using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        private TabViewItem? _lastPressedTabViewItem;
        private readonly Stack<string> _recentlyClosedTabs = new();

        public void RestoreRecentlyClosedTab()
        {
            if (_recentlyClosedTabs.TryPop(out string? path) && !string.IsNullOrEmpty(path))
            {
                CreateNewTab(path);
            }
        }

        public void AttachTab(TabViewItem tabViewItem, NavigationTabItem? navTab = null, int insertIndex = -1)
        {
            if (navTab == null && tabViewItem.DataContext is NavigationTabItem dataTab)
            {
                navTab = dataTab;
            }

            tabViewItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
            {
                if (s is TabViewItem tvi)
                {
                    var ptr = e.GetCurrentPoint(tvi);
                    if (ptr.Properties.IsMiddleButtonPressed)
                    {
                        CloseTab(tvi);
                        e.Handled = true;
                        return;
                    }

                    _lastPressedTabViewItem = tvi;
                }
            }), true);

            AttachTabContextMenu(tabViewItem);

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

            tab.ViewModeChanged += (navTab) =>
            {
                if (CurrentTab == navTab)
                {
                    ApplyViewMode(navTab.ViewMode, navTab.ViewScale, saveConfig: false);
                }
            };

            tab.ItemSelectionRequested += (navTab, selectName) =>
            {
                if (CurrentTab == navTab && !string.IsNullOrEmpty(selectName))
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        var targetItem = navTab.Items.FirstOrDefault(i => 
                            i.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase) ||
                            (i.IsDrive && i.FullPath.StartsWith(selectName, StringComparison.OrdinalIgnoreCase)));
                        if (targetItem != null)
                        {
                            SelectSingleItem(targetItem);

                            var list = ActiveListControl;
                            if (list != null)
                            {
                                list.UpdateLayout();
                                list.ScrollIntoView(targetItem);
                                this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                                {
                                    list.UpdateLayout();
                                    list.ScrollIntoView(targetItem);
                                });
                            }

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

        public void OpenSettingsTab(string? section = null)
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
                            if (!string.IsNullOrEmpty(section))
                            {
                                sc.NavigateToSection(section);
                            }
                        }
                        return;
                    }
                }

                var settingsControl = new Views.Settings.SettingsControl();
                if (!string.IsNullOrEmpty(section))
                {
                    settingsControl.NavigateToSection(section);
                }

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
                try
                {
                    string localFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                    System.IO.Directory.CreateDirectory(localFolder);
                    string crashLog = System.IO.Path.Combine(localFolder, "crash.log");
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OpenSettingsTab Exception: {ex}\r\n\r\n");
                }
                catch { }
                System.Diagnostics.Debug.WriteLine($"[OpenSettingsTab] Exception: {ex}");
            }
        }

        private void CloseTab(TabViewItem tabViewItem)
        {
            if (tabViewItem.DataContext is NavigationTabItem navTab && !string.IsNullOrEmpty(navTab.CurrentPath))
            {
                _recentlyClosedTabs.Push(navTab.CurrentPath);
            }

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
                    _appliedUiViewMode = null;
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

        private void AttachTabContextMenu(TabViewItem tabViewItem)
        {
            var menu = new MenuFlyout();

            var menuNewTab = new MenuFlyoutItem { Text = "新しいタブ (Ctrl+T)", Icon = new FontIcon { Glyph = "\uE710" } };
            menuNewTab.Click += (s, e) => CreateNewTab();

            var menuDuplicate = new MenuFlyoutItem { Text = "タブを複製", Icon = new FontIcon { Glyph = "\uE8C8" } };
            menuDuplicate.Click += (s, e) =>
            {
                if (tabViewItem.DataContext is NavigationTabItem navTab)
                {
                    CreateNewTab(navTab.CurrentPath);
                }
            };

            var menuCloseOthers = new MenuFlyoutItem { Text = "他のタブを閉じる", Icon = new FontIcon { Glyph = "\uE74D" } };
            menuCloseOthers.Click += (s, e) => CloseOtherTabs(tabViewItem);

            var menuCloseRight = new MenuFlyoutItem { Text = "右側のタブを閉じる", Icon = new FontIcon { Glyph = "\uE74D" } };
            menuCloseRight.Click += (s, e) => CloseTabsToTheRight(tabViewItem);

            var menuReopenClosed = new MenuFlyoutItem { Text = "閉じたタブを再度開く (Ctrl+Shift+T)", Icon = new FontIcon { Glyph = "\uE777" } };
            menuReopenClosed.Click += (s, e) => RestoreRecentlyClosedTab();

            var menuCopyPath = new MenuFlyoutItem { Text = "パスのコピー", Icon = new FontIcon { Glyph = "\uE8C8" } };
            menuCopyPath.Click += (s, e) =>
            {
                if (tabViewItem.DataContext is NavigationTabItem navTab && !string.IsNullOrEmpty(navTab.CurrentPath))
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(navTab.CurrentPath);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                }
            };

            var menuClose = new MenuFlyoutItem { Text = "タブを閉じる (Ctrl+W)", Icon = new FontIcon { Glyph = "\uE711" } };
            menuClose.Click += (s, e) => CloseTab(tabViewItem);

            menu.Items.Add(menuNewTab);
            menu.Items.Add(menuDuplicate);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(menuCloseOthers);
            menu.Items.Add(menuCloseRight);
            menu.Items.Add(menuReopenClosed);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(menuCopyPath);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(menuClose);

            menu.Opening += (s, e) =>
            {
                int idx = MainTabView.TabItems.IndexOf(tabViewItem);
                menuCloseOthers.IsEnabled = MainTabView.TabItems.Count > 1;
                menuCloseRight.IsEnabled = idx >= 0 && idx < MainTabView.TabItems.Count - 1;
                menuReopenClosed.IsEnabled = _recentlyClosedTabs.Count > 0;
            };

            tabViewItem.ContextFlyout = menu;
        }

        private void CloseOtherTabs(TabViewItem keepTab)
        {
            var tabsToClose = MainTabView.TabItems.OfType<TabViewItem>().Where(t => t != keepTab).ToList();
            foreach (var t in tabsToClose)
            {
                DetachTab(t, disposeModel: true);
            }
            MainTabView.SelectedItem = keepTab;
            BindCurrentTabToUi();
        }

        private void CloseTabsToTheRight(TabViewItem tabViewItem)
        {
            int idx = MainTabView.TabItems.IndexOf(tabViewItem);
            if (idx < 0) return;

            var tabsToClose = MainTabView.TabItems.OfType<TabViewItem>().Skip(idx + 1).ToList();
            foreach (var t in tabsToClose)
            {
                DetachTab(t, disposeModel: true);
            }
            MainTabView.SelectedItem = tabViewItem;
            BindCurrentTabToUi();
        }

        #endregion
    }
}
