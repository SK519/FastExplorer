using System;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl : UserControl
    {
        private bool _isInitializing = true;
        private string _activeTabTag = "Theme";
        private bool _isUpdatingToggles = false;

        public SettingsControl()
        {
            this.InitializeComponent();
            InitAutoScrollTimer();
            LoadSettingsToUI();
            _isInitializing = false;
            UpdateTabVisuals("Theme");
        }

        public void ReloadSettings()
        {
            _isInitializing = true;
            ConfigService.Load(); // Re-read from disk in case it was modified externally
            LoadSettingsToUI();
            _isInitializing = false;
        }

        private void LoadSettingsToUI()
        {
            var config = ConfigService.Current;

            // テーマ ComboBox
            string theme = config.Ui.Theme.ToLowerInvariant();
            int selectedIndex = theme switch
            {
                "dark" => 1,
                "light" => 2,
                _ => 0
            };
            ThemeComboBox.SelectedIndex = selectedIndex;

            // 表示・削除 Toggle
            ShowItemCheckBoxesToggle.IsOn = config.Ui.ShowItemCheckBoxes;
            ShowHiddenFilesToggle.IsOn = config.Ui.ShowHiddenFiles;
            ConfirmDeleteToggle.IsOn = config.Ui.ConfirmDelete;

            // ショートカット一覧初期化
            InitShortcutsSection();

            // コンテキストメニュー項目 Toggle (標準)
            ToggleOpenWith.IsOn = config.ShellMenu.ShowOpenWith;
            ToggleEditWithEditor.IsOn = config.ShellMenu.ShowEditWithEditor;
            ToggleOpenInTerminal.IsOn = config.ShellMenu.ShowOpenInTerminal;
            ToggleCopyPath.IsOn = config.ShellMenu.ShowCopyPath;
            ToggleZipOptions.IsOn = config.ShellMenu.ShowZipOptions;
            ZipLevelSettingsPanel.Visibility = config.ShellMenu.ShowZipOptions ? Visibility.Visible : Visibility.Collapsed;
            ZipDefaultLevelComboBox.SelectedIndex = CompressionLevelToIndex(config.ShellMenu.DefaultZipLevel);
            SevenZipDefaultLevelComboBox.SelectedIndex = CompressionLevelToIndex(config.ShellMenu.DefaultSevenZipLevel);
            ToggleProperties.IsOn = config.ShellMenu.ShowProperties;
            ToggleOsStandardOption.IsOn = config.ShellMenu.ShowOsStandardOption;

            // コンテキストメニュー項目 Toggle (OSメニュー抽出拡張)
            ToggleShowAllShellItems.IsOn = config.ShellMenu.ShowAllShellItems;

            // 検出済み項目の動的トグルコントロールの生成
            EnsureMenuOrderInitialized();
            RenderDetectedItemsList();

            // エディタ・ターミナル
            EditorPathBox.Text = config.Editor.Path;
            TerminalPathBox.Text = config.Terminal.Path;

            // キャッシュ
            MaxCacheMemoryBox.Value = config.Cache.MaxMemoryMB;

            // システム連携
            bool isDef = SystemIntegrationService.IsDefaultExplorerEnabled();
            ToggleDefaultExplorer.IsOn = isDef;
            UpdateDefaultExplorerStatusBadge(isDef);
        }

        private void UpdateDefaultExplorerStatusBadge(bool isDefault)
        {
            if (DefaultExplorerStatusBadge != null && DefaultExplorerStatusText != null)
            {
                if (isDefault)
                {
                    DefaultExplorerStatusBadge.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                    DefaultExplorerStatusText.Text = "既定に設定中";
                }
                else
                {
                    DefaultExplorerStatusBadge.Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
                    DefaultExplorerStatusText.Text = "未設定 (Windows 標準)";
                }
            }
        }

        #region 縦タブ ナビゲーション

        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SwitchTab(tag);
            }
        }

        private void SwitchTab(string tag)
        {
            _activeTabTag = tag;
            UpdateTabVisuals(tag);

            SectionTheme.Visibility = tag == "Theme" ? Visibility.Visible : Visibility.Collapsed;
            SectionShortcuts.Visibility = tag == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
            SectionStandard.Visibility = tag == "Standard" ? Visibility.Visible : Visibility.Collapsed;
            SectionDetected.Visibility = tag == "Detected" ? Visibility.Visible : Visibility.Collapsed;
            SectionTools.Visibility = tag == "Tools" ? Visibility.Visible : Visibility.Collapsed;
            SectionIntegration.Visibility = tag == "Integration" ? Visibility.Visible : Visibility.Collapsed;
            SectionAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            if (tag == "Shortcuts")
            {
                RenderShortcutsList();
            }
            else if (tag == "Detected")
            {
                RenderDetectedItemsList(SearchFilterBox?.Text ?? "");
            }
        }

        private void UpdateTabVisuals(string activeTag)
        {
            SetButtonActiveStyle(TabBtnTheme, activeTag == "Theme");
            SetButtonActiveStyle(TabBtnShortcuts, activeTag == "Shortcuts");
            SetButtonActiveStyle(TabBtnStandard, activeTag == "Standard");
            SetButtonActiveStyle(TabBtnDetected, activeTag == "Detected");
            SetButtonActiveStyle(TabBtnTools, activeTag == "Tools");
            SetButtonActiveStyle(TabBtnIntegration, activeTag == "Integration");
            SetButtonActiveStyle(TabBtnAbout, activeTag == "About");
        }

        private static void SetButtonActiveStyle(Button btn, bool isActive)
        {
            if (btn == null) return;
            if (isActive)
            {
                btn.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
                btn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                btn.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                btn.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        #endregion

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

        private void ToggleDefaultExplorer_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool isEnabled = ToggleDefaultExplorer.IsOn;
            bool success = SystemIntegrationService.SetAsDefaultExplorer(isEnabled);
            if (success)
            {
                ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer = isEnabled;

                // 親トグルが変更された場合、子オプション（右クリック、Win+E）も連動
                if (isEnabled)
                {
                    // 一括で有効化
                    SystemIntegrationService.SetContextMenuIntegration(true);
                    ConfigService.Current.SystemIntegration.AddContextMenuToFolders = true;

                    ConfigService.Current.SystemIntegration.InterceptWinE = true;
                    if (App.CurrentWindow is global::FastExplorer.MainWindow mw)
                    {
                        SystemIntegrationService.RegisterWinEHotKey(mw.WindowHandle);
                    }
                }
                else
                {
                    // 一括で解除
                    SystemIntegrationService.SetContextMenuIntegration(false);
                    ConfigService.Current.SystemIntegration.AddContextMenuToFolders = false;

                    ConfigService.Current.SystemIntegration.InterceptWinE = false;
                    if (App.CurrentWindow is global::FastExplorer.MainWindow mw)
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
            UpdateDefaultExplorerStatusBadge(ToggleDefaultExplorer.IsOn);
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

        private async void BrowseEditorPath_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");

            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                EditorPathBox.Text = file.Path;
                ConfigService.Current.Editor.Path = file.Path;
                ConfigService.Save();
            }
        }

        private async void BrowseTerminalPath_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");

            if (App.CurrentWindow is global::FastExplorer.MainWindow window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                TerminalPathBox.Text = file.Path;
                ConfigService.Current.Terminal.Path = file.Path;
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
                }
            }
        }

        public static bool IsCtrlPressed()
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        public static bool IsShiftPressed()
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        public static bool IsAltPressed()
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        #endregion
    }
}
