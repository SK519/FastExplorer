using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Initialization & Sidebar

        private void InitializeSidebar()
        {
            SidebarList.ItemsSource = SidebarItems;
            QuickAccessService.PinnedItemsChanged += OnQuickAccessPinnedItemsChanged;
            
            // 初回は基本のスケルトンを即座に追加してUIフレームを瞬時に構築
            var initialSkeleton = new List<FileItem>
            {
                new FileItem
                {
                    Name = "ホーム",
                    FullPath = "Home",
                    EmojiIcon = "🏠",
                    GlyphIcon = "\uE80F",
                    FileType = "システム",
                    IsDirectory = true
                },
                new FileItem
                {
                    Name = "ごみ箱",
                    FullPath = RecycleBinService.RecycleBinUri,
                    GlyphIcon = "\uE74D",
                    FileType = "システム",
                    IsDirectory = true
                },
                new FileItem { IsSeparator = true },
                new FileItem
                {
                    Name = "PC",
                    FullPath = "ThisPC",
                    GlyphIcon = "\uE770",
                    FileType = "システム",
                    IsDirectory = true,
                    IsExpandable = true,
                    IsExpanded = _isPcExpanded
                },
                new FileItem
                {
                    Name = "ネットワーク",
                    FullPath = "shell:NetworkPlacesFolder",
                    GlyphIcon = "\uE968",
                    FileType = "システム",
                    IsDirectory = true,
                    IsExpandable = true,
                    IsExpanded = _isNetworkExpanded
                }
            };
            SyncSidebarItems(initialSkeleton);

            // 詳細なピン留めフォルダー・ドライブ走査はバックグラウンドで並行実行
            RefreshSidebar();
        }

        private void OnQuickAccessPinnedItemsChanged()
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                RefreshSidebar();
                if (CurrentTab?.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase) == true)
                {
                    RefreshHomeView();
                }
            });
        }

        public void RefreshSidebar()
        {
            bool isPcExp = _isPcExpanded;
            bool isNetExp = _isNetworkExpanded;
            bool isWslExp = _isWslExpanded;

            System.Threading.Tasks.Task.Run(() =>
            {
                var newItems = BuildSidebarItems(isPcExp, isNetExp, isWslExp);
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    SyncSidebarItems(newItems);
                });
            });
        }

        private List<FileItem> BuildSidebarItems(bool isPcExp, bool isNetExp, bool isWslExp)
        {
            var newItems = new List<FileItem>();

            // 1. ホーム (最上部)
            newItems.Add(new FileItem
            {
                Name = "ホーム",
                FullPath = "Home",
                EmojiIcon = "🏠",
                GlyphIcon = "\uE80F",
                FileType = "システム",
                IsDirectory = true
            });

            // 2. ごみ箱 (ホームの直下)
            newItems.Add(new FileItem
            {
                Name = "ごみ箱",
                FullPath = RecycleBinService.RecycleBinUri,
                GlyphIcon = "\uE74D",
                FileType = "システム",
                IsDirectory = true
            });

            // 区切り線 1 (システムとピン留めの間: 8pxの控えめな隙間)
            newItems.Add(new FileItem { IsSeparator = true });

            // 3. ピン留めセクション (標準 Windows Explorer と完全同期)
            var pinnedFolders = QuickAccessService.GetPinnedFolders();
            foreach (var pinned in pinnedFolders)
            {
                newItems.Add(new FileItem
                {
                    Name = pinned.Name,
                    FullPath = pinned.Path,
                    GlyphIcon = pinned.GlyphIcon,
                    FileType = "ピン留めフォルダー",
                    IsDirectory = true,
                    IsPinned = true
                });
            }

            // 区切り線 2 (ピン留めとPCの間: 8pxの控えめな隙間)
            newItems.Add(new FileItem { IsSeparator = true });

            // 4. PC (ThisPC) - 展開可能なツリー
            var pcItem = new FileItem
            {
                Name = "PC",
                FullPath = "ThisPC",
                GlyphIcon = "\uE770",
                FileType = "システム",
                IsDirectory = true,
                IsExpandable = true,
                IsExpanded = isPcExp
            };
            newItems.Add(pcItem);

            if (isPcExp)
            {
                var drives = NativeFileScanner.GetDrives();
                foreach (var drive in drives)
                {
                    drive.IndentLevel = 1;
                    newItems.Add(drive);
                }
            }

            // 5. ネットワーク - 展開マーク付き
            var netItem = new FileItem
            {
                Name = "ネットワーク",
                FullPath = "shell:NetworkPlacesFolder",
                GlyphIcon = "\uE968",
                FileType = "システム",
                IsDirectory = true,
                IsExpandable = true,
                IsExpanded = isNetExp
            };
            newItems.Add(netItem);

            if (isNetExp)
            {
                var netDrives = NativeFileScanner.GetNetworkPlaces();
                foreach (var netDrive in netDrives)
                {
                    netDrive.IndentLevel = 1;
                    newItems.Add(netDrive);
                }
            }

            // 5. WSL
            var distros = GetWslDistros();
            if (distros.Count > 0 || Directory.Exists(@"\\wsl.localhost") || Directory.Exists(@"\\wsl$"))
            {
                var wslHeader = new FileItem
                {
                    Name = "WSL",
                    FullPath = @"\\wsl.localhost",
                    GlyphIcon = "\uE74C",
                    FileType = "システム",
                    IsDirectory = true,
                    IsExpandable = true,
                    IsExpanded = isWslExp
                };
                newItems.Add(wslHeader);

                if (isWslExp)
                {
                    if (distros.Count == 0) distros.Add("Ubuntu");
                    foreach (var distro in distros)
                    {
                        newItems.Add(new FileItem
                        {
                            Name = distro,
                            FullPath = $@"\\wsl.localhost\{distro}",
                            GlyphIcon = "\uE74C",
                            FileType = "WSL ディストリビューション",
                            IsDirectory = true,
                            IndentLevel = 1
                        });
                    }
                }
            }

            return newItems;
        }

        private void SyncSidebarItems(List<FileItem> newItems)
        {
            while (SidebarItems.Count > newItems.Count)
            {
                SidebarItems.RemoveAt(SidebarItems.Count - 1);
            }

            for (int i = 0; i < newItems.Count; i++)
            {
                var target = newItems[i];
                if (i < SidebarItems.Count)
                {
                    var existing = SidebarItems[i];
                    bool isMatch = existing.IsSeparator == target.IsSeparator
                        && string.Equals(existing.FullPath, target.FullPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                        && existing.IndentLevel == target.IndentLevel
                        && existing.IsExpanded == target.IsExpanded;

                    if (!isMatch)
                    {
                        IconThumbnailService.Instance.ApplyImmediateDefaultIcon(target);
                        SidebarItems[i] = target;
                        IconThumbnailService.Instance.Enqueue(target);
                    }
                    else if (existing.Icon == null && !existing.IsSeparator)
                    {
                        IconThumbnailService.Instance.ApplyImmediateDefaultIcon(existing);
                        IconThumbnailService.Instance.Enqueue(existing);
                    }
                }
                else
                {
                    IconThumbnailService.Instance.ApplyImmediateDefaultIcon(target);
                    SidebarItems.Add(target);
                    IconThumbnailService.Instance.Enqueue(target);
                }
            }
        }

        private bool _isPcExpanded = false;
        private bool _isNetworkExpanded = false;
        private bool _isWslExpanded = false;

        private static List<string> GetWslDistros()
        {
            var distros = new List<string>();
            try
            {
                using var lxssKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
                if (lxssKey != null)
                {
                    foreach (var subKeyName in lxssKey.GetSubKeyNames())
                    {
                        using var subKey = lxssKey.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("DistributionName") is string distName && !string.IsNullOrWhiteSpace(distName))
                        {
                            distros.Add(distName);
                        }
                    }
                }
            }
            catch { }

            if (distros.Count == 0)
            {
                try
                {
                    if (Directory.Exists(@"\\wsl.localhost"))
                    {
                        distros.AddRange(Directory.GetDirectories(@"\\wsl.localhost").Select(Path.GetFileName).Where(s => !string.IsNullOrEmpty(s))!);
                    }
                    else if (Directory.Exists(@"\\wsl$"))
                    {
                        distros.AddRange(Directory.GetDirectories(@"\\wsl$").Select(Path.GetFileName).Where(s => !string.IsNullOrEmpty(s))!);
                    }
                }
                catch { }
            }

            return distros.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void SidebarChevron_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FileItem item)
            {
                ToggleSidebarItemExpansion(item);
            }
        }

        private void SidebarItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FileItem item && item.IsExpandable)
            {
                ToggleSidebarItemExpansion(item);
                e.Handled = true;
            }
        }

        private void ToggleSidebarItemExpansion(FileItem item)
        {
            if (!item.IsExpandable) return;
            item.IsExpanded = !item.IsExpanded;
            int index = SidebarItems.IndexOf(item);
            if (index < 0) return;

            if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                _isPcExpanded = item.IsExpanded;
                if (item.IsExpanded)
                {
                    var drives = NativeFileScanner.GetDrives();
                    for (int i = 0; i < drives.Count; i++)
                    {
                        drives[i].IndentLevel = 1;
                        SidebarItems.Insert(index + 1 + i, drives[i]);
                        IconThumbnailService.Instance.Enqueue(drives[i]);
                    }
                }
                else
                {
                    while (index + 1 < SidebarItems.Count && SidebarItems[index + 1].IndentLevel > 0)
                    {
                        SidebarItems.RemoveAt(index + 1);
                    }
                }
            }
            else if (item.Name.Equals("WSL", StringComparison.OrdinalIgnoreCase))
            {
                _isWslExpanded = item.IsExpanded;
                if (item.IsExpanded)
                {
                    var distros = GetWslDistros();
                    if (distros.Count == 0) distros.Add("Ubuntu");
                    for (int i = 0; i < distros.Count; i++)
                    {
                        var distroItem = new FileItem
                        {
                            Name = distros[i],
                            FullPath = $@"\\wsl.localhost\{distros[i]}",
                            GlyphIcon = "\uE74C",
                            FileType = "WSL ディストリビューション",
                            IsDirectory = true,
                            IndentLevel = 1
                        };
                        SidebarItems.Insert(index + 1 + i, distroItem);
                        IconThumbnailService.Instance.Enqueue(distroItem);
                    }
                }
                else
                {
                    while (index + 1 < SidebarItems.Count && SidebarItems[index + 1].IndentLevel > 0)
                    {
                        SidebarItems.RemoveAt(index + 1);
                    }
                }
            }
            else if (item.FullPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase))
            {
                _isNetworkExpanded = item.IsExpanded;
                if (item.IsExpanded)
                {
                    var netItems = NativeFileScanner.GetNetworkPlaces();
                    for (int i = 0; i < netItems.Count; i++)
                    {
                        netItems[i].IndentLevel = 1;
                        SidebarItems.Insert(index + 1 + i, netItems[i]);
                        IconThumbnailService.Instance.Enqueue(netItems[i]);
                    }
                }
                else
                {
                    while (index + 1 < SidebarItems.Count && SidebarItems[index + 1].IndentLevel > 0)
                    {
                        SidebarItems.RemoveAt(index + 1);
                    }
                }
            }
        }

        private void SidebarList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!_isInitialized) return;
            if (e.ClickedItem is FileItem clicked && !clicked.IsSeparator && CurrentTab != null)
            {
                CurrentTab.NavigateTo(clicked.FullPath);
            }
        }

        private void SidebarList_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileItem item && !item.IsSeparator)
            {
                // 1. ホームは右クリックを受け付けない
                if (item.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase) || item.Name.Equals("ホーム", StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    return;
                }

                var point = new Windows.Foundation.Point(0, 0);
                bool hasPoint = e.TryGetPosition(sender, out point);

                // 2. ごみ箱のコンテキストメニュー
                if (RecycleBinService.IsRecycleBinPath(item.FullPath))
                {
                    e.Handled = true;
                    var menu = new MenuFlyout();

                    var openItem = new MenuFlyoutItem { Text = "開く" };
                    openItem.Icon = new FontIcon { Glyph = "\uE8E5" };
                    openItem.Click += (s, args) => CurrentTab?.NavigateTo(item.FullPath);
                    menu.Items.Add(openItem);

                    var openNewTab = new MenuFlyoutItem { Text = "新しいタブで開く" };
                    openNewTab.Icon = new FontIcon { Glyph = "\uE737" };
                    openNewTab.Click += (s, args) => CreateNewTab(item.FullPath);
                    menu.Items.Add(openNewTab);

                    menu.Items.Add(new MenuFlyoutSeparator());

                    var emptyItem = new MenuFlyoutItem { Text = "ごみ箱を空にする" };
                    emptyItem.Icon = new FontIcon { Glyph = "\uE74D" };
                    emptyItem.Click += (s, args) =>
                    {
                        RecycleBinService.EmptyRecycleBin(WindowHandle, showConfirmation: true);
                        if (RecycleBinService.IsRecycleBinPath(CurrentTab?.CurrentPath))
                        {
                            CurrentTab?.Refresh();
                        }
                    };
                    menu.Items.Add(emptyItem);

                    if (hasPoint) menu.ShowAt(sender, point);
                    else menu.ShowAt(fe);
                    return;
                }

                // 3. PC、ネットワーク、WSL（システムヘッダー項目）はシンプルなメニュー（開く / 新しいタブで開く）
                bool isSystemRoot = item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)
                    || item.FullPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase)
                    || (item.Name.Equals("WSL", StringComparison.OrdinalIgnoreCase) && item.IndentLevel == 0);

                if (isSystemRoot)
                {
                    e.Handled = true;
                    var menu = new MenuFlyout();

                    var openItem = new MenuFlyoutItem { Text = "開く" };
                    openItem.Icon = new FontIcon { Glyph = "\uE8E5" };
                    openItem.Click += (s, args) => CurrentTab?.NavigateTo(item.FullPath);
                    menu.Items.Add(openItem);

                    var openNewTab = new MenuFlyoutItem { Text = "新しいタブで開く" };
                    openNewTab.Icon = new FontIcon { Glyph = "\uE737" };
                    openNewTab.Click += (s, args) => CreateNewTab(item.FullPath);
                    menu.Items.Add(openNewTab);

                    if (hasPoint)
                    {
                        menu.ShowAt(sender, point);
                    }
                    else
                    {
                        menu.ShowAt(fe);
                    }
                    return;
                }

                // 3. ピン留めフォルダーやその他のフォルダー・ファイルは通常のコンテキストメニュー
                e.Handled = true;
                ShowItemContextMenuForPath(sender, hasPoint ? point : null, item.FullPath, isDirectory: item.IsDirectory);
            }
        }

        #endregion
    }
}
