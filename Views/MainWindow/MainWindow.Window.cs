using System;
using System.Diagnostics;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Helpers;
using FastExplorer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Window Setup & TitleBar

        private Windows.Foundation.Point _windowDragStartPos;
        private bool _isWindowDragPending;

        private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var prop = e.GetCurrentPoint(sender as UIElement).Properties;
            if (prop.IsLeftButtonPressed)
            {
                _isWindowDragPending = true;
                _windowDragStartPos = e.GetCurrentPoint(null).Position;
                if (sender is UIElement el)
                {
                    el.CapturePointer(e.Pointer);
                }
            }
        }

        private void TitleBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isWindowDragPending) return;

            var prop = e.GetCurrentPoint(sender as UIElement).Properties;
            if (!prop.IsLeftButtonPressed)
            {
                _isWindowDragPending = false;
                if (sender is UIElement el)
                {
                    el.ReleasePointerCapture(e.Pointer);
                }
                return;
            }

            var currentPos = e.GetCurrentPoint(null).Position;
            double dx = currentPos.X - _windowDragStartPos.X;
            double dy = currentPos.Y - _windowDragStartPos.Y;

            // 4px 以上動かした場合のみウィンドウドラッグを開始 (単押しクリックでは絶対に追従しない)
            if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
            {
                _isWindowDragPending = false;
                if (sender is UIElement el)
                {
                    try { el.ReleasePointerCapture(e.Pointer); } catch { }
                }
                Win32Interop.ReleaseCapture();

                // 最大化状態の場合は、まず元に戻して（Restore）マウス位置にウィンドウを移動してからドラッグ開始
                if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                    if (Win32Interop.GetCursorPos(out var screenPt))
                    {
                        var size = AppWindow.Size;
                        int newX = Math.Max(0, screenPt.X - size.Width / 3);
                        int newY = Math.Max(0, screenPt.Y - 20);
                        AppWindow.Move(new Windows.Graphics.PointInt32(newX, newY));
                    }
                }

                Win32Interop.SendMessage(WindowHandle, Win32Interop.WM_SYSCOMMAND, (nuint)0xF012, 0);
            }
        }

        private void TitleBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isWindowDragPending = false;
            if (sender is UIElement el)
            {
                try { el.ReleasePointerCapture(e.Pointer); } catch { }
            }
        }

        private void TitleBar_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isWindowDragPending = false;
            if (sender is UIElement el)
            {
                try { el.ReleasePointerCapture(e.Pointer); } catch { }
            }
        }

        private void TitleBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isWindowDragPending = false;
        }

        private void TitleBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    if (presenter.State == OverlappedPresenterState.Maximized)
                    {
                        presenter.Restore();
                    }
                    else
                    {
                        presenter.Maximize();
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        private void SetupTitleBarTheme()
        {
            try
            {
                string theme = ConfigService.Current.Ui.Theme;
                bool isDark = theme switch
                {
                    "light" => false,
                    "dark" => true,
                    _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
                };

                if (AppWindow.TitleBar != null)
                {
                    AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

                    if (isDark)
                    {
                        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
                        AppWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(30, 255, 255, 255);
                        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(50, 255, 255, 255);
                        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 240, 240, 240);
                    }
                    else
                    {
                        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 30, 30, 30);
                        AppWindow.TitleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 0, 0, 0);
                        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(35, 0, 0, 0);
                        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 30, 30, 30);
                    }

                    // OS の自動ドラッグ領域を明示的にクリアして、単押し追従バグを防ぐ
                    AppWindow.TitleBar.SetDragRectangles([]);
                }

                // DWM / Win32 ノンクライアント領域の初期白線（フレーム境界）を即座に再計算・除去
                nint hWnd = WindowHandle;
                if (hWnd != nint.Zero)
                {
                    Win32Interop.ApplyImmersiveDarkMode(hWnd, isDark);
                    Win32Interop.SetWindowPos(
                        hWnd,
                        nint.Zero,
                        0, 0, 0, 0,
                        Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_FRAMECHANGED);
                }
            }
            catch
            {
                // ignored
            }
        }

        private void SetupWindowIcon()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string iconPath = System.IO.Path.Combine(baseDir, "icon.ico");
                if (!System.IO.File.Exists(iconPath))
                {
                    iconPath = System.IO.Path.GetFullPath("icon.ico");
                }

                if (System.IO.File.Exists(iconPath))
                {
                    AppWindow.SetIcon(iconPath);

                    nint hWnd = WindowHandle;
                    if (hWnd != nint.Zero)
                    {
                        nint hIconBig = Win32Interop.LoadImageW(nint.Zero, iconPath, Win32Interop.IMAGE_ICON, 32, 32, Win32Interop.LR_LOADFROMFILE);
                        nint hIconSmall = Win32Interop.LoadImageW(nint.Zero, iconPath, Win32Interop.IMAGE_ICON, 16, 16, Win32Interop.LR_LOADFROMFILE);

                        if (hIconBig != nint.Zero)
                        {
                            Win32Interop.SendMessage(hWnd, Win32Interop.WM_SETICON, (nuint)Win32Interop.ICON_BIG, hIconBig);
                        }
                        if (hIconSmall != nint.Zero)
                        {
                            Win32Interop.SendMessage(hWnd, Win32Interop.WM_SETICON, (nuint)Win32Interop.ICON_SMALL, hIconSmall);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowIcon] Error setting icon: {ex.Message}");
            }
        }

        private void RestoreWindowState()
        {
            try
            {
                var winState = ConfigService.Current.WindowState;
                if (winState.Width > 300 && winState.Height > 200)
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(winState.Width, winState.Height));
                }

                if (winState.X.HasValue && winState.Y.HasValue)
                {
                    AppWindow.Move(new Windows.Graphics.PointInt32(winState.X.Value, winState.Y.Value));
                }

                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    if (winState.IsMaximized)
                    {
                        presenter.Maximize();
                    }
                }

                AppWindow.Closing += (s, e) => SaveWindowState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowState] Error restoring: {ex.Message}");
            }
        }

        private void SaveWindowState()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    if (presenter.State == OverlappedPresenterState.Maximized)
                    {
                        ConfigService.Current.WindowState.IsMaximized = true;
                    }
                    else
                    {
                        ConfigService.Current.WindowState.IsMaximized = false;
                        var size = AppWindow.Size;
                        var pos = AppWindow.Position;
                        if (size.Width > 300 && size.Height > 200)
                        {
                            ConfigService.Current.WindowState.Width = size.Width;
                            ConfigService.Current.WindowState.Height = size.Height;
                            ConfigService.Current.WindowState.X = pos.X;
                            ConfigService.Current.WindowState.Y = pos.Y;
                        }
                    }
                    ConfigService.Save();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowState] Error saving: {ex.Message}");
            }
        }

        public async void ShowErrorDialog(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch
            {
                // ignored
            }
        }

        public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsTab();
        }

        private void SetupGlobalKeyboardAccelerators()
        {
            if (this.Content is UIElement root)
            {
                root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
            }
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CancelActiveRenaming();
            }

            if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox ||
                CurrentTab?.Items?.Any(x => x.IsRenaming) == true)
            {
                return;
            }

            // Enter キーは WinUI 3 の ListViewBase が内部で消費して選択トグルしてしまうため、
            // Handled 判定より前に捕捉して選択されている項目を開く
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (CurrentTab?.Items?.Any(x => x.IsRenaming) == true ||
                    Stopwatch.GetElapsedTime(_lastRenameCommittedTimestamp).TotalMilliseconds < 500)
                {
                    e.Handled = true;
                    return;
                }

                if (IsAltPressed())
                {
                    ContextMenuProperties_Click(this, e);
                }
                else
                {
                    OpenSelectedItems();
                }
                e.Handled = true;
                return;
            }

            if (e.Handled)
            {
                return;
            }

            FileListView_KeyDown(sender, e);
        }

        private Win32Interop.SUBCLASSPROC? _mainWindowSubclassProc;

        private void SetupMainWindowSubclass()
        {
            if (WindowHandle == 0 || _mainWindowSubclassProc != null) return;
            _mainWindowSubclassProc = MainWindowWndProc;
            Win32Interop.SetWindowSubclass(WindowHandle, _mainWindowSubclassProc, 100, 0);

            SystemIntegrationService.WinEHotKeyPressed -= OnWinEHotKeyPressed;
            SystemIntegrationService.WinEHotKeyPressed += OnWinEHotKeyPressed;
        }

        private void OnWinEHotKeyPressed()
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                this.AppWindow.Show();
                if (this.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                    {
                        presenter.Restore();
                    }
                }
                this.Activate();
                Win32Interop.ForceForegroundWindow(WindowHandle);

                if (TabCount == 0)
                {
                    CreateNewTab(ConfigService.Current.Startup.DefaultPath);
                }
            });
        }

        private nint MainWindowWndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
        {
            if (uMsg == Win32Interop.WM_XBUTTONUP)
            {
                int button = (int)((wParam >> 16) & 0xFFFF);
                if (button == Win32Interop.XBUTTON1) // 戻る (Back)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        CurrentTab?.GoBack();
                        UpdateToolbarState();
                    });
                    return (nint)1;
                }
                else if (button == Win32Interop.XBUTTON2) // 進む (Forward)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        CurrentTab?.GoForward();
                        UpdateToolbarState();
                    });
                    return (nint)1;
                }
            }
            else if (uMsg == Win32Interop.WM_APPCOMMAND)
            {
                short cmd = (short)(((nint)lParam >> 16) & 0xFFF);
                if (cmd == Win32Interop.APPCOMMAND_BROWSER_BACKWARD)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        CurrentTab?.GoBack();
                        UpdateToolbarState();
                    });
                    return (nint)1;
                }
                else if (cmd == Win32Interop.APPCOMMAND_BROWSER_FORWARD)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        CurrentTab?.GoForward();
                        UpdateToolbarState();
                    });
                    return (nint)1;
                }
            }
            else if (uMsg == Win32Interop.WM_HOTKEY)
            {
                int hotkeyId = (int)wParam;
                if (hotkeyId == SystemIntegrationService.WIN_E_HOTKEY_ID)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        Win32Interop.SetForegroundWindow(WindowHandle);
                        this.Activate();
                        CreateNewTab(ConfigService.Current.Startup.DefaultPath);
                    });
                    return (nint)1;
                }
            }

            return Win32Interop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        #endregion

        #region Wallpaper & Background

        private string? _currentLoadedWallpaperPath;

        public void ApplyWallpaper()
        {
            try
            {
                var ui = ConfigService.Current.Ui;
                string path = ui.BackgroundImagePath;

                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    BackgroundHostGrid.Visibility = Visibility.Collapsed;
                    BackgroundImageHost.Source = null;
                    _currentLoadedWallpaperPath = null;
                    return;
                }

                // 画像の読み込み (パスが変わった場合のみ再ロード)
                if (_currentLoadedWallpaperPath != path || BackgroundImageHost.Source == null)
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                    BackgroundImageHost.Source = bitmap;
                    _currentLoadedWallpaperPath = path;
                }

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

                BackgroundHostGrid.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Wallpaper] Error applying wallpaper: {ex.Message}");
                BackgroundHostGrid.Visibility = Visibility.Collapsed;
            }
        }

        #endregion
    }
}
