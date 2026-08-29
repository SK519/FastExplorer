using System;
using System.IO;
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
            try
            {
                this.InitializeComponent();
                InitAutoScrollTimer();
                LoadSettingsToUI();
                _isInitializing = false;
                SwitchTab("Theme");

                FastExplorer.Services.Update.UpdateService.UpdateStatusChanged += OnGlobalUpdateStatusChanged;
                this.Unloaded += (s, e) =>
                {
                    FastExplorer.Services.Update.UpdateService.UpdateStatusChanged -= OnGlobalUpdateStatusChanged;
                };
            }
            catch (Exception ex)
            {
                try
                {
                    string localFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                    Directory.CreateDirectory(localFolder);
                    string crashLog = System.IO.Path.Combine(localFolder, "crash.log");
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SettingsControl constructor Exception: {ex}\r\n\r\n");
                }
                catch { }
            }
        }

        private void OnGlobalUpdateStatusChanged(FastExplorer.Services.Update.UpdateInfo info)
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                UpdateUpdateSectionUI();
            });
        }

        public void ReloadSettings()
        {
            try
            {
                _isInitializing = true;
                ConfigService.Load(); // Re-read from disk in case it was modified externally
                LoadSettingsToUI();
                _isInitializing = false;
                SwitchTab(_activeTabTag);
            }
            catch (Exception ex)
            {
                try
                {
                    string localFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                    Directory.CreateDirectory(localFolder);
                    string crashLog = System.IO.Path.Combine(localFolder, "crash.log");
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ReloadSettings Exception: {ex}\r\n\r\n");
                }
                catch { }
            }
        }

        private void LoadSettingsToUI()
        {
            try
            {
                var config = ConfigService.Current;

                // テーマ ComboBox
                string theme = config.Ui.Theme?.ToLowerInvariant() ?? "system";
                int selectedIndex = theme switch
                {
                    "dark" => 1,
                    "light" => 2,
                    _ => 0
                };
                if (ThemeComboBox != null) ThemeComboBox.SelectedIndex = selectedIndex;

                // 表示・削除 Toggle
                if (ShowItemCheckBoxesToggle != null) ShowItemCheckBoxesToggle.IsOn = config.Ui.ShowItemCheckBoxes;
                if (ShowHiddenFilesToggle != null) ShowHiddenFilesToggle.IsOn = config.Ui.ShowHiddenFiles;
                if (ConfirmDeleteToggle != null) ConfirmDeleteToggle.IsOn = config.Ui.ConfirmDelete;

                // 壁紙
                if (WallpaperPathTextBox != null) WallpaperPathTextBox.Text = config.Ui.BackgroundImagePath ?? "";
                if (WallpaperOpacitySlider != null) WallpaperOpacitySlider.Value = (int)Math.Round(config.Ui.BackgroundOpacity * 100);
                if (WallpaperOpacityValueText != null) WallpaperOpacityValueText.Text = $"{(int)(WallpaperOpacitySlider?.Value ?? 35)}%";
                if (WallpaperTintSlider != null) WallpaperTintSlider.Value = (int)Math.Round(config.Ui.BackgroundTintOpacity * 100);
                if (WallpaperTintValueText != null) WallpaperTintValueText.Text = $"{(int)(WallpaperTintSlider?.Value ?? 30)}%";
                if (WallpaperFitComboBox != null)
                {
                    WallpaperFitComboBox.SelectedIndex = (config.Ui.BackgroundFit ?? "UniformToFill") switch
                    {
                        "Uniform" => 1,
                        "Fill" => 2,
                        "None" => 3,
                        _ => 0
                    };
                }
                if (WallpaperOptionsPanel != null) WallpaperOptionsPanel.Opacity = string.IsNullOrWhiteSpace(config.Ui.BackgroundImagePath) ? 0.6 : 1.0;

                // ショートカット一覧初期化
                InitShortcutsSection();

                // コンテキストメニュー項目 Toggle (標準)
                if (ToggleOpenWith != null) ToggleOpenWith.IsOn = config.ShellMenu.ShowOpenWith;
                if (ToggleEditWithEditor != null) ToggleEditWithEditor.IsOn = config.ShellMenu.ShowEditWithEditor;
                if (ToggleOpenInTerminal != null) ToggleOpenInTerminal.IsOn = config.ShellMenu.ShowOpenInTerminal;
                if (ToggleCopyPath != null) ToggleCopyPath.IsOn = config.ShellMenu.ShowCopyPath;
                if (ToggleZipOptions != null) ToggleZipOptions.IsOn = config.ShellMenu.ShowZipOptions;
                if (ZipLevelSettingsPanel != null) ZipLevelSettingsPanel.Visibility = config.ShellMenu.ShowZipOptions ? Visibility.Visible : Visibility.Collapsed;
                if (ZipDefaultLevelComboBox != null) ZipDefaultLevelComboBox.SelectedIndex = CompressionLevelToIndex(config.ShellMenu.DefaultZipLevel);
                if (SevenZipDefaultLevelComboBox != null) SevenZipDefaultLevelComboBox.SelectedIndex = CompressionLevelToIndex(config.ShellMenu.DefaultSevenZipLevel);
                if (ToggleProperties != null) ToggleProperties.IsOn = config.ShellMenu.ShowProperties;
                if (ToggleOsStandardOption != null) ToggleOsStandardOption.IsOn = config.ShellMenu.ShowOsStandardOption;

                // コンテキストメニュー項目 Toggle (OSメニュー抽出拡張)
                if (ToggleShowAllShellItems != null) ToggleShowAllShellItems.IsOn = config.ShellMenu.ShowAllShellItems;

                // 検出済み項目の動的トグルコントロールの生成
                EnsureMenuOrderInitialized();
                RenderDetectedItemsList();

                // エディタ・ターミナル
                if (EditorPathBox != null) EditorPathBox.Text = config.Editor.Path ?? "";
                if (TerminalPathBox != null) TerminalPathBox.Text = config.Terminal.Path ?? "";

                // キャッシュ
                if (MaxCacheMemoryBox != null) MaxCacheMemoryBox.Value = config.Cache.MaxMemoryMB;

                // アップデート情報
                UpdateUpdateSectionUI();

                // システム連携
                bool isDef = SystemIntegrationService.IsDefaultExplorerEnabled();
                if (ToggleDefaultExplorer != null) ToggleDefaultExplorer.IsOn = isDef;
                UpdateDefaultExplorerStatusBadge(isDef);
            }
            catch (Exception ex)
            {
                try
                {
                    string localFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                    System.IO.Directory.CreateDirectory(localFolder);
                    string crashLog = System.IO.Path.Combine(localFolder, "crash.log");
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] LoadSettingsToUI Exception: {ex}\r\n\r\n");
                }
                catch { }
            }
        }

        public static Brush GetThemeBrush(string key, Brush? fallback = null)
        {
            try
            {
                if (Application.Current?.Resources != null)
                {
                    if (Application.Current.Resources.TryGetValue(key, out var res) && res is Brush b)
                        return b;

                    var themeDicts = Application.Current.Resources.ThemeDictionaries;
                    if (themeDicts != null)
                    {
                        string themeKey = Application.Current.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light";
                        if (themeDicts.TryGetValue(themeKey, out var tDictObj) && tDictObj is ResourceDictionary tDict)
                        {
                            if (tDict.TryGetValue(key, out var tb) && tb is Brush b2)
                                return b2;
                        }
                        if (themeDicts.TryGetValue("Default", out var dDictObj) && dDictObj is ResourceDictionary dDict)
                        {
                            if (dDict.TryGetValue(key, out var tb) && tb is Brush b3)
                                return b3;
                        }
                    }
                }
            }
            catch { }

            return fallback ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public static Style? GetThemeStyle(string key)
        {
            try
            {
                if (Application.Current?.Resources != null &&
                    Application.Current.Resources.TryGetValue(key, out var res) && res is Style s)
                {
                    return s;
                }
            }
            catch { }
            return null;
        }

        private void UpdateDefaultExplorerStatusBadge(bool isDefault)
        {
            if (DefaultExplorerStatusBadge != null && DefaultExplorerStatusText != null)
            {
                if (isDefault)
                {
                    DefaultExplorerStatusBadge.Background = GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue));
                    DefaultExplorerStatusText.Text = "既定に設定中";
                }
                else
                {
                    DefaultExplorerStatusBadge.Background = GetThemeBrush("CardStrokeColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray));
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

        public void NavigateToSection(string tag)
        {
            SwitchTab(tag);
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

            if (tag == "About")
            {
                UpdateUpdateSectionUI();
            }
            else if (tag == "Shortcuts")
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
                btn.Background = GetThemeBrush("SubtleFillColorSecondaryBrush", new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)));
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
                    window.ApplyWallpaper();
                }
            }
        }

        #endregion
    }
}
