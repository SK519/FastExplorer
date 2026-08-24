using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer.Views.Dialogs
{
    public enum ConflictResolution
    {
        Cancel,
        Replace,
        Skip,
        KeepBoth
    }

    public sealed partial class FileConflictDialog : ContentDialog
    {
        public ConflictResolution Result { get; private set; } = ConflictResolution.Cancel;
        public bool ApplyToAll => ApplyToAllCheckBox.IsChecked == true;

        public FileConflictDialog(string sourcePath, string destPath)
        {
            this.InitializeComponent();

            string fileName = Path.GetFileName(destPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = Path.GetFileName(sourcePath);
            }

            MessageTextBlock.Text = $"宛先フォルダーには既に「{fileName}」という名前のファイルが存在します。";
            SourceFileNameText.Text = Path.GetFileName(sourcePath);
            DestFileNameText.Text = fileName;

            // ソースファイル情報
            try
            {
                if (File.Exists(sourcePath))
                {
                    var srcInfo = new FileInfo(sourcePath);
                    SourceFileInfoText.Text = $"コピー元: {FormatSize(srcInfo.Length)} - {srcInfo.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
                }
                else if (Directory.Exists(sourcePath))
                {
                    var srcInfo = new DirectoryInfo(sourcePath);
                    SourceFileInfoText.Text = $"コピー元フォルダー - {srcInfo.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
                }
            }
            catch
            {
                SourceFileInfoText.Text = "コピー元";
            }

            // 宛先ファイル情報
            try
            {
                if (File.Exists(destPath))
                {
                    var destInfo = new FileInfo(destPath);
                    DestFileInfoText.Text = $"宛先 (既存): {FormatSize(destInfo.Length)} - {destInfo.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
                }
                else if (Directory.Exists(destPath))
                {
                    var destInfo = new DirectoryInfo(destPath);
                    DestFileInfoText.Text = $"宛先フォルダー (既存) - {destInfo.LastWriteTime:yyyy/MM/dd HH:mm:ss}";
                }
            }
            catch
            {
                DestFileInfoText.Text = "宛先 (既存)";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        private void BtnReplace_Click(object sender, RoutedEventArgs e)
        {
            Result = ConflictResolution.Replace;
            this.Hide();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            Result = ConflictResolution.Skip;
            this.Hide();
        }

        private void BtnKeepBoth_Click(object sender, RoutedEventArgs e)
        {
            Result = ConflictResolution.KeepBoth;
            this.Hide();
        }
    }
}
