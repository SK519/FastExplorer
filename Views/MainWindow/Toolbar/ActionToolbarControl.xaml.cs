using System;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer.Views.MainWindow.Toolbar
{
    public sealed partial class ActionToolbarControl : UserControl
    {
        public event RoutedEventHandler? NewFolderRequested;
        public event RoutedEventHandler? NewTextFileRequested;
        public event RoutedEventHandler? CutRequested;
        public event RoutedEventHandler? CopyRequested;
        public event RoutedEventHandler? PasteRequested;
        public event RoutedEventHandler? RenameRequested;
        public event RoutedEventHandler? DeleteRequested;
        public event RoutedEventHandler? PropertiesRequested;
        public event RoutedEventHandler? RestoreRequested;
        public event RoutedEventHandler? EmptyRecycleBinRequested;
        public event RoutedEventHandler? TogglePreviewRequested;
        public event RoutedEventHandler? ViewModeDetailsRequested;
        public event RoutedEventHandler? ViewModeListRequested;
        public event RoutedEventHandler? ViewModeContentRequested;
        public event RoutedEventHandler? ViewModeGridRequested;
        public event RoutedEventHandler? ViewModeTilesRequested;
        public event RoutedEventHandler? ViewSizeSmallRequested;
        public event RoutedEventHandler? ViewSizeMediumRequested;
        public event RoutedEventHandler? ViewSizeLargeRequested;
        public event RoutedEventHandler? ViewSizeExtraLargeRequested;
        public event Action<bool>? ShowCheckBoxesChanged;
        public event Action<bool>? ShowHiddenFilesChanged;
        public event Action<System.Collections.Generic.IList<MenuFlyoutItemBase>>? NewMenuOpening;

        public ActionToolbarControl()
        {
            this.InitializeComponent();
        }

        public void UpdateButtonsState(bool hasSelection, bool isSingle, bool isThisPC, bool canPaste, bool isRecycleBin = false)
        {
            if (isRecycleBin)
            {
                StandardToolbarPanel.Visibility = Visibility.Collapsed;
                RecycleBinToolbarPanel.Visibility = Visibility.Visible;

                ToolbarBtnRestore.IsEnabled = hasSelection;
                ToolbarBtnRecycleDelete.IsEnabled = hasSelection;
                ToolbarBtnRecycleProperties.IsEnabled = hasSelection;
            }
            else
            {
                StandardToolbarPanel.Visibility = Visibility.Visible;
                RecycleBinToolbarPanel.Visibility = Visibility.Collapsed;

                ToolbarBtnCut.IsEnabled = hasSelection && !isThisPC;
                ToolbarBtnCopy.IsEnabled = hasSelection;
                ToolbarBtnPaste.IsEnabled = canPaste && !isThisPC;
                ToolbarBtnRename.IsEnabled = isSingle && !isThisPC;
                ToolbarBtnDelete.IsEnabled = hasSelection && !isThisPC;
                ToolbarBtnProperties.IsEnabled = hasSelection;
            }
        }

        private void NewItemFlyout_Opening(object? sender, object e)
        {
            if (sender is MenuFlyout flyout)
            {
                NewMenuOpening?.Invoke(flyout.Items);
            }
        }

        private FolderViewMode _currentMode = FolderViewMode.Details;
        private ViewScaleLevel _currentScale = ViewScaleLevel.Normal;

        public void UpdateViewMenuState(FolderViewMode mode, ViewScaleLevel scale, bool showCheckBoxes, bool showHidden)
        {
            _currentMode = mode;
            _currentScale = scale;

            bool isGridMode = mode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons;

            // レイアウトボタンの選択状態ハイライト
            SetFlyoutButtonActive(BtnFlyoutDetails, mode == FolderViewMode.Details);
            SetFlyoutButtonActive(BtnFlyoutList, mode == FolderViewMode.List);
            SetFlyoutButtonActive(BtnFlyoutContent, mode == FolderViewMode.Content);
            SetFlyoutButtonActive(BtnFlyoutGrid, isGridMode);
            SetFlyoutButtonActive(BtnFlyoutTiles, mode == FolderViewMode.Tiles);

            // サイズボタンの選択状態ハイライト
            SetFlyoutButtonActive(BtnSizeSmall, mode == FolderViewMode.SmallIcons);
            SetFlyoutButtonActive(BtnSizeMedium, mode == FolderViewMode.MediumIcons);
            SetFlyoutButtonActive(BtnSizeLarge, mode == FolderViewMode.LargeIcons);
            SetFlyoutButtonActive(BtnSizeExtraLarge, mode == FolderViewMode.ExtraLargeIcons);

            if (CheckShowItemCheckBoxes != null) CheckShowItemCheckBoxes.IsOn = showCheckBoxes;
            if (CheckShowHiddenFiles != null) CheckShowHiddenFiles.IsOn = showHidden;
        }

        private static void SetFlyoutButtonActive(Button? button, bool isActive)
        {
            if (button == null) return;
            if (isActive)
            {
                button.Background = Views.Settings.SettingsControl.GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue));
                button.Foreground = Views.Settings.SettingsControl.GetThemeBrush("TextOnAccentFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.White));
                button.BorderBrush = Views.Settings.SettingsControl.GetThemeBrush("AccentFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue));
            }
            else
            {
                button.Background = Views.Settings.SettingsControl.GetThemeBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent));
                button.Foreground = Views.Settings.SettingsControl.GetThemeBrush("TextFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.White));
                button.BorderBrush = Views.Settings.SettingsControl.GetThemeBrush("ControlElevationBorderBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray));
            }
        }

        private void ViewMenuFlyout_Opening(object? sender, object e)
        {
            UpdateViewMenuState(_currentMode, _currentScale, ConfigService.Current.Ui.ShowItemCheckBoxes, ConfigService.Current.Ui.ShowHiddenFiles);
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e) => NewFolderRequested?.Invoke(sender, e);
        private void NewTextFile_Click(object sender, RoutedEventArgs e) => NewTextFileRequested?.Invoke(sender, e);
        private void Cut_Click(object sender, RoutedEventArgs e) => CutRequested?.Invoke(sender, e);
        private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(sender, e);
        private void Paste_Click(object sender, RoutedEventArgs e) => PasteRequested?.Invoke(sender, e);
        private void Rename_Click(object sender, RoutedEventArgs e) => RenameRequested?.Invoke(sender, e);
        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(sender, e);
        private void Properties_Click(object sender, RoutedEventArgs e) => PropertiesRequested?.Invoke(sender, e);
        private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(sender, e);
        private void EmptyRecycleBin_Click(object sender, RoutedEventArgs e) => EmptyRecycleBinRequested?.Invoke(sender, e);
        private void TogglePreview_Click(object sender, RoutedEventArgs e) => TogglePreviewRequested?.Invoke(sender, e);

        private void ViewModeDetails_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewModeDetailsRequested?.Invoke(sender, e); }
        private void ViewModeList_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewModeListRequested?.Invoke(sender, e); }
        private void ViewModeContent_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewModeContentRequested?.Invoke(sender, e); }
        private void ViewModeGrid_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewModeGridRequested?.Invoke(sender, e); }
        private void ViewModeTiles_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewModeTilesRequested?.Invoke(sender, e); }

        private void ViewSizeSmall_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewSizeSmallRequested?.Invoke(sender, e); }
        private void ViewSizeMedium_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewSizeMediumRequested?.Invoke(sender, e); }
        private void ViewSizeLarge_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewSizeLargeRequested?.Invoke(sender, e); }
        private void ViewSizeExtraLarge_Click(object sender, RoutedEventArgs e) { ToolbarViewFlyout.Hide(); ViewSizeExtraLargeRequested?.Invoke(sender, e); }

        private void CheckShowItemCheckBoxes_Toggled(object sender, RoutedEventArgs e)
        {
            ShowCheckBoxesChanged?.Invoke(CheckShowItemCheckBoxes.IsOn);
        }

        private void CheckShowHiddenFiles_Toggled(object sender, RoutedEventArgs e)
        {
            ShowHiddenFilesChanged?.Invoke(CheckShowHiddenFiles.IsOn);
        }
    }
}
