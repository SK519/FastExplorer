using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FastExplorer.Services;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        #region 壁紙・背景設定

        private async void BrowseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".gif");

                if (App.CurrentWindow is global::FastExplorer.MainWindow window)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
                }

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    WallpaperPathTextBox.Text = file.Path;
                    ConfigService.Current.Ui.BackgroundImagePath = file.Path;
                    ConfigService.Save();

                    WallpaperOptionsPanel.Opacity = 1.0;

                    if (App.CurrentWindow is global::FastExplorer.MainWindow mainWin)
                    {
                        mainWin.ApplyWallpaper();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Wallpaper] Error browsing file: {ex.Message}");
            }
        }

        private void ClearWallpaper_Click(object sender, RoutedEventArgs e)
        {
            WallpaperPathTextBox.Text = "";
            ConfigService.Current.Ui.BackgroundImagePath = "";
            ConfigService.Save();

            WallpaperOptionsPanel.Opacity = 0.6;

            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                window.ApplyWallpaper();
            }
        }

        private void WallpaperOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (WallpaperOpacityValueText != null)
            {
                WallpaperOpacityValueText.Text = $"{(int)e.NewValue}%";
            }
            if (_isInitializing) return;

            ConfigService.Current.Ui.BackgroundOpacity = e.NewValue / 100.0;
            ConfigService.Save();

            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                window.ApplyWallpaper();
            }
        }

        private void WallpaperTintSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (WallpaperTintValueText != null)
            {
                WallpaperTintValueText.Text = $"{(int)e.NewValue}%";
            }
            if (_isInitializing) return;

            ConfigService.Current.Ui.BackgroundTintOpacity = e.NewValue / 100.0;
            ConfigService.Save();

            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                window.ApplyWallpaper();
            }
        }

        private void WallpaperFitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (WallpaperFitComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                ConfigService.Current.Ui.BackgroundFit = tag;
                ConfigService.Save();

                if (App.CurrentWindow is global::FastExplorer.MainWindow window)
                {
                    window.ApplyWallpaper();
                }
            }
        }

        #endregion
    }
}
