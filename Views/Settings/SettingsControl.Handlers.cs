using System;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        #region 設定値の更新ハンドラー

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                ConfigService.Current.Ui.Theme = tag;
                ConfigService.Save();

                if (App.CurrentWindow is global::FastExplorer.MainWindow window)
                {
                    window.ApplyTheme(tag);
                }
            }
        }

        private void ShowItemCheckBoxesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ConfigService.Current.Ui.ShowItemCheckBoxes = ShowItemCheckBoxesToggle.IsOn;
            ConfigService.Save();
            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                window.ApplyItemCheckBoxesState();
                window.UpdateViewMenuCheckStates();
            }
        }

        private void ShowHiddenFilesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ConfigService.Current.Ui.ShowHiddenFiles = ShowHiddenFilesToggle.IsOn;
            ConfigService.Save();
        }

        private void ConfirmDeleteToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ConfigService.Current.Ui.ConfirmDelete = ConfirmDeleteToggle.IsOn;
            ConfigService.Save();
        }

        private void ToggleMenu_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var menu = ConfigService.Current.ShellMenu;
            menu.ShowOpenWith = ToggleOpenWith.IsOn;
            menu.ShowEditWithEditor = ToggleEditWithEditor.IsOn;
            menu.ShowOpenInTerminal = ToggleOpenInTerminal.IsOn;
            menu.ShowCopyPath = ToggleCopyPath.IsOn;
            menu.ShowZipOptions = ToggleZipOptions.IsOn;
            ZipLevelSettingsPanel.Visibility = ToggleZipOptions.IsOn ? Visibility.Visible : Visibility.Collapsed;
            menu.ShowProperties = ToggleProperties.IsOn;
            menu.ShowOsStandardOption = ToggleOsStandardOption.IsOn;
            menu.ShowAllShellItems = ToggleShowAllShellItems.IsOn;

            ConfigService.Save();
        }

        private bool _isSettingDefaultExplorer = false;

        private async void ToggleDefaultExplorer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _isSettingDefaultExplorer) return;

            _isSettingDefaultExplorer = true;
            bool isEnabled = ToggleDefaultExplorer.IsOn;

            // 連打防止のため UI トグルを即時無効化し、処理中表示を開始
            ToggleDefaultExplorer.IsEnabled = false;
            if (DefaultExplorerProgressRing != null)
            {
                DefaultExplorerProgressRing.IsActive = true;
                DefaultExplorerProgressRing.Visibility = Visibility.Visible;
            }
            if (DefaultExplorerStatusText != null)
            {
                DefaultExplorerStatusText.Text = isEnabled ? "既定に設定中..." : "既定を解除中...";
            }

            try
            {
                bool success = await System.Threading.Tasks.Task.Run(() =>
                {
                    return SystemIntegrationService.SetAsDefaultExplorer(isEnabled);
                });

                if (success)
                {
                    ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer = isEnabled;

                    // 既定の切り替えに伴い、右クリックメニュー連携および Win+E キー連動も自動で一括連動
                    SystemIntegrationService.SetContextMenuIntegration(isEnabled);
                    ConfigService.Current.SystemIntegration.AddContextMenuToFolders = isEnabled;
                    ConfigService.Current.SystemIntegration.InterceptWinE = isEnabled;

                    if (App.CurrentWindow is global::FastExplorer.MainWindow mw)
                    {
                        if (isEnabled)
                        {
                            SystemIntegrationService.RegisterWinEHotKey(mw.WindowHandle);
                        }
                        else
                        {
                            SystemIntegrationService.UnregisterWinEHotKey(mw.WindowHandle);
                        }
                    }

                    ConfigService.Save();
                }
                else
                {
                    _isInitializing = true;
                    ToggleDefaultExplorer.IsOn = !isEnabled;
                    _isInitializing = false;
                }

                // シェルや Watcher プロセスの安全な遷移・初期化完了を待機（最低 1.2 秒のクールダウン）
                await System.Threading.Tasks.Task.Delay(1200);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] ToggleDefaultExplorer_Toggled error: {ex.Message}");
                _isInitializing = true;
                ToggleDefaultExplorer.IsOn = !isEnabled;
                _isInitializing = false;
            }
            finally
            {
                UpdateDefaultExplorerStatusBadge(ToggleDefaultExplorer.IsOn);
                if (DefaultExplorerProgressRing != null)
                {
                    DefaultExplorerProgressRing.IsActive = false;
                    DefaultExplorerProgressRing.Visibility = Visibility.Collapsed;
                }
                ToggleDefaultExplorer.IsEnabled = true;
                _isSettingDefaultExplorer = false;
            }
        }

        private void ToggleDisableCopilotKey_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            bool isEnabled = ToggleDisableCopilotKey.IsOn;
            ConfigService.Current.SystemIntegration.DisableCopilotKey = isEnabled;
            SystemIntegrationService.UpdateKeyBlockingRegistry(isEnabled, ConfigService.Current.SystemIntegration.DisableCapsLockKey);
            ConfigService.Save();
        }

        private void ToggleDisableCapsLockKey_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            bool isEnabled = ToggleDisableCapsLockKey.IsOn;
            ConfigService.Current.SystemIntegration.DisableCapsLockKey = isEnabled;
            SystemIntegrationService.UpdateKeyBlockingRegistry(ConfigService.Current.SystemIntegration.DisableCopilotKey, isEnabled);
            ConfigService.Save();
        }

        private static int CompressionLevelToIndex(ArchiveCompressionLevel level) => level switch
        {
            ArchiveCompressionLevel.Ultra => 0,
            ArchiveCompressionLevel.Normal => 1,
            ArchiveCompressionLevel.Fast => 2,
            ArchiveCompressionLevel.Store => 3,
            _ => 1
        };

        private static ArchiveCompressionLevel IndexToCompressionLevel(int index) => index switch
        {
            0 => ArchiveCompressionLevel.Ultra,
            1 => ArchiveCompressionLevel.Normal,
            2 => ArchiveCompressionLevel.Fast,
            3 => ArchiveCompressionLevel.Store,
            _ => ArchiveCompressionLevel.Normal
        };

        private void CompressionLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            if (sender is ComboBox cb)
            {
                if (cb == ZipDefaultLevelComboBox && ZipDefaultLevelComboBox.SelectedIndex >= 0)
                {
                    ConfigService.Current.ShellMenu.DefaultZipLevel = IndexToCompressionLevel(ZipDefaultLevelComboBox.SelectedIndex);
                    ConfigService.Save();
                }
                else if (cb == SevenZipDefaultLevelComboBox && SevenZipDefaultLevelComboBox.SelectedIndex >= 0)
                {
                    ConfigService.Current.ShellMenu.DefaultSevenZipLevel = IndexToCompressionLevel(SevenZipDefaultLevelComboBox.SelectedIndex);
                    ConfigService.Save();
                }
            }
        }

        private void EditorPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ConfigService.Current.Editor.Path = EditorPathBox.Text.Trim();
            ConfigService.Save();
        }

        private void TerminalPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ConfigService.Current.Terminal.Path = TerminalPathBox.Text.Trim();
            ConfigService.Save();
        }

        private void BrowseEditorPath_Click(object sender, RoutedEventArgs e)
        {
            nint hwnd = (App.CurrentWindow is global::FastExplorer.MainWindow window) ? window.WindowHandle : 0;
            string? filePath = FastExplorer.Helpers.NativeFilePickerHelper.PickExecutable(hwnd, "テキストエディターの実行ファイルを選択");
            if (!string.IsNullOrEmpty(filePath))
            {
                EditorPathBox.Text = filePath;
                ConfigService.Current.Editor.Path = filePath;
                ConfigService.Save();
            }
        }

        private void BrowseTerminalPath_Click(object sender, RoutedEventArgs e)
        {
            nint hwnd = (App.CurrentWindow is global::FastExplorer.MainWindow window) ? window.WindowHandle : 0;
            string? filePath = FastExplorer.Helpers.NativeFilePickerHelper.PickExecutable(hwnd, "ターミナルの実行ファイルを選択");
            if (!string.IsNullOrEmpty(filePath))
            {
                TerminalPathBox.Text = filePath;
                ConfigService.Current.Terminal.Path = filePath;
                ConfigService.Save();
            }
        }

        private void MaxCacheMemoryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializing) return;
            if (!double.IsNaN(sender.Value) && sender.Value >= 10)
            {
                ConfigService.Current.Cache.MaxMemoryMB = (int)sender.Value;
                ConfigService.Save();
            }
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            IconThumbnailService.Instance.ClearCache();
            if (sender is Button btn)
            {
                btn.Content = "キャッシュをクリアしました ✓";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, ev) =>
                {
                    btn.Content = "キャッシュをクリア";
                    timer.Stop();
                };
                timer.Start();
            }
        }

        private async void ResetAllSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "すべての設定を初期化",
                    Content = "FastExplorer のすべての設定（外観、コンテキストメニュー、ショートカットキー、外部ツール等）を初期状態に戻しますか？",
                    PrimaryButtonText = "すべて初期化",
                    CloseButtonText = "キャンセル",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    ConfigService.ResetToDefaults();
                    LoadSettingsToUI();

                    if (App.CurrentWindow is global::FastExplorer.MainWindow window)
                    {
                        window.ApplyTheme(ConfigService.Current.Ui.Theme);
                        window.ApplyItemCheckBoxesState();
                        window.ApplyWallpaper();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] ResetAllSettings error: {ex.Message}");
            }
        }

        #endregion
    }
}
