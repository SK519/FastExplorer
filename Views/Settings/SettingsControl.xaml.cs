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

            // 壁紙
            WallpaperPathTextBox.Text = config.Ui.BackgroundImagePath ?? "";
            WallpaperOpacitySlider.Value = (int)Math.Round(config.Ui.BackgroundOpacity * 100);
            WallpaperOpacityValueText.Text = $"{(int)WallpaperOpacitySlider.Value}%";
            WallpaperTintSlider.Value = (int)Math.Round(config.Ui.BackgroundTintOpacity * 100);
            WallpaperTintValueText.Text = $"{(int)WallpaperTintSlider.Value}%";
            WallpaperFitComboBox.SelectedIndex = config.Ui.BackgroundFit switch
            {
                "Uniform" => 1,
                "Fill" => 2,
                "None" => 3,
                _ => 0
            };
            WallpaperOptionsPanel.Opacity = string.IsNullOrWhiteSpace(config.Ui.BackgroundImagePath) ? 0.6 : 1.0;

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

            // アップデート情報
            GitHubOwnerBox.Text = config.Update.GitHubOwner;
            GitHubRepoBox.Text = config.Update.GitHubRepo;
            CurrentVersionTextBlock.Text = $"現在のバージョン: v{FastExplorer.Services.Update.UpdateService.GetCurrentVersionString()}";
            AppAboutVersionText.Text = $"バージョン {FastExplorer.Services.Update.UpdateService.GetCurrentVersionString()} (WinUI 3 / Windows App SDK)";

            // システム連携
            bool isDef = SystemIntegrationService.IsDefaultExplorerEnabled();
            ToggleDefaultExplorer.IsOn = isDef;
            UpdateDefaultExplorerStatusBadge(isDef);
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
                    window.ApplyWallpaper();
                }
            }
        }

        #region アップデート機能ハンドラー

        private void UpdateRepoConfig_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ConfigService.Current.Update.GitHubOwner = GitHubOwnerBox.Text.Trim();
            ConfigService.Current.Update.GitHubRepo = GitHubRepoBox.Text.Trim();
            ConfigService.Save();
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            var config = ConfigService.Current.Update;
            string owner = config.GitHubOwner;
            string repo = config.GitHubRepo;

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                UpdateStatusTextBlock.Text = "リポジトリの所有者名・リポジトリ名を入力してください。";
                return;
            }

            CheckForUpdatesButton.IsEnabled = false;
            UpdateCheckProgressRing.IsActive = true;
            UpdateCheckProgressRing.Visibility = Visibility.Visible;
            UpdateStatusTextBlock.Text = "最新情報を確認中...";
            UpdateAvailableCard.Visibility = Visibility.Collapsed;

            try
            {
                var updateService = new FastExplorer.Services.Update.UpdateService();
                var info = await updateService.CheckForUpdatesAsync(owner, repo);

                UpdateStatusTextBlock.Text = $"最終確認: {DateTime.Now:HH:mm:ss}";

                if (!string.IsNullOrEmpty(info.ErrorMessage))
                {
                    UpdateStatusTextBlock.Text = info.ErrorMessage;
                }
                else if (info.IsUpdateAvailable)
                {
                    NewVersionTitleTextBlock.Text = $"新しいバージョン (v{info.LatestVersion}) が利用可能です！";
                    ReleaseNotesTextBlock.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "最新のインストーラーがリリースされています。" : info.ReleaseNotes;
                    InstallUpdateButton.Tag = info.DownloadUrl;
                    UpdateAvailableCard.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateStatusTextBlock.Text = $"お使いのバージョン (v{info.CurrentVersion}) は最新です。";
                }
            }
            catch (Exception ex)
            {
                UpdateStatusTextBlock.Text = $"確認エラー: {ex.Message}";
            }
            finally
            {
                UpdateCheckProgressRing.IsActive = false;
                UpdateCheckProgressRing.Visibility = Visibility.Collapsed;
                CheckForUpdatesButton.IsEnabled = true;
            }
        }

        private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (InstallUpdateButton.Tag is string downloadUrl && !string.IsNullOrEmpty(downloadUrl))
            {
                InstallUpdateButton.IsEnabled = false;
                UpdateDownloadProgressBar.Visibility = Visibility.Visible;
                UpdateDownloadProgressBar.Value = 0;

                var updateService = new FastExplorer.Services.Update.UpdateService();
                bool success = await updateService.DownloadAndInstallUpdateAsync(downloadUrl, progress =>
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateDownloadProgressBar.Value = progress;
                    });
                });

                if (!success)
                {
                    InstallUpdateButton.IsEnabled = true;
                    UpdateDownloadProgressBar.Visibility = Visibility.Collapsed;
                    UpdateStatusTextBlock.Text = "ダウンロードまたはインストーラー起動に失敗しました。";
                }
            }
            else
            {
                UpdateStatusTextBlock.Text = "ダウンロード URL が無効です。";
            }
        }

        #endregion

        #region キー入力補助方法

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

        #endregion
    }
}
