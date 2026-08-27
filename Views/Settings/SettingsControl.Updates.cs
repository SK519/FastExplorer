using System;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.System;
using FastExplorer.Services;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        #region アップデート機能ハンドラー

        private void UpdateUpdateSectionUI()
        {
            if (CurrentVersionTextBlock != null)
                CurrentVersionTextBlock.Text = $"現在のバージョン: v{FastExplorer.Services.Update.UpdateService.GetCurrentVersionString()}";
            if (AppAboutVersionText != null)
                AppAboutVersionText.Text = $"バージョン {FastExplorer.Services.Update.UpdateService.GetCurrentVersionString()} (WinUI 3 / Windows App SDK)";

            if (UpdateStatusTextBlock != null)
            {
                if (FastExplorer.Services.Update.UpdateService.LastCheckedTime.HasValue)
                {
                    var lastTime = FastExplorer.Services.Update.UpdateService.LastCheckedTime.Value;
                    var lastInfo = FastExplorer.Services.Update.UpdateService.LastUpdateInfo;
                    if (lastInfo != null && !string.IsNullOrEmpty(lastInfo.ErrorMessage))
                    {
                        UpdateStatusTextBlock.Text = $"最終確認: {lastTime:yyyy/MM/dd HH:mm:ss} ({lastInfo.ErrorMessage})";
                    }
                    else if (lastInfo != null && lastInfo.IsUpdateAvailable)
                    {
                        UpdateStatusTextBlock.Text = $"最終確認: {lastTime:yyyy/MM/dd HH:mm:ss}";
                        if (NewVersionTitleTextBlock != null)
                            NewVersionTitleTextBlock.Text = $"新しいバージョン (v{lastInfo.LatestVersion}) が利用可能です！";
                        if (ReleaseNotesTextBlock != null)
                            ReleaseNotesTextBlock.Text = string.IsNullOrWhiteSpace(lastInfo.ReleaseNotes) ? "最新のインストーラーがリリースされています。" : lastInfo.ReleaseNotes;
                        if (InstallUpdateButton != null)
                            InstallUpdateButton.Tag = lastInfo.DownloadUrl;
                        if (UpdateAvailableCard != null)
                            UpdateAvailableCard.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        UpdateStatusTextBlock.Text = $"最終確認: {lastTime:yyyy/MM/dd HH:mm:ss} (お使いのバージョンは最新です)";
                        if (UpdateAvailableCard != null)
                            UpdateAvailableCard.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    UpdateStatusTextBlock.Text = "最終確認: 未確認";
                    if (UpdateAvailableCard != null)
                        UpdateAvailableCard.Visibility = Visibility.Collapsed;
                }
            }
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            var config = ConfigService.Current.Update;
            string owner = config.GitHubOwner ?? "SK519";
            string repo = config.GitHubRepo ?? "FastExplorer";

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
            // すでにダウンロード済みの場合はインストール実行
            if (!string.IsNullOrEmpty(FastExplorer.Services.Update.UpdateService.DownloadedInstallerPath) &&
                File.Exists(FastExplorer.Services.Update.UpdateService.DownloadedInstallerPath))
            {
                FastExplorer.Services.Update.UpdateService.LaunchInstallerAndExit();
                return;
            }

            if (InstallUpdateButton.Tag is string downloadUrl && !string.IsNullOrEmpty(downloadUrl))
            {
                InstallUpdateButton.IsEnabled = false;
                UpdateDownloadProgressBar.Visibility = Visibility.Visible;
                UpdateDownloadProgressBar.Value = 0;
                if (InstallUpdateText != null) InstallUpdateText.Text = "ダウンロード中...";

                var updateService = new FastExplorer.Services.Update.UpdateService();
                var path = await updateService.DownloadUpdateAsync(downloadUrl, progress =>
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateDownloadProgressBar.Value = progress;
                        if (InstallUpdateText != null)
                        {
                            InstallUpdateText.Text = $"ダウンロード中 {(int)progress}%";
                        }
                    });
                });

                InstallUpdateButton.IsEnabled = true;
                if (!string.IsNullOrEmpty(path))
                {
                    if (InstallUpdateIcon != null) InstallUpdateIcon.Glyph = "\uE777";
                    if (InstallUpdateText != null) InstallUpdateText.Text = "今すぐインストールして再起動";
                    UpdateStatusTextBlock.Text = "ダウンロードが完了しました。「今すぐインストールして再起動」をクリックしてください。";
                }
                else
                {
                    if (InstallUpdateText != null) InstallUpdateText.Text = "ダウンロードして更新";
                    UpdateDownloadProgressBar.Visibility = Visibility.Collapsed;
                    UpdateStatusTextBlock.Text = "ダウンロードに失敗しました。ネットワーク接続をご確認ください。";
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
    }
}
