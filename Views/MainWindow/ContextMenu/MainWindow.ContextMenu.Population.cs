using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Helpers;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu Population & Opening Events

        private ActiveShellMenuSession? _activeShellSession;

        private void ItemContextMenu_Closed(object? sender, object e)
        {
            UninstallMouseHook();
            CancelSubmenuOpen();
            HideActiveSubmenu();
            _activeShellSession?.Dispose();
            _activeShellSession = null;
            this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _contextTargetItemsOverride = null;
            });
        }

        private void ItemContextMenu_Opening(object? sender, object e)
        {
            try
            {
                InstallMouseHook();
                EnsureWindowSubclass();
                HideActiveSubmenu();

                var selectedItems = GetContextTargetItems();
                if (selectedItems.Count == 0) return;

                var targetItem = selectedItems.Count == 1 ? selectedItems[0] : null;
                bool isSingle = selectedItems.Count == 1;
                bool isFile = isSingle && !selectedItems[0].IsDirectory;
                bool isDirectory = isSingle && selectedItems[0].IsDirectory;
                bool isZip = isFile && selectedItems[0].Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);

                var shellConfig = ConfigService.Current.ShellMenu;
                if (HeaderBtnPaste != null)
                {
                    try { HeaderBtnPaste.IsEnabled = FileOperationService.CanPaste(); } catch { HeaderBtnPaste.IsEnabled = false; }
                }

                // 「その他のオプションを表示 (Shift+右クリック)」の描画幅を基準に動的にメニュー幅を計算・適用
                if (ContextMenuRootPanel != null)
                {
                    ContextMenuRootPanel.Width = CalculateStandardContextMenuWidth();
                }

                ContextMenuItemsPanel.Children.Clear();

                Style? itemStyle = null;
                try
                {
                    if (Application.Current.Resources.TryGetValue("ContextMenuItemButtonStyle", out var styleObj))
                    {
                        itemStyle = styleObj as Style;
                    }
                }
                catch { }

                // ==========================================
                // エリア 1: 標準機能項目 (上部)
                // ==========================================
                var standardItems = new List<FrameworkElement>();
                bool isHomeView = CurrentTab?.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase) == true;
                bool isRecycleBin = RecycleBinService.IsRecycleBinPath(CurrentTab?.CurrentPath);
                bool isInsideArchive = ArchiveService.IsArchiveOrSubPath(CurrentTab?.CurrentPath, out _, out _);

                if (isRecycleBin)
                {
                    // ごみ箱内アイテム専用コンテキストメニュー
                    standardItems.Add(CreateContextButton("\uE777", "元に戻す", ContextMenuRestore_Click, itemStyle));
                    standardItems.Add(CreateContextButton("\uE74D", "完全に削除", ContextMenuDelete_Click, itemStyle));
                    standardItems.Add(CreateContextButton("\uE90F", "プロパティ", ContextMenuProperties_Click, itemStyle));

                    foreach (var item in standardItems)
                    {
                        ContextMenuItemsPanel.Children.Add(item);
                    }
                    return;
                }

                // 開く (フォルダー、またはアーカイブ内部以外の通常ファイル)
                if (isDirectory || (!isInsideArchive && isFile) || (!isInsideArchive && selectedItems.Count > 0))
                {
                    standardItems.Add(CreateContextButton("\uE8E5", "開く", ContextMenuOpen_Click, itemStyle));
                }

                // 新しいタブで開く
                if (isDirectory && !isInsideArchive)
                {
                    standardItems.Add(CreateContextButton("\uE737", "新しいタブで開く", ContextMenuOpenNewTab_Click, itemStyle));
                }

                // プログラムから開く
                if (isFile && shellConfig.ShowOpenWith && !isInsideArchive && targetItem != null && !string.IsNullOrEmpty(targetItem.FullPath))
                {
                    var targetPathsList = selectedItems.Select(x => x.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    var openWithFlyout = CreateOpenWithSubFlyout(1, targetItem.FullPath, targetPathsList, itemStyle);
                    standardItems.Add(CreateContextSubmenuButton("\uE7AC", "プログラムから開く", openWithFlyout, itemStyle));
                }

                // テキストで編集
                if (isFile && shellConfig.ShowEditWithEditor && !isInsideArchive)
                {
                    standardItems.Add(CreateContextButton("\uE70F", "テキストで編集", ContextMenuEdit_Click, itemStyle));
                }

                // ターミナルで開く
                if (shellConfig.ShowOpenInTerminal && !isInsideArchive && !string.IsNullOrEmpty(targetItem?.FullPath) && !targetItem.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase) && !targetItem.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    standardItems.Add(CreateContextButton("\uE756", "ターミナルで開く", ContextMenuTerminal_Click, itemStyle));
                }

                // パスのコピー
                if (shellConfig.ShowCopyPath && !string.IsNullOrEmpty(targetItem?.FullPath) && !targetItem.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase) && !targetItem.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    standardItems.Add(CreateContextButton("\uE8C8", "パスのコピー", ContextMenuCopyPath_Click, itemStyle));
                }

                // クイック アクセスにピン留め / 解除
                if (targetItem != null && targetItem.IsDirectory && !string.IsNullOrEmpty(targetItem.FullPath))
                {
                    bool isPinned = false;
                    try { isPinned = QuickAccessService.IsPinned(targetItem.FullPath); } catch { }
                    if (isPinned)
                    {
                        standardItems.Add(CreateContextButton("\uE77A", "クイック アクセスからピン留めを外す", ContextMenuUnpinQuickAccess_Click, itemStyle));
                    }
                    else
                    {
                        standardItems.Add(CreateContextButton("\uE718", "クイック アクセスにピン留め", ContextMenuPinQuickAccess_Click, itemStyle));
                    }
                }

                // ファイルの場所を開く (Home 画面またはショートカット所持アイテム)
                if ((isHomeView || !string.IsNullOrEmpty(targetItem?.ShortcutPath)) && !string.IsNullOrEmpty(targetItem?.FullPath) && !targetItem.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase) && !targetItem.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    standardItems.Add(CreateContextButton("\uED25", "ファイルの場所を開く", ContextMenuOpenFileLocation_Click, itemStyle));
                }

                // 最近使用した項目から削除 (Home または ショートカット所持アイテム)
                if (isHomeView || !string.IsNullOrEmpty(targetItem?.ShortcutPath))
                {
                    standardItems.Add(CreateContextButton("\uE74D", "最近使用した項目から削除", ContextMenuRemoveFromRecent_Click, itemStyle));
                }

                // アーカイブファイルの判定
                bool isArchive = isFile && ArchiveService.IsSupportedArchive(targetItem?.FullPath);

                // 展開 (解凍) 項目 (ZIP / 7-Zip / RAR / TAR / GZ 等のアーカイブファイル選択時)
                if (isArchive && !string.IsNullOrEmpty(targetItem?.FullPath) && !targetItem.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase) && !targetItem.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    string folderName = Path.GetFileNameWithoutExtension(targetItem.FullPath);
                    standardItems.Add(CreateContextButton("\uE896", "ここに展開", ContextMenuExtractHere_Click, itemStyle));
                    standardItems.Add(CreateContextButton("\uE838", $"\"{folderName}\" に展開", ContextMenuExtractToFolder_Click, itemStyle));
                }

                // 圧縮項目 (複数選択、またはアーカイブ以外の単一項目選択時)
                bool canCompress = selectedItems.Count > 0 &&
                                   !isHomeView &&
                                   selectedItems.All(i => !string.IsNullOrEmpty(i.FullPath) &&
                                                          !i.FullPath.Equals("Home", StringComparison.OrdinalIgnoreCase) &&
                                                          !i.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase));

                if (shellConfig.ShowZipOptions && canCompress && (!isSingle || !isArchive))
                {
                    standardItems.Add(CreateContextButton("\uE8F1", "ZIP ファイルに圧縮", ContextMenuCompressZip_Click, itemStyle));
                    standardItems.Add(CreateContextButton("\uF126", "7z ファイルに圧縮", ContextMenuCompress7z_Click, itemStyle));

                    var compSubFlyout = CreateCompressionSubFlyout(1, itemStyle);
                    standardItems.Add(CreateContextSubmenuButton("\uE8F1", "圧縮オプション", compSubFlyout, itemStyle));
                }

                foreach (var item in standardItems)
                {
                    ContextMenuItemsPanel.Children.Add(item);
                }

                // ==========================================
                // エリア 2: 動的 OS シェルメニュー抽出 & 統合 (中央部)
                // ==========================================
                _activeShellSession?.Dispose();
                _activeShellSession = null;

                var targetPaths = selectedItems.Select(x => x.FullPath).Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
                if (targetPaths.Count > 0)
                {
                    var session = new ActiveShellMenuSession(WindowHandle);
                    if (session.Build(targetPaths) && session.ExtractedItems.Count > 0)
                    {
                        _activeShellSession = session;

                        var orderedItems = SortExtractedItems(session.ExtractedItems, shellConfig.MenuOrder);
                        if (orderedItems.Count > 0 && ContextMenuItemsPanel.Children.Count > 0)
                        {
                            ContextMenuItemsPanel.Children.Add(CreateContextMenuSeparator());
                        }

                        foreach (var shellItem in orderedItems)
                        {
                            var capturedItem = shellItem;
                            if (capturedItem.IsSubmenu && capturedItem.Children.Count > 0)
                            {
                                var subFlyout = CreateShellSubFlyout(1, capturedItem, targetPaths, itemStyle);
                                var subBtn = CreateContextSubmenuButton(capturedItem.Glyph, capturedItem.CleanText, subFlyout, itemStyle);
                                ContextMenuItemsPanel.Children.Add(subBtn);
                            }
                            else
                            {
                                var btn = CreateContextButton(capturedItem.Glyph, capturedItem.CleanText, (s, args) =>
                                {
                                    string? workingDir = targetPaths.Count > 0 ? (Directory.Exists(targetPaths[0]) ? targetPaths[0] : Path.GetDirectoryName(targetPaths[0])) : null;
                                    bool invoked = _activeShellSession?.InvokeCommand(capturedItem, workingDir) ?? false;
                                    ItemContextMenu.Hide();
                                    if (!invoked)
                                    {
                                        ShellContextMenuService.InvokeShellCommand(WindowHandle, targetPaths, capturedItem);
                                    }
                                }, itemStyle);
                                ContextMenuItemsPanel.Children.Add(btn);
                            }
                        }
                    }
                    else
                    {
                        session.Dispose();
                    }
                }

                // ==========================================
                // エリア 3: 最下部固定項目 (その他のオプションを表示)
                // ==========================================
                if (shellConfig.ShowOsStandardOption)
                {
                    if (ContextMenuItemsPanel.Children.Count > 0)
                    {
                        ContextMenuItemsPanel.Children.Add(CreateContextMenuSeparator());
                    }

                    var osBtn = CreateContextButton("\uE712", "その他のオプションを表示 (Shift+右クリック)", ContextMenuOsStandard_Click, itemStyle);
                    ContextMenuItemsPanel.Children.Add(osBtn);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ItemContextMenu_Opening] Fatal error: {ex}");
            }
        }

        private void BackgroundContextMenu_Opening(object? sender, object e)
        {
            try
            {
                bool isRecycleBin = RecycleBinService.IsRecycleBinPath(CurrentTab?.CurrentPath);

                if (MenuBgPaste != null)
                {
                    try { MenuBgPaste.IsEnabled = !isRecycleBin && FileOperationService.CanPaste(); } catch { MenuBgPaste.IsEnabled = false; }
                    MenuBgPaste.Visibility = isRecycleBin ? Visibility.Collapsed : Visibility.Visible;
                }
                if (MenuBgNewSubItem != null)
                {
                    MenuBgNewSubItem.Visibility = isRecycleBin ? Visibility.Collapsed : Visibility.Visible;
                    if (!isRecycleBin)
                    {
                        PopulateNewMenu(MenuBgNewSubItem.Items, isToolbarFlyout: false);
                    }
                }

                // ごみ箱背景用「ごみ箱を空にする」アイテムの動的挿入
                if (sender is MenuFlyout flyout)
                {
                    var existingEmpty = flyout.Items.FirstOrDefault(i => (i.Tag as string) == "RecycleBinEmpty");
                    if (isRecycleBin)
                    {
                        if (existingEmpty == null)
                        {
                            var emptyItem = new MenuFlyoutItem
                            {
                                Text = "ごみ箱を空にする",
                                Icon = new FontIcon { Glyph = "\uE74D" },
                                Tag = "RecycleBinEmpty"
                            };
                            emptyItem.Click += ContextMenuEmptyRecycleBin_Click;
                            flyout.Items.Insert(0, emptyItem);
                        }
                    }
                    else if (existingEmpty != null)
                    {
                        flyout.Items.Remove(existingEmpty);
                    }
                }

                UpdateViewMenuCheckStates();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BackgroundContextMenu_Opening] Error: {ex}");
            }
        }

        private void NewItemFlyout_Opening(object? sender, object e)
        {
            if (sender is MenuFlyout flyout)
            {
                PopulateNewMenu(flyout.Items, isToolbarFlyout: true);
            }
        }

        private void PopulateNewMenu(IList<MenuFlyoutItemBase> targetList, bool isToolbarFlyout)
        {
            targetList.Clear();

            var folderItem = new MenuFlyoutItem
            {
                Text = "フォルダー (Ctrl+Shift+N)",
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            folderItem.Click += ContextMenuNewFolder_Click;
            targetList.Add(folderItem);

            var textItem = new MenuFlyoutItem
            {
                Text = "テキスト ドキュメント"
            };
            textItem.Click += ContextMenuNewTextFile_Click;
            SetMenuItemExtensionIcon(textItem, ".txt", "\uE7C3");
            targetList.Add(textItem);

            var templates = ShellNewService.GetShellNewTemplates();
            var extraTemplates = templates.Where(t => !t.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)).ToList();

            if (extraTemplates.Count > 0)
            {
                targetList.Add(new MenuFlyoutSeparator());

                foreach (var template in extraTemplates)
                {
                    var captured = template;
                    var item = new MenuFlyoutItem
                    {
                        Text = captured.DisplayName
                    };
                    item.Click += (s, args) => CreateNewFileFromTemplate(captured);
                    SetMenuItemExtensionIcon(item, captured.Extension, captured.Glyph);
                    targetList.Add(item);
                }
            }
        }

        private async void SetMenuItemExtensionIcon(MenuFlyoutItem menuItem, string extension, string fallbackGlyph)
        {
            var softwareBitmap = IconThumbnailService.GetSoftwareBitmapForExtension(extension);
            if (softwareBitmap != null)
            {
                try
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(softwareBitmap);
                    menuItem.Icon = new ImageIcon { Source = source };
                    return;
                }
                catch { }
            }

            menuItem.Icon = new FontIcon { Glyph = fallbackGlyph };
        }

        #endregion
    }
}
