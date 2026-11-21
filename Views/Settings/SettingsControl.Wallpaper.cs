using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FastExplorer.Services;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        #region 壁紙・背景設定

        private void BrowseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                nint hwnd = (App.CurrentWindow is global::FastExplorer.MainWindow window) ? window.WindowHandle : 0;
                string? filePath = FastExplorer.Helpers.NativeFilePickerHelper.PickWallpaperImage(hwnd);
                if (!string.IsNullOrEmpty(filePath))
                {
                    WallpaperPathTextBox.Text = filePath;
                    ConfigService.Current.Ui.BackgroundImagePath = filePath;
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
                window.UpdateWallpaperProperties();
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
                window.UpdateWallpaperProperties();
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
                    window.UpdateWallpaperProperties();
                }
            }
        }

        private void DisableWallpaperOutsideHomeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (OutsideHomeConfigPanel != null)
            {
                OutsideHomeConfigPanel.Visibility = DisableWallpaperOutsideHomeToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_isInitializing) return;

            ConfigService.Current.Ui.DisableWallpaperOutsideHome = DisableWallpaperOutsideHomeToggle.IsOn;
            ConfigService.Save();

            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                window.ApplyWallpaper();
            }
        }

        private void HomeNetworkTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            ConfigService.Current.Ui.HomeSsids = ParseListInput(HomeSsidsTextBox?.Text);
            ConfigService.Current.Ui.HomeMacs = ParseListInput(HomeMacsTextBox?.Text);
            ConfigService.Save();

            if (ConfigService.Current.Ui.DisableWallpaperOutsideHome &&
                App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                window.ApplyWallpaper();
            }
        }

        private void ImportWallpaperConfigToml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                nint hwnd = (App.CurrentWindow is global::FastExplorer.MainWindow window) ? window.WindowHandle : 0;
                string? tomlPath = FastExplorer.Helpers.NativeFilePickerHelper.PickSingleFile(
                    hwnd,
                    "config.toml を選択",
                    "TOML 設定ファイル (*.toml)\0*.toml\0すべてのファイル (*.*)\0*.*\0\0");

                if (string.IsNullOrEmpty(tomlPath)) return;

                if (NetworkLocationService.TryImportFromToml(tomlPath, out var ssids, out var macs))
                {
                    if (HomeSsidsTextBox != null) HomeSsidsTextBox.Text = string.Join(", ", ssids);
                    if (HomeMacsTextBox != null) HomeMacsTextBox.Text = string.Join(", ", macs);

                    ConfigService.Current.Ui.HomeSsids = ssids;
                    ConfigService.Current.Ui.HomeMacs = macs;
                    ConfigService.Save();

                    if (ConfigService.Current.Ui.DisableWallpaperOutsideHome &&
                        App.CurrentWindow is global::FastExplorer.MainWindow mainWin)
                    {
                        mainWin.ApplyWallpaper();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Wallpaper] Error importing config.toml: {ex.Message}");
            }
        }

        private static List<string> ParseListInput(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return raw
                .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion
    }
}
