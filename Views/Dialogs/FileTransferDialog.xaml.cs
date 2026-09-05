using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FastExplorer.Views.Dialogs
{
    public sealed partial class FileTransferDialog : UserControl
    {
        private readonly List<double> _speedHistory = [];
        private const int MaxHistoryPoints = 45;
        private bool _isDetailsExpanded = false;

        public FileTransferController? Controller { get; set; }
        public event Action? CancelRequested;

        public FileTransferDialog()
        {
            this.InitializeComponent();
        }

        public void ResetState(string title)
        {
            DialogTitleText.Text = title;
            PercentText.Text = "0%";
            ItemsSummaryText.Text = "準備中...";
            SpeedText.Text = "-- MB/秒";
            RemainingTimeText.Text = "残り時間を計算中...";
            CurrentFileNameText.Text = "準備中...";
            PathInfoText.Text = string.Empty;
            TransferProgressBar.IsIndeterminate = true;
            TransferProgressBar.Value = 0;
            IconPauseResume.Glyph = "\uE769"; // Pause icon

            _speedHistory.Clear();
            GraphPolyline.Points.Clear();
            GraphPolygon.Points.Clear();
            PeakSpeedText.Text = "-- MB/秒";
        }

        public void UpdateProgress(FileTransferProgress p)
        {
            DialogTitleText.Text = p.IsMove ? "アイテムを移動中" : "アイテムをコピー中";

            if (p.IsPaused)
            {
                PercentText.Text = $"{(int)p.ProgressPercentage}% (一時停止中)";
                SpeedText.Text = "一時停止中";
                RemainingTimeText.Text = "一時停止中";
                IconPauseResume.Glyph = "\uE768"; // Play icon
                ToolTipService.SetToolTip(BtnPauseResume, "再開");
                return;
            }

            IconPauseResume.Glyph = "\uE769"; // Pause icon
            ToolTipService.SetToolTip(BtnPauseResume, "一時停止");

            if (p.IsSizeCalculating && p.TotalBytes <= 0)
            {
                TransferProgressBar.IsIndeterminate = true;
                PercentText.Text = "計算中...";
            }
            else
            {
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = p.ProgressPercentage;
                PercentText.Text = $"{(int)p.ProgressPercentage}%";
            }

            // アイテム概要 (転送済みバイト / 合計バイト & 残り項目数)
            string transferredStr = FileTransferProgress.FormatBytes(p.BytesTransferred);
            string totalStr = (p.IsSizeCalculating && p.TotalBytes <= 0) ? "計算中..." : FileTransferProgress.FormatBytes(p.TotalBytes);
            int remainingFiles = Math.Max(0, p.TotalFiles - p.FilesTransferred);
            ItemsSummaryText.Text = (p.IsSizeCalculating && p.TotalBytes <= 0)
                ? $"{transferredStr} 転送済み (全体を計算中...)"
                : $"{transferredStr} / {totalStr} (残り {remainingFiles} 項目)";

            // 速度と残り時間
            SpeedText.Text = FileTransferProgress.FormatSpeed(p.BytesPerSecond);
            RemainingTimeText.Text = (p.BytesTransferred >= p.TotalBytes && p.TotalBytes > 0)
                ? "完了中..."
                : FileTransferProgress.FormatTimeRemaining(p.EstimatedTimeRemaining);

            // 現在処理中のファイル名とパス
            CurrentFileNameText.Text = string.IsNullOrEmpty(p.CurrentFileName) ? "処理中..." : p.CurrentFileName;
            if (!string.IsNullOrEmpty(p.SourceDirectory) && !string.IsNullOrEmpty(p.DestinationDirectory))
            {
                PathInfoText.Text = $"{p.SourceDirectory} ➔ {p.DestinationDirectory}";
            }

            // 速度履歴の記録とグラフ描画
            _speedHistory.Add(p.BytesPerSecond);
            if (_speedHistory.Count > MaxHistoryPoints)
            {
                _speedHistory.RemoveAt(0);
            }

            if (_isDetailsExpanded)
            {
                DrawGraph();
            }
        }

        private void DrawGraph()
        {
            double width = GraphCanvas.ActualWidth;
            double height = GraphCanvas.ActualHeight;
            if (width <= 10 || height <= 10 || _speedHistory.Count == 0) return;

            double maxSpeed = _speedHistory.Max();
            // 最小スケールを 10 MB/s に設定し、ピーク値に少しマージンを持たせる
            maxSpeed = Math.Max(10 * 1024 * 1024, maxSpeed * 1.15);

            PeakSpeedText.Text = FileTransferProgress.FormatSpeed(maxSpeed);

            var polylinePoints = new PointCollection();
            var polygonPoints = new PointCollection();

            double stepX = width / (MaxHistoryPoints - 1);
            int offset = MaxHistoryPoints - _speedHistory.Count;

            // ポリゴンの左下基準点
            polygonPoints.Add(new Point(offset * stepX, height));

            for (int i = 0; i < _speedHistory.Count; i++)
            {
                double speed = _speedHistory[i];
                double x = (offset + i) * stepX;
                double normalized = Math.Clamp(speed / maxSpeed, 0, 1.0);
                double y = height - (normalized * height);

                var pt = new Point(x, y);
                polylinePoints.Add(pt);
                polygonPoints.Add(pt);
            }

            // ポリゴンの右下基準点 (閉じた面を作成)
            double lastX = (offset + _speedHistory.Count - 1) * stepX;
            polygonPoints.Add(new Point(lastX, height));

            GraphPolyline.Points = polylinePoints;
            GraphPolygon.Points = polygonPoints;
        }

        private void BtnToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            _isDetailsExpanded = !_isDetailsExpanded;
            SpeedGraphSection.Visibility = _isDetailsExpanded ? Visibility.Visible : Visibility.Collapsed;
            IconDetailsChevron.Glyph = _isDetailsExpanded ? "\uE70E" : "\uE70D"; // ChevronUp : ChevronDown
            DetailsToggleText.Text = _isDetailsExpanded ? "詳細情報を非表示" : "詳細情報を表示";

            if (_isDetailsExpanded)
            {
                DrawGraph();
            }
        }

        private void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            Controller?.TogglePause();
            if (Controller?.IsPaused == true)
            {
                PercentText.Text += " (一時停止中)";
                SpeedText.Text = "一時停止中";
                RemainingTimeText.Text = "一時停止中";
                IconPauseResume.Glyph = "\uE768"; // Play icon
                ToolTipService.SetToolTip(BtnPauseResume, "再開");
            }
            else
            {
                IconPauseResume.Glyph = "\uE769"; // Pause icon
                ToolTipService.SetToolTip(BtnPauseResume, "一時停止");
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Controller?.Cancel();
            CancelRequested?.Invoke();
        }

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isDetailsExpanded)
            {
                DrawGraph();
            }
        }
    }
}
