using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region View Mode Switching

        private DataTemplate? GetTemplate(string key, string fallbackKey)
        {
            if (RootGrid.Resources.TryGetValue(key, out var res) && res is DataTemplate dt) return dt;
            if (Application.Current.Resources.TryGetValue(key, out res) && res is DataTemplate dtApp) return dtApp;
            if (RootGrid.Resources.TryGetValue(fallbackKey, out var fRes) && fRes is DataTemplate fDt) return fDt;
            if (Application.Current.Resources.TryGetValue(fallbackKey, out fRes) && fRes is DataTemplate fDtApp) return fDtApp;
            return null;
        }

        public void ApplyViewMode(FolderViewMode mode, ViewScaleLevel? scale = null, bool saveConfig = true)
        {
            if (CurrentTab == null) return;

            var oldMode = CurrentTab.ViewMode;
            var oldScale = CurrentTab.ViewScale;

            if (scale.HasValue)
            {
                CurrentTab.ViewScale = scale.Value;
            }
            CurrentTab.ViewMode = mode;
            var scaleLevel = CurrentTab.ViewScale;

            bool isCurrentGrid = FileGridView != null && FileGridView.Visibility == Visibility.Visible;
            bool shouldBeGrid = mode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons or FolderViewMode.List or FolderViewMode.Tiles;

            // 変更がなく、かつUIの表示状態も一致している場合は何もしない
            if (oldMode == mode && oldScale == scaleLevel && !saveConfig && isCurrentGrid == shouldBeGrid)
            {
                UpdateViewMenuCheckStates();
                UpdateActionToolbarButtons();
                return;
            }

            bool modeChanged = oldMode != mode;
            List<FileItem>? selectedItems = null;
            if (modeChanged)
            {
                selectedItems = ActiveListControl.SelectedItems.OfType<FileItem>().ToList();
            }

            if (FileListView == null || FileGridView == null) return;

            switch (mode)
            {
                case FolderViewMode.Details:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Visible) FileListHeader.Visibility = Visibility.Visible;
                    if (FileListView.Visibility != Visibility.Visible) FileListView.Visibility = Visibility.Visible;
                    if (FileGridView.Visibility != Visibility.Collapsed) FileGridView.Visibility = Visibility.Collapsed;
                    var detailsTemplate = GetTemplate("DetailsItemTemplate", "DetailsItemTemplate");
                    if (FileListView.ItemTemplate != detailsTemplate)
                    {
                        FileListView.ItemTemplate = detailsTemplate;
                    }
                    if (CurrentTab != null)
                    {
                        foreach (var item in CurrentTab.Items)
                        {
                            item.ApplyDetailsScale(scaleLevel);
                        }
                    }
                    break;

                case FolderViewMode.Content:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Visible) FileListView.Visibility = Visibility.Visible;
                    if (FileGridView.Visibility != Visibility.Collapsed) FileGridView.Visibility = Visibility.Collapsed;
                    FileListView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("ContentExtraLargeItemTemplate", "ContentLargeItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("ContentLargeItemTemplate", "ContentItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("ContentCompactItemTemplate", "ContentItemTemplate"),
                        _ => GetTemplate("ContentItemTemplate", "ContentItemTemplate")
                    };
                    break;

                case FolderViewMode.ExtraLargeIcons:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Collapsed) FileListView.Visibility = Visibility.Collapsed;
                    if (FileGridView.Visibility != Visibility.Visible) FileGridView.Visibility = Visibility.Visible;
                    FileGridView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("ExtraLargeIconsLargeItemTemplate", "ExtraLargeIconsItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("ExtraLargeIconsLargeItemTemplate", "ExtraLargeIconsItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("ExtraLargeIconsCompactItemTemplate", "ExtraLargeIconsItemTemplate"),
                        _ => GetTemplate("ExtraLargeIconsItemTemplate", "ExtraLargeIconsItemTemplate")
                    };
                    break;

                case FolderViewMode.LargeIcons:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Collapsed) FileListView.Visibility = Visibility.Collapsed;
                    if (FileGridView.Visibility != Visibility.Visible) FileGridView.Visibility = Visibility.Visible;
                    FileGridView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("LargeIconsLargeItemTemplate", "LargeIconsItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("LargeIconsLargeItemTemplate", "LargeIconsItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("LargeIconsCompactItemTemplate", "LargeIconsItemTemplate"),
                        _ => GetTemplate("LargeIconsItemTemplate", "LargeIconsItemTemplate")
                    };
                    break;

                case FolderViewMode.MediumIcons:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Collapsed) FileListView.Visibility = Visibility.Collapsed;
                    if (FileGridView.Visibility != Visibility.Visible) FileGridView.Visibility = Visibility.Visible;
                    FileGridView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("MediumIconsLargeItemTemplate", "MediumIconsItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("MediumIconsLargeItemTemplate", "MediumIconsItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("MediumIconsCompactItemTemplate", "MediumIconsItemTemplate"),
                        _ => GetTemplate("MediumIconsItemTemplate", "MediumIconsItemTemplate")
                    };
                    break;

                case FolderViewMode.SmallIcons:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Collapsed) FileListView.Visibility = Visibility.Collapsed;
                    if (FileGridView.Visibility != Visibility.Visible) FileGridView.Visibility = Visibility.Visible;
                    FileGridView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("SmallIconsLargeItemTemplate", "SmallIconsItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("SmallIconsLargeItemTemplate", "SmallIconsItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("SmallIconsCompactItemTemplate", "SmallIconsItemTemplate"),
                        _ => GetTemplate("SmallIconsItemTemplate", "SmallIconsItemTemplate")
                    };
                    break;

                case FolderViewMode.List:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Collapsed) FileListView.Visibility = Visibility.Collapsed;
                    if (FileGridView.Visibility != Visibility.Visible) FileGridView.Visibility = Visibility.Visible;
                    FileGridView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("ListExtraLargeItemTemplate", "ListLargeItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("ListLargeItemTemplate", "ListItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("ListCompactItemTemplate", "ListItemTemplate"),
                        _ => GetTemplate("ListItemTemplate", "ListItemTemplate")
                    };
                    break;

                case FolderViewMode.Tiles:
                    if (FileListHeader != null && FileListHeader.Visibility != Visibility.Collapsed) FileListHeader.Visibility = Visibility.Collapsed;
                    if (FileListView.Visibility != Visibility.Collapsed) FileListView.Visibility = Visibility.Collapsed;
                    if (FileGridView.Visibility != Visibility.Visible) FileGridView.Visibility = Visibility.Visible;
                    FileGridView.ItemTemplate = scaleLevel switch
                    {
                        ViewScaleLevel.ExtraLarge => GetTemplate("TilesExtraLargeItemTemplate", "TilesLargeItemTemplate"),
                        ViewScaleLevel.Large => GetTemplate("TilesLargeItemTemplate", "TilesItemTemplate"),
                        ViewScaleLevel.Compact => GetTemplate("TilesCompactItemTemplate", "TilesItemTemplate"),
                        _ => GetTemplate("TilesItemTemplate", "TilesItemTemplate")
                    };
                    break;
            }

            if (CurrentTab?.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (FileListHeader != null) FileListHeader.Visibility = Visibility.Collapsed;
                FileListContainer.Visibility = Visibility.Collapsed;
                if (HomeView != null) HomeView.Visibility = Visibility.Visible;
            }
            else
            {
                if (HomeView != null) HomeView.Visibility = Visibility.Collapsed;
                FileListContainer.Visibility = Visibility.Visible;
            }

            // モードによる画像プレビュー許可状態の同期とアイコン再取得
            bool wasImageOriented = IconThumbnailService.IsImageOrientedMode(oldMode);
            bool isNowImageOriented = IconThumbnailService.IsImageOrientedMode(mode);
            if (wasImageOriented != isNowImageOriented && CurrentTab != null)
            {
                foreach (var item in CurrentTab.Items)
                {
                    item.AllowThumbnail = isNowImageOriented;
                    IconThumbnailService.Instance.Enqueue(item, force: true);
                }
            }

            // モードが切り替わった場合のみ選択状態の復元を行う（同じモード内でのサイズ変更時のチラつき防止）
            if (modeChanged && selectedItems != null)
            {
                ActiveListControl.SelectedItems.Clear();
                foreach (var item in selectedItems)
                {
                    ActiveListControl.SelectedItems.Add(item);
                }
            }

            UpdateViewMenuCheckStates();
            UpdateActionToolbarButtons();

            if (saveConfig && CurrentTab != null)
            {
                string normPath = FastExplorer.Helpers.PathHelper.NormalizeFolderPath(CurrentTab.CurrentPath);
                if (!string.IsNullOrEmpty(normPath) && !normPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) && !normPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    ConfigService.Current.FolderViewSettings[normPath] = new FolderViewSetting
                    {
                        ViewMode = mode.ToString(),
                        ViewScale = (int)scaleLevel,
                        CustomSize = CurrentTab.CustomSize
                    };
                    ConfigService.Save();
                }
            }
        }

        private void ViewModeExtraLarge_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.ExtraLargeIcons);
        private void ViewModeLarge_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.LargeIcons);
        private void ViewModeMedium_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.MediumIcons);
        private void ViewModeSmall_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.SmallIcons);
        private void ViewModeList_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.List);
        private void ViewModeDetails_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.Details);
        private void ViewModeTiles_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.Tiles);
        private void ViewModeContent_Click(object sender, RoutedEventArgs e) => ApplyViewMode(FolderViewMode.Content);

        private void ViewZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewScale == ViewScaleLevel.Compact)
            {
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Normal);
            }
            else if (CurrentTab.ViewScale == ViewScaleLevel.Normal)
            {
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Large);
            }
            else
            {
                // 次の上位モードへ
                int idx = Array.FindIndex(FullZoomOrder, z => z.Mode == CurrentTab.ViewMode && z.Scale == CurrentTab.ViewScale);
                if (idx >= 0 && idx < FullZoomOrder.Length - 1)
                {
                    ApplyViewMode(FullZoomOrder[idx + 1].Mode, FullZoomOrder[idx + 1].Scale);
                }
            }
        }

        private void ViewZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewScale == ViewScaleLevel.Large)
            {
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Normal);
            }
            else if (CurrentTab.ViewScale == ViewScaleLevel.Normal)
            {
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Compact);
            }
            else
            {
                // 下位モードへ
                int idx = Array.FindIndex(FullZoomOrder, z => z.Mode == CurrentTab.ViewMode && z.Scale == CurrentTab.ViewScale);
                if (idx > 0)
                {
                    ApplyViewMode(FullZoomOrder[idx - 1].Mode, FullZoomOrder[idx - 1].Scale);
                }
            }
        }

        private void ViewZoomReset_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Normal);
        }

        private void QuickToggleIconsView_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewMode == FolderViewMode.Details || CurrentTab.ViewMode == FolderViewMode.Content)
            {
                ApplyViewMode(FolderViewMode.MediumIcons);
            }
            else
            {
                ApplyViewMode(FolderViewMode.Details);
            }
        }

        private void ViewModeGrid_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewMode is not (FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons))
            {
                ApplyViewMode(FolderViewMode.MediumIcons);
            }
        }



        private void ViewSizeSmall_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            // アイコングリッドモードの場合: 小アイコンサイズに切り替え。リスト系モードの場合: Compact スケールを適用
            if (CurrentTab.ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons)
                ApplyViewMode(FolderViewMode.SmallIcons);
            else
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Compact);
        }

        private void ViewSizeMedium_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons)
                ApplyViewMode(FolderViewMode.MediumIcons);
            else
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Normal);
        }

        private void ViewSizeLarge_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons)
                ApplyViewMode(FolderViewMode.LargeIcons);
            else
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.Large);
        }

        private void ViewSizeExtraLarge_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentTab == null) return;
            if (CurrentTab.ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons)
                ApplyViewMode(FolderViewMode.ExtraLargeIcons);
            else
                ApplyViewMode(CurrentTab.ViewMode, ViewScaleLevel.ExtraLarge);
        }

        private void CheckShowItemCheckBoxes_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts && ts.IsOn != ConfigService.Current.Ui.ShowItemCheckBoxes)
            {
                ToggleShowItemCheckBoxes();
            }
        }

        private void MenuToggleCheckBoxes_Click(object sender, RoutedEventArgs e)
        {
            ToggleShowItemCheckBoxes();
        }

        private void CheckShowHiddenFiles_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts && ts.IsOn != ConfigService.Current.Ui.ShowHiddenFiles)
            {
                ToggleShowHiddenFiles();
            }
        }

        private void ViewMenuFlyout_Opening(object? sender, object e)
        {
            UpdateViewMenuCheckStates();
        }

        public void UpdateViewMenuCheckStates()
        {
            var mode = CurrentTab?.ViewMode ?? FolderViewMode.Details;
            var scale = CurrentTab?.ViewScale ?? ViewScaleLevel.Normal;

            ActionToolbar?.UpdateViewMenuState(mode, scale, ConfigService.Current.Ui.ShowItemCheckBoxes, ConfigService.Current.Ui.ShowHiddenFiles);

            // 背景右クリックメニュー
            if (MenuBgViewExtraLarge != null) MenuBgViewExtraLarge.IsChecked = mode == FolderViewMode.ExtraLargeIcons;
            if (MenuBgViewLarge != null) MenuBgViewLarge.IsChecked = mode == FolderViewMode.LargeIcons;
            if (MenuBgViewMedium != null) MenuBgViewMedium.IsChecked = mode == FolderViewMode.MediumIcons;
            if (MenuBgViewSmall != null) MenuBgViewSmall.IsChecked = mode == FolderViewMode.SmallIcons;
            if (MenuBgViewList != null) MenuBgViewList.IsChecked = mode == FolderViewMode.List;
            if (MenuBgViewDetails != null) MenuBgViewDetails.IsChecked = mode == FolderViewMode.Details;
            if (MenuBgViewTiles != null) MenuBgViewTiles.IsChecked = mode == FolderViewMode.Tiles;
            if (MenuBgViewContent != null) MenuBgViewContent.IsChecked = mode == FolderViewMode.Content;
            if (MenuBgShowCheckBoxes != null) MenuBgShowCheckBoxes.IsChecked = ConfigService.Current.Ui.ShowItemCheckBoxes;
            if (MenuBgShowHidden != null) MenuBgShowHidden.IsChecked = ConfigService.Current.Ui.ShowHiddenFiles;
        }

        #region Zoom (Ctrl + Wheel)

        private static readonly (FolderViewMode Mode, ViewScaleLevel Scale)[] FullZoomOrder =
        [
            (FolderViewMode.Details, ViewScaleLevel.Compact),
            (FolderViewMode.Details, ViewScaleLevel.Normal),
            (FolderViewMode.Details, ViewScaleLevel.Large),
            (FolderViewMode.List, ViewScaleLevel.Compact),
            (FolderViewMode.List, ViewScaleLevel.Normal),
            (FolderViewMode.List, ViewScaleLevel.Large),
            (FolderViewMode.SmallIcons, ViewScaleLevel.Compact),
            (FolderViewMode.SmallIcons, ViewScaleLevel.Normal),
            (FolderViewMode.SmallIcons, ViewScaleLevel.Large),
            (FolderViewMode.Tiles, ViewScaleLevel.Compact),
            (FolderViewMode.Tiles, ViewScaleLevel.Normal),
            (FolderViewMode.Tiles, ViewScaleLevel.Large),
            (FolderViewMode.MediumIcons, ViewScaleLevel.Compact),
            (FolderViewMode.MediumIcons, ViewScaleLevel.Normal),
            (FolderViewMode.MediumIcons, ViewScaleLevel.Large),
            (FolderViewMode.LargeIcons, ViewScaleLevel.Compact),
            (FolderViewMode.LargeIcons, ViewScaleLevel.Normal),
            (FolderViewMode.LargeIcons, ViewScaleLevel.Large),
            (FolderViewMode.ExtraLargeIcons, ViewScaleLevel.Compact),
            (FolderViewMode.ExtraLargeIcons, ViewScaleLevel.Normal),
            (FolderViewMode.ExtraLargeIcons, ViewScaleLevel.Large),
        ];

        private void FileListContainer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!IsCtrlPressed() || CurrentTab == null) return;

            var properties = e.GetCurrentPoint(sender as UIElement).Properties;
            int delta = properties.MouseWheelDelta;

            if (delta == 0) return;

            var currentMode = CurrentTab.ViewMode;
            var currentScale = CurrentTab.ViewScale;

            int currentIndex = Array.FindIndex(FullZoomOrder, z => z.Mode == currentMode && z.Scale == currentScale);
            if (currentIndex < 0)
            {
                currentIndex = Array.FindIndex(FullZoomOrder, z => z.Mode == currentMode);
                if (currentIndex < 0) currentIndex = 1;
            }

            if (delta > 0 && currentIndex < FullZoomOrder.Length - 1)
            {
                var next = FullZoomOrder[currentIndex + 1];
                ApplyViewMode(next.Mode, next.Scale);
                e.Handled = true;
            }
            else if (delta < 0 && currentIndex > 0)
            {
                var prev = FullZoomOrder[currentIndex - 1];
                ApplyViewMode(prev.Mode, prev.Scale);
                e.Handled = true;
            }
        }

        #endregion

        #endregion
    }
}
