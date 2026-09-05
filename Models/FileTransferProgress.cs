using System;
using System.Threading;

namespace FastExplorer.Models
{
    public class FileTransferProgress
    {
        public string CurrentFileName { get; set; } = string.Empty;
        public string SourceDirectory { get; set; } = string.Empty;
        public string DestinationDirectory { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long BytesTransferred { get; set; }
        public int TotalFiles { get; set; }
        public int FilesTransferred { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
        public bool IsPaused { get; set; }
        public bool IsCancelled { get; set; }
        public bool IsMove { get; set; }
        public bool IsSizeCalculating { get; set; }

        public double ProgressPercentage
        {
            get
            {
                // サイズ集計中の場合は、分母が未確定なため 100% に跳ね上がるのを防ぐ
                if (IsSizeCalculating)
                {
                    if (TotalBytes > 0)
                    {
                        double bytesPct = (double)BytesTransferred / TotalBytes * 100.0;
                        return Math.Clamp(bytesPct, 0, 99.0);
                    }
                    return 0;
                }

                if (TotalBytes > 0)
                {
                    double bytesPct = Math.Clamp((double)BytesTransferred / TotalBytes * 100.0, 0, 100.0);
                    if (TotalFiles > 0)
                    {
                        double filesPct = Math.Clamp((double)FilesTransferred / TotalFiles * 100.0, 0, 100.0);
                        return Math.Max(bytesPct, filesPct);
                    }
                    return bytesPct;
                }
                else if (TotalFiles > 0)
                {
                    return Math.Clamp((double)FilesTransferred / TotalFiles * 100.0, 0, 100.0);
                }
                return 0;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0} B/秒";
            if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024.0:F1} KB/秒";
            if (bytesPerSecond < 1024 * 1024 * 1024) return $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB/秒";
            return $"{bytesPerSecond / (1024.0 * 1024.0 * 1024.0):F2} GB/秒";
        }

        public static string FormatTimeRemaining(TimeSpan ts)
        {
            if (ts.TotalSeconds <= 1) return "残り 数秒";
            if (ts.TotalSeconds < 60) return $"残り 約{(int)ts.TotalSeconds}秒";
            if (ts.TotalMinutes < 60) return $"残り 約{(int)ts.TotalMinutes}分 {ts.Seconds}秒";
            return $"残り 約{(int)ts.TotalHours}時間 {ts.Minutes}分";
        }
    }

    public class FileTransferController
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private bool _isPaused = false;

        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancelled => _cts.IsCancellationRequested;
        public bool IsPaused => _isPaused;

        public void Pause()
        {
            _isPaused = true;
            _pauseEvent.Reset();
        }

        public void Resume()
        {
            _isPaused = false;
            _pauseEvent.Set();
        }

        public void TogglePause()
        {
            if (_isPaused) Resume();
            else Pause();
        }

        public void Cancel()
        {
            _cts.Cancel();
            _pauseEvent.Set(); // unblock if paused so cancel proceeds
        }

        public void WaitIfPaused()
        {
            _pauseEvent.Wait(_cts.Token);
        }
    }
}
