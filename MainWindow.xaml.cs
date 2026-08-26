using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer
{
    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollection<NavigationTabItem> _tabs = [];
        public ObservableCollection<FileItem> SidebarItems { get; } = [];
        public int TabCount => MainTabView?.TabItems?.Count ?? _tabs.Count;
        private bool _isInitialized;

        public MainWindow(bool createInitialTab = true, string? initialPath = null, string? selectItemName = null)
        {
            // タイトルバーをコンテンツ内に拡張し、カスタムドラッグ領域を登録
            this.ExtendsContentIntoTitleBar = true;

            InitializeComponent();
            InitializeComponentEvents();

            this.SetTitleBar(CustomDragRegion);

            // アイコンサービス初期化 (Sidebar構築前にDispatcherQueueを確実に登録)
            IconThumbnailService.Instance.Initialize(this.DispatcherQueue);
            IconThumbnailService.Instance.DefaultIconsInitialized += () =>
            {
                if (MainTabView?.TabItems != null)
                {
                    foreach (var tabItem in MainTabView.TabItems.OfType<TabViewItem>())
                    {
                        if (tabItem.DataContext is NavigationTabItem navTab)
                        {
                            tabItem.IconSource = IconThumbnailService.GetIconSourceForNavigationPath(navTab.CurrentPath);
                        }
                    }
                }
                foreach (var sItem in SidebarItems)
                {
                    IconThumbnailService.Instance.ApplyImmediateDefaultIcon(sItem);
                }
            };

            // タイトルバーテーマ設定
            SetupTitleBarTheme();

            // ウィンドウアイコン設定 (タスクバープレビュー、Alt+Tab等)
            SetupWindowIcon();

            // 前回のウィンドウ状態（最大化・サイズ・位置）を復元
            RestoreWindowState();
            this.Closed += (s, e) => SaveWindowState();

            // サイドバー構築
            InitializeSidebar();

            // グローバルショートカットキー設定
            SetupGlobalKeyboardAccelerators();

            // 初期タブ作成
            if (createInitialTab)
            {
                string target = string.IsNullOrEmpty(initialPath) ? ConfigService.Current.Startup.DefaultPath : initialPath;
                CreateNewTab(target, selectItemName);
            }

            // プレビューペイン初期化
            UpdatePreviewPane();

            // プロパティ変更（隠し属性など）のリアルタイム反映
            FilePropertiesInfo.FilePropertiesChanged += OnFilePropertiesChanged;

            // ウィンドウフォーカス復帰時の同期
            this.Activated += MainWindow_Activated;

            // Win + E ホットキー登録
            if (ConfigService.Current.SystemIntegration.InterceptWinE)
            {
                SystemIntegrationService.RegisterWinEHotKey(WindowHandle);
            }

            // 同名ファイル衝突回避ダイアログの登録
            FileOperationService.ConflictResolver = ShowFileConflictDialogAsync;

            // 項目チェックボックス表示の初期化
            ApplyItemCheckBoxesState();
            ApplyWallpaper();
            InitializeComponentEvents();
            InitializeFileListEvents();
            InitializeColumnResize();

            // ウィンドウレベルのメッセージ処理 (マウスサイドキー対応)
            SetupMainWindowSubclass();

            // 名前欄以外をタップ・クリックしたときに名前変更を確実にキャンセルするグローバル監視
            RootGrid.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnGlobalPointerPressed), true);
            RootGrid.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnGlobalPointerReleased), true);

            _isInitialized = true;

            // 起動時に最前面・最上面化を強制実行
            Win32Interop.ForceForegroundWindow(this.WindowHandle);

            // 初回描画完了後のアイドル時間にバックグラウンドで事前ロード（ウォームアップ）を実行
            TriggerBackgroundWarmup();
        }

        private void TriggerBackgroundWarmup()
        {
            Task.Run(async () =>
            {
                try
                {
                    // UIと初期タブの初回レンダリングを最優先するため少し待機
                    await Task.Delay(300);

                    // 1. 新規作成テンプレートの一覧走査とアイコン事前キャッシュ
                    var templates = ShellNewService.GetShellNewTemplates();
                    foreach (var t in templates)
                    {
                        if (!string.IsNullOrEmpty(t.Extension))
                        {
                            IconThumbnailService.GetSoftwareBitmapForExtension(t.Extension);
                        }
                    }
                    IconThumbnailService.GetSoftwareBitmapForExtension(".txt");

                    // 2. 主要な拡張子アイコンの事前キャッシュ
                    string[] commonExts = [".docx", ".xlsx", ".pptx", ".pdf", ".zip", ".jpg", ".png", ".mp4", ".exe"];
                    foreach (var ext in commonExts)
                    {
                        IconThumbnailService.GetSoftwareBitmapForExtension(ext);
                    }

                    // 3. Shell コンテキストメニューの COM 基盤の事前初期化
                    string tempPath = Path.GetTempPath();
                    if (Directory.Exists(tempPath))
                    {
                        try
                        {
                            ShellContextMenuService.ExtractMatchingShellItems(WindowHandle, new[] { tempPath });
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WarmUp] Background warmup error: {ex.Message}");
                }
            });
        }

        private async Task<(Views.Dialogs.ConflictResolution Resolution, bool ApplyToAll)> ShowFileConflictDialogAsync(string sourcePath, string destPath)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<(Views.Dialogs.ConflictResolution, bool)>();

            this.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (this.Content?.XamlRoot == null)
                    {
                        tcs.SetResult((Views.Dialogs.ConflictResolution.KeepBoth, false));
                        return;
                    }

                    var dialog = new Views.Dialogs.FileConflictDialog(sourcePath, destPath)
                    {
                        XamlRoot = this.Content.XamlRoot
                    };

                    await dialog.ShowAsync();
                    tcs.SetResult((dialog.Result, dialog.ApplyToAll));
                }
                catch
                {
                    tcs.SetResult((Views.Dialogs.ConflictResolution.KeepBoth, false));
                }
            });

            return await tcs.Task;
        }

        private void OnGlobalPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(this.Content).Properties;
            if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed ||
                props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed)
            {
                return;
            }

            if (e.OriginalSource is DependencyObject d)
            {
                // クリックされた要素が TextBox（RenameBox）またはその内部の場合は名前変更を継続
                if (FindVisualParent<TextBox>(d) != null)
                {
                    return;
                }
            }

            // 名前欄以外がタップされた場合はアクティブな名前変更をすべてキャンセル
            CancelActiveRenaming();
        }

        private void OnGlobalPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(this.Content).Properties;
            if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Released)
            {
                CurrentTab?.GoBack();
                UpdateToolbarState();
                e.Handled = true;
            }
            else if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton2Released)
            {
                CurrentTab?.GoForward();
                UpdateToolbarState();
                e.Handled = true;
            }
        }

        public void CancelActiveRenaming()
        {
            if (CurrentTab?.Items != null)
            {
                foreach (var item in CurrentTab.Items)
                {
                    if (item.IsRenaming)
                    {
                        item.RenameText = item.Name;
                        item.IsRenaming = false;
                    }
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                if (_isInitialized)
                {
                    if (CurrentTab?.CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        RefreshHomeView();
                    }
                    else if (CurrentTab != null)
                    {
                        CurrentTab.Refresh();
                    }
                }
            }
        }

        private void OnFilePropertiesChanged(System.Collections.Generic.IReadOnlyList<string> paths)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                CurrentTab?.Refresh();
            });
        }

        public void ToggleShowHiddenFiles()
        {
            ConfigService.Current.Ui.ShowHiddenFiles = !ConfigService.Current.Ui.ShowHiddenFiles;
            ConfigService.Save();
            CurrentTab?.Refresh();
        }

        public void ToggleShowItemCheckBoxes()
        {
            ConfigService.Current.Ui.ShowItemCheckBoxes = !ConfigService.Current.Ui.ShowItemCheckBoxes;
            ConfigService.Save();
            ApplyItemCheckBoxesState();
            UpdateViewMenuCheckStates();
        }

        public void ApplyItemCheckBoxesState()
        {
            if (FileListView != null) FileListView.SelectionMode = ListViewSelectionMode.Extended;
            if (FileGridView != null) FileGridView.SelectionMode = ListViewSelectionMode.Extended;

            FileItem.GlobalShowCheckBoxes = ConfigService.Current.Ui.ShowItemCheckBoxes;
            if (_tabs != null)
            {
                foreach (var tab in _tabs)
                {
                    if (tab.Items != null)
                    {
                        foreach (var item in tab.Items)
                        {
                            item.RefreshCheckBoxVisibility();
                        }
                    }
                }
            }
        }

        private NavigationTabItem? CurrentTab => (MainTabView.SelectedItem as TabViewItem)?.DataContext as NavigationTabItem;

        public void ApplyTheme(string themeTag)
        {
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = themeTag switch
                {
                    "dark" => ElementTheme.Dark,
                    "light" => ElementTheme.Light,
                    _ => ElementTheme.Default
                };
            }
            ApplyWallpaper();
        }

        private void InitializeComponentEvents()
        {
            InitializeAddressBarEvents();
            InitializeToolbarEvents();
            InitializeStatusBarEvents();
            InitializePreviewPaneEvents();
        }

        private void InitializeToolbarEvents()
        {
            if (ActionToolbar == null) return;
            ActionToolbar.NewMenuOpening += items => PopulateNewMenu(items, isToolbarFlyout: true);
            ActionToolbar.NewFolderRequested += (s, e) => ContextMenuNewFolder_Click(s, e);
            ActionToolbar.NewTextFileRequested += (s, e) => ContextMenuNewTextFile_Click(s, e);
            ActionToolbar.CutRequested += (s, e) => ContextMenuCut_Click(s, e);
            ActionToolbar.CopyRequested += (s, e) => ContextMenuCopy_Click(s, e);
            ActionToolbar.PasteRequested += (s, e) => ContextMenuPaste_Click(s, e);
            ActionToolbar.RenameRequested += (s, e) => ContextMenuRename_Click(s, e);
            ActionToolbar.DeleteRequested += (s, e) => ContextMenuDelete_Click(s, e);
            ActionToolbar.PropertiesRequested += (s, e) => ContextMenuProperties_Click(s, e);
            ActionToolbar.RestoreRequested += (s, e) => ContextMenuRestore_Click(s, e);
            ActionToolbar.EmptyRecycleBinRequested += (s, e) => ContextMenuEmptyRecycleBin_Click(s, e);
            ActionToolbar.TogglePreviewRequested += (s, e) => TogglePreviewPane_Click(s, e);

            ActionToolbar.ViewModeDetailsRequested += (s, e) => ViewModeDetails_Click(s, e);
            ActionToolbar.ViewModeListRequested += (s, e) => ViewModeList_Click(s, e);
            ActionToolbar.ViewModeContentRequested += (s, e) => ViewModeContent_Click(s, e);
            ActionToolbar.ViewModeGridRequested += (s, e) => ViewModeGrid_Click(s, e);
            ActionToolbar.ViewModeTilesRequested += (s, e) => ViewModeTiles_Click(s, e);

            ActionToolbar.ViewSizeSmallRequested += (s, e) => ViewSizeSmall_Click(s, e);
            ActionToolbar.ViewSizeMediumRequested += (s, e) => ViewSizeMedium_Click(s, e);
            ActionToolbar.ViewSizeLargeRequested += (s, e) => ViewSizeLarge_Click(s, e);
            ActionToolbar.ViewSizeExtraLargeRequested += (s, e) => ViewSizeExtraLarge_Click(s, e);

            ActionToolbar.ShowCheckBoxesChanged += isOn =>
            {
                ConfigService.Current.Ui.ShowItemCheckBoxes = isOn;
                ConfigService.Save();
                ApplyItemCheckBoxesState();
            };

            ActionToolbar.ShowHiddenFilesChanged += isOn =>
            {
                ConfigService.Current.Ui.ShowHiddenFiles = isOn;
                ConfigService.Save();
                CurrentTab?.Refresh();
            };
        }

        private void InitializeStatusBarEvents()
        {
            if (StatusBar == null) return;
            StatusBar.DetailsViewRequested += (s, e) => ViewModeDetails_Click(s, e);
            StatusBar.IconsViewRequested += (s, e) => QuickToggleIconsView_Click(s, e);
            StatusBar.ZoomOutRequested += (s, e) => ViewZoomOut_Click(s, e);
            StatusBar.ZoomInRequested += (s, e) => ViewZoomIn_Click(s, e);
        }

        private void InitializePreviewPaneEvents()
        {
            if (PreviewPane == null) return;
            PreviewPane.CloseRequested += (s, e) => ClosePreviewPane_Click(s, e);
        }
    }
}
