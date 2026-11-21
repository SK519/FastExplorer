using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Services;
using Microsoft.UI.Xaml;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Wallpaper & Background

        private static string? _cachedWallpaperPath;
        private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _cachedWallpaperBitmap;
        private string? _currentLoadedWallpaperPath;
        private CancellationTokenSource? _wallpaperEvalCts;

        private void ClearWallpaperVisuals(bool clearStaticCache)
        {
            BackgroundHostGrid.Visibility = Visibility.Collapsed;
            BackgroundImageHost.Source = null;
            _currentLoadedWallpaperPath = null;

            if (clearStaticCache)
            {
                _cachedWallpaperBitmap = null;
                _cachedWallpaperPath = null;
            }
        }

        public async void ApplyWallpaper()
        {
            _wallpaperEvalCts?.Cancel();
            _wallpaperEvalCts?.Dispose();
            _wallpaperEvalCts = new CancellationTokenSource();
            var ct = _wallpaperEvalCts.Token;

            try
            {
                var ui = ConfigService.Current.Ui;
                string path = ui.BackgroundImagePath;

                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    ClearWallpaperVisuals(clearStaticCache: true);
                    return;
                }

                if (ui.DisableWallpaperOutsideHome)
                {
                    var ssids = ui.HomeSsids ?? [];
                    var macs = ui.HomeMacs ?? [];
                    bool isAtHome = await Task.Run(
                        () => NetworkLocationService.IsAtHomeAsync(ssids, macs, maxRetries: 10, ct),
                        ct);

                    if (ct.IsCancellationRequested) return;

                    if (!isAtHome)
                    {
                        ClearWallpaperVisuals(clearStaticCache: true);
                        return;
                    }
                }

                // 不透明度・ティント・フィットを即時反映
                UpdateWallpaperProperties();

                // 画像の読み込み (同一パスの場合はキャッシュを即座に再利用)
                if (_currentLoadedWallpaperPath != path || BackgroundImageHost.Source == null)
                {
                    if (_cachedWallpaperPath == path && _cachedWallpaperBitmap != null)
                    {
                        BackgroundImageHost.Source = _cachedWallpaperBitmap;
                        _currentLoadedWallpaperPath = path;
                        BackgroundHostGrid.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        // 最適なデコード解像度を決定 (画面の解像度に合わせて無駄な巨大デコード時間を削減)
                        int targetDecodeWidth = 2560;
                        try
                        {
                            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                            if (displayArea != null && displayArea.OuterBounds.Width > 0)
                            {
                                targetDecodeWidth = Math.Clamp(displayArea.OuterBounds.Width, 1920, 3840);
                            }
                        }
                        catch { }

                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                        {
                            DecodePixelWidth = targetDecodeWidth
                        };

                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true))
                        {
                            var ras = fs.AsRandomAccessStream();
                            await bitmap.SetSourceAsync(ras);
                        }

                        if (ct.IsCancellationRequested) return;

                        _cachedWallpaperPath = path;
                        _cachedWallpaperBitmap = bitmap;
                        BackgroundImageHost.Source = bitmap;
                        _currentLoadedWallpaperPath = path;
                        BackgroundHostGrid.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    BackgroundHostGrid.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException)
            {
                // 新しい壁紙適用リクエストによりキャンセルされた場合は何もしない
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Wallpaper] Error applying wallpaper: {ex.Message}");
                BackgroundHostGrid.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateWallpaperProperties()
        {
            try
            {
                var ui = ConfigService.Current.Ui;

                // 不透明度 (Opacity)
                BackgroundImageHost.Opacity = Math.Clamp(ui.BackgroundOpacity, 0.0, 1.0);

                // フィット方式 (Stretch)
                BackgroundImageHost.Stretch = ui.BackgroundFit switch
                {
                    "Uniform" => Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    "Fill" => Microsoft.UI.Xaml.Media.Stretch.Fill,
                    "None" => Microsoft.UI.Xaml.Media.Stretch.None,
                    _ => Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                };

                // 背景ティントオーバーレイ
                BackgroundTintOverlay.Opacity = Math.Clamp(ui.BackgroundTintOpacity, 0.0, 1.0);
            }
            catch { }
        }

        #endregion
    }
}
