using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FastExplorer.Views.Properties
{
    public sealed partial class PropertiesWindow : Window
    {
        private FilePropertiesInfo? _model;
        private bool _isDirty;
        private bool _isUpdatingUI;
        private const int LogicalWindowWidth = 530;
        private const int LogicalWindowHeight = 730;

        public FilePropertiesInfo? Model => _model;

        public PropertiesWindow()
        {
            // タイトルバーをコンテンツ内に拡張
            this.ExtendsContentIntoTitleBar = true;

            this.InitializeComponent();

            // 自作タイトルバーを設定
            SetTitleBar(AppTitleBar);

            // ダイアログ専用プレゼンターの設定 (最小化・最大化無効化、閉じるボタンのみ)
            try
            {
                nint hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

                if (AppWindow != null)
                {
                    var presenter = OverlappedPresenter.CreateForDialog();
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsMaximizable = false;
                    AppWindow.SetPresenter(presenter);

                    var (pWidth, pHeight) = GetPhysicalWindowSize(hWnd);
                    AppWindow.Resize(new SizeInt32(pWidth, pHeight));

                    if (AppWindow.TitleBar != null)
                    {
                        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                    }

                    string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
                    if (System.IO.File.Exists(iconPath))
                    {
                        AppWindow.SetIcon(iconPath);
                    }
                }

                // Win32 レベルでも確実に最小化/最大化ボタンスタイルを除去
                if (hWnd != nint.Zero)
                {
                    int style = Win32Interop.GetWindowLongW(hWnd, Win32Interop.GWL_STYLE);
                    style &= ~(int)(Win32Interop.WS_MINIMIZEBOX | Win32Interop.WS_MAXIMIZEBOX);
                    Win32Interop.SetWindowLongW(hWnd, Win32Interop.GWL_STYLE, style);
                }
            }
            catch { }

            this.Closed += PropertiesWindow_Closed;
        }

        private static (int PhysicalWidth, int PhysicalHeight) GetPhysicalWindowSize(nint hWnd)
        {
            uint dpi = Win32Interop.GetDpiForWindow(hWnd);
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;

            int pWidth = (int)Math.Round(LogicalWindowWidth * scale);
            int pHeight = (int)Math.Round(LogicalWindowHeight * scale);
            return (pWidth, pHeight);
        }

        public static async void Show(IReadOnlyList<string> paths, Windows.Foundation.Point? screenPos = null, ElementTheme theme = ElementTheme.Default)
        {
            if (paths == null || paths.Count == 0) return;

            try
            {
                var window = new PropertiesWindow();

                if (window.Content is FrameworkElement root)
                {
                    root.RequestedTheme = theme;
                }

                window.PositionWindow(screenPos);
                window.Activate();

                var model = await FilePropertiesInfo.CreateAsync(paths, window.DispatcherQueue);
                window.InitializeWithModel(model);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PropertiesWindow.Show] Error: {ex.Message}");
            }
        }

        private void PositionWindow(Windows.Foundation.Point? screenPos)
        {
            try
            {
                nint hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var (pWidth, pHeight) = GetPhysicalWindowSize(hWnd);

                int posX, posY;
                if (screenPos.HasValue)
                {
                    posX = (int)screenPos.Value.X;
                    posY = (int)screenPos.Value.Y;
                }
                else
                {
                    Win32Interop.GetCursorPos(out var pt);
                    posX = pt.X;
                    posY = pt.Y;
                }

                var point = new PointInt32(posX, posY);
                var displayArea = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);

                if (displayArea != null)
                {
                    var workArea = displayArea.WorkArea;
                    int maxX = workArea.X + workArea.Width - pWidth - 10;
                    int maxY = workArea.Y + workArea.Height - pHeight - 10;

                    int targetX = Math.Min(posX, maxX);
                    int targetY = Math.Min(posY, maxY);
                    targetX = Math.Max(targetX, workArea.X + 10);
                    targetY = Math.Max(targetY, workArea.Y + 10);

                    AppWindow.Move(new PointInt32(targetX, targetY));
                }
            }
            catch { }
        }

        public void InitializeWithModel(FilePropertiesInfo model)
        {
            _model = model;
            _model.PropertyChanged += Model_PropertyChanged;

            this.Title = $"{_model.Name} のプロパティ";

            UpdateUIFromModel();

            // デジタル署名
            if (_model.HasDigitalSignatures)
            {
                SignaturesListView.ItemsSource = _model.DigitalSignatures;
                TabBtnSignatures.Visibility = Visibility.Visible;
            }
            else
            {
                TabBtnSignatures.Visibility = Visibility.Collapsed;
            }

            // セキュリティ
            if (_model.SecurityPrincipals.Count > 0)
            {
                SecurityPrincipalsListView.ItemsSource = _model.SecurityPrincipals;
                SecurityPrincipalsListView.SelectedIndex = 0;
            }

            // 詳細
            DetailsListView.ItemsSource = _model.DetailsList;

            if (_model.TargetType == PropertyTargetType.SingleFile)
            {
                _ = _model.CalculateHashesAsync();
            }
        }

        private void UpdateUIFromModel()
        {
            if (_model == null) return;

            _isUpdatingUI = true;
            try
            {
                // 1. ヘッダーアイコン
                HeaderFontIcon.Visibility = Visibility.Collapsed;
                if (_model.Icon != null)
                {
                    HeaderImageIcon.Source = _model.Icon;
                    HeaderImageIcon.Visibility = Visibility.Visible;
                }
                else
                {
                    HeaderImageIcon.Visibility = Visibility.Visible;
                    Task.Run(async () =>
                    {
                        bool isDir = _model.TargetType == PropertyTargetType.SingleFolder;
                        var bmp = IconThumbnailService.GetSoftwareBitmapForPath(_model.FullPath, isDir, true);
                        if (bmp != null)
                        {
                            try
                            {
                                var copy = SoftwareBitmap.Copy(bmp);
                                this.DispatcherQueue.TryEnqueue(async () =>
                                {
                                    var src = new SoftwareBitmapSource();
                                    await src.SetBitmapAsync(copy);
                                    HeaderImageIcon.Source = src;
                                });
                            }
                            catch { }
                        }
                    });
                }

                if (_model.ShowNameTextBox)
                {
                    NameTextBox.Text = _model.Name;
                    NameTextBox.Visibility = Visibility.Visible;
                    NameTextBlock.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NameTextBlock.Text = _model.Name;
                    NameTextBlock.Visibility = Visibility.Visible;
                    NameTextBox.Visibility = Visibility.Collapsed;
                }

                ItemTypeTextBlock.Text = _model.ItemType;

                // 2. モード別の表示切り替え
                if (_model.IsDrive)
                {
                    DrivePanel.Visibility = Visibility.Visible;
                    FileFolderPanel.Visibility = Visibility.Collapsed;
                    TabBtnSignatures.Visibility = Visibility.Collapsed;
                    TabBtnSecurity.Visibility = Visibility.Visible;
                    TabBtnDetails.Visibility = Visibility.Collapsed;

                    DriveTypeText.Text = _model.ItemType;
                    DriveFileSystemText.Text = _model.FileSystem;
                    DriveUsedSpaceText.Text = _model.FormattedUsedSpace;
                    DriveFreeSpaceText.Text = _model.FormattedFreeSpace;
                    DriveTotalSpaceText.Text = _model.FormattedTotalSpace;
                    DriveProgressBar.Value = _model.UsedPercentage;
                }
                else
                {
                    DrivePanel.Visibility = Visibility.Collapsed;
                    FileFolderPanel.Visibility = Visibility.Visible;

                    // プログラム
                    if (_model.ShowOpensWith)
                    {
                        OpensWithText.Text = _model.OpensWith;
                        OpensWithRow.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        OpensWithRow.Visibility = Visibility.Collapsed;
                    }

                    // 場所
                    LocationText.Text = _model.Location;

                    // サイズ
                    SizeText.Text = _model.FormattedSize;
                    SizeProgressRing.IsActive = _model.IsCalculatingSize;
                    SizeProgressRing.Visibility = _model.IsCalculatingSize ? Visibility.Visible : Visibility.Collapsed;

                    // ディスク上のサイズ
                    if (_model.ShowSizeOnDisk)
                    {
                        SizeOnDiskText.Text = _model.FormattedSizeOnDisk;
                        SizeOnDiskRow.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        SizeOnDiskRow.Visibility = Visibility.Collapsed;
                    }

                    // 内容
                    if (_model.ShowContains)
                    {
                        ContainsText.Text = _model.FormattedContains;
                        ContainsRow.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ContainsRow.Visibility = Visibility.Collapsed;
                    }

                    // 日時
                    if (_model.ShowDates)
                    {
                        DateCreatedText.Text = _model.FormattedDateCreated;
                        DateModifiedText.Text = _model.FormattedDateModified;
                        DateAccessedText.Text = _model.FormattedDateAccessed;
                        DatesPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        DatesPanel.Visibility = Visibility.Collapsed;
                    }

                    // 属性 (2値で確実にON/OFF)
                    if (_model.ShowAttributes)
                    {
                        ReadOnlyCheckBox.IsChecked = _model.IsReadOnly == true;
                        HiddenCheckBox.IsChecked = _model.IsHidden == true;
                        AttributesRow.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        AttributesRow.Visibility = Visibility.Collapsed;
                    }

                    // デジタル署名タブ (署名がある場合のみ表示)
                    TabBtnSignatures.Visibility = _model.HasDigitalSignatures ? Visibility.Visible : Visibility.Collapsed;

                    // セキュリティタブ
                    SecurityObjectPathText.Text = _model.FullPath;
                    TabBtnSecurity.Visibility = _model.ShowSecurityTab ? Visibility.Visible : Visibility.Collapsed;

                    // 詳細タブ
                    TabBtnDetails.Visibility = _model.ShowDetailsTab ? Visibility.Visible : Visibility.Collapsed;
                    Sha256TextBox.Text = _model.Sha256Hash;
                    Md5TextBox.Text = _model.Md5Hash;
                }
            }
            finally
            {
                _isUpdatingUI = false;
            }
        }

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton clickedBtn && clickedBtn.Tag is string tag)
            {
                TabBtnGeneral.IsChecked = tag == "General";
                TabBtnSignatures.IsChecked = tag == "Signatures";
                TabBtnSecurity.IsChecked = tag == "Security";
                TabBtnDetails.IsChecked = tag == "Details";

                GeneralTabContent.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
                SignaturesTabContent.Visibility = tag == "Signatures" ? Visibility.Visible : Visibility.Collapsed;
                SecurityTabContent.Visibility = tag == "Security" ? Visibility.Visible : Visibility.Collapsed;
                DetailsTabContent.Visibility = tag == "Details" ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_model == null) return;

            this.DispatcherQueue.TryEnqueue(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(FilePropertiesInfo.Icon):
                        if (_model.Icon != null)
                        {
                            HeaderImageIcon.Source = _model.Icon;
                            HeaderImageIcon.Visibility = Visibility.Visible;
                            HeaderFontIcon.Visibility = Visibility.Collapsed;
                        }
                        break;

                    case nameof(FilePropertiesInfo.FormattedSize):
                    case nameof(FilePropertiesInfo.Size):
                        SizeText.Text = _model.FormattedSize;
                        break;

                    case nameof(FilePropertiesInfo.FormattedSizeOnDisk):
                    case nameof(FilePropertiesInfo.SizeOnDisk):
                        SizeOnDiskText.Text = _model.FormattedSizeOnDisk;
                        break;

                    case nameof(FilePropertiesInfo.FormattedContains):
                        ContainsText.Text = _model.FormattedContains;
                        break;

                    case nameof(FilePropertiesInfo.IsCalculatingSize):
                        SizeProgressRing.IsActive = _model.IsCalculatingSize;
                        SizeProgressRing.Visibility = _model.IsCalculatingSize ? Visibility.Visible : Visibility.Collapsed;
                        break;

                    case nameof(FilePropertiesInfo.Sha256Hash):
                        Sha256TextBox.Text = _model.Sha256Hash;
                        break;

                    case nameof(FilePropertiesInfo.Md5Hash):
                        Md5TextBox.Text = _model.Md5Hash;
                        break;
                }
            });
        }

        private void SecurityPrincipalsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SecurityPrincipalsListView.SelectedItem is SecurityPrincipalPermission perm)
            {
                PermissionsForUserText.Text = $"アクセス許可 ({perm.DisplayName}):";

                PermFullControl.Glyph = perm.FullControl ? "\uE73E" : string.Empty;
                PermModify.Glyph = perm.Modify ? "\uE73E" : string.Empty;
                PermReadAndExecute.Glyph = perm.ReadAndExecute ? "\uE73E" : string.Empty;
                PermRead.Glyph = perm.Read ? "\uE73E" : string.Empty;
                PermWrite.Glyph = perm.Write ? "\uE73E" : string.Empty;
            }
        }

        private void ChangeProgramButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model != null && !string.IsNullOrEmpty(_model.FullPath))
            {
                FileOperationService.OpenWithDialog(_model.FullPath);
            }
        }

        private void NameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_model != null && NameTextBox.Text.Trim() != _model.OriginalName)
            {
                _isDirty = true;
                ApplyButton.IsEnabled = true;
            }
        }

        private void NameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_model != null && NameTextBox.Text.Trim() != _model.OriginalName)
                {
                    _isDirty = true;
                    ApplyButton.IsEnabled = true;
                }
                FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
                e.Handled = true;
            }
        }

        private void Attribute_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _isDirty = true;
            ApplyButton.IsEnabled = true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDirty && _model != null)
            {
                ApplyCurrentChanges();
            }
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model != null)
            {
                ApplyCurrentChanges();
                ApplyButton.IsEnabled = false;
                _isDirty = false;
            }
        }

        private void ApplyCurrentChanges()
        {
            if (_model == null) return;

            _model.Name = NameTextBox.Text.Trim();
            _model.IsReadOnly = ReadOnlyCheckBox.IsChecked;
            _model.IsHidden = HiddenCheckBox.IsChecked;

            if (!_model.ApplyChanges(out string? error))
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyCurrentChanges] Error: {error}");
            }
        }

        private void CopySha256Button_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(Sha256TextBox.Text) && !Sha256TextBox.Text.Contains("計算中") && !Sha256TextBox.Text.Contains("エラー"))
            {
                SetClipboardText(Sha256TextBox.Text);
            }
        }

        private void CopyMd5Button_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(Md5TextBox.Text) && !Md5TextBox.Text.Contains("計算中") && !Md5TextBox.Text.Contains("エラー"))
            {
                SetClipboardText(Md5TextBox.Text);
            }
        }

        private static void SetClipboardText(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch { }
        }

        private void PropertiesWindow_Closed(object sender, WindowEventArgs args)
        {
            _model?.CancelOperations();
        }
    }
}
