using System;
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
                    _lastPressedTabViewItem = tvi;
                    TabDragDropService.SetDraggingTab(this, tvi);
                }
            }), true);

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
    }
}
