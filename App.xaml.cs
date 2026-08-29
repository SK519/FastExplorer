using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Services;
using Microsoft.UI.Xaml;

namespace FastExplorer
{
    public partial class App : Application
    {
        public static List<MainWindow> OpenWindows { get; } = [];
        public static Window? CurrentWindow => OpenWindows.LastOrDefault();

        public App()
        {
            try
            {
                Core.Win32Interop.OleInitialize(nint.Zero);
                Core.Win32Interop.SetPreferredAppMode(Core.Win32Interop.PreferredAppMode.AllowDark);
                Core.Win32Interop.FlushMenuThemes();
            }
            catch
            {
                // ignored
            }

            UnhandledException += (s, e) =>
            {
                try
                {
                    string localFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                    Directory.CreateDirectory(localFolder);
                    string crashLog = Path.Combine(localFolder, "crash.log");
                    File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UnhandledException: {e.Message}\r\n{e.Exception}\r\n\r\n");
                }
                catch { }
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    string localFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastExplorer");
                    Directory.CreateDirectory(localFolder);
                    string crashLog = Path.Combine(localFolder, "crash.log");
                    File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AppDomain Exception: {e.ExceptionObject}\r\n\r\n");
                }
                catch { }
            };

            InitializeComponent();
        }

        private static Microsoft.UI.Dispatching.DispatcherQueue? _appDispatcherQueue;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _appDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // Single Instance: 外部プロセスからの起動通知（フォルダーダブルクリック、ショートカット、/select 等）を処理
            try
            {
                var currentInstance = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
                currentInstance.Activated += (sender, activatedArgs) =>
                {
                    _appDispatcherQueue?.TryEnqueue(() =>
                    {
                        ParseActivatedEventArgs(activatedArgs, out string? targetPath, out string? selectItem);
                        OpenOrCreateWindow(targetPath, selectItem);
                    });
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] AppInstance.Activated setup error: {ex.Message}");
            }

            string[] cmdArgs = Environment.GetCommandLineArgs();
            ParseArguments(cmdArgs, out string? initialPath, out string? selectItemName);
            OpenOrCreateWindow(initialPath, selectItemName);
        }

        public static bool HasBackgroundFlag(string[]? args)
        {
            if (args == null) return false;
            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                string clean = arg.Trim('"', '\'', ' ');
                if (clean.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("-b", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ParseActivatedEventArgs(Microsoft.Windows.AppLifecycle.AppActivationArguments args, out string? targetPath, out string? selectItem)
        {
            targetPath = null;
            selectItem = null;

            try
            {
                if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch)
                {
                    if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
                    {
                        string arguments = launchArgs.Arguments ?? string.Empty;
                        ParseArguments(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), out targetPath, out selectItem);
                    }
                }
                else if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.CommandLineLaunch)
                {
                    if (args.Data is Windows.ApplicationModel.Activation.ICommandLineActivatedEventArgs cmdArgs)
                    {
                        string arguments = cmdArgs.Operation?.Arguments ?? string.Empty;
                        ParseArguments(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), out targetPath, out selectItem);
                    }
                }
                else if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
                {
                    if (args.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs && fileArgs.Files.Count > 0)
                    {
                        string filePath = fileArgs.Files[0].Path;
                        if (Directory.Exists(filePath))
                        {
                            targetPath = filePath;
                        }
                        else if (File.Exists(filePath))
                        {
                            targetPath = Path.GetDirectoryName(filePath);
                            selectItem = Path.GetFileName(filePath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] ParseActivatedEventArgs error: {ex.Message}");
            }
        }

        private static bool IsOwnBinaryOrFlag(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return true;
            string clean = arg.Trim('"', '\'', ' ');

            // 1. 内部オプションフラグ
            if (clean.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("-b", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. 引数が単なる実行可能ファイル名単体のみの場合（パス区切りを含まない引数トークン）
            if (!clean.Contains('\\') && !clean.Contains('/'))
            {
                if (clean.Equals("FastExplorer", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("FastExplorer.exe", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("FastExplorer.dll", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("FastExplorerWatcher.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            try
            {
                // 3. 実行中のプロセスの実行バイナリパスと完全一致する場合
                string? procPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(procPath) && clean.Equals(procPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 4. 自身のインストールディレクトリ直下の FastExplorer.exe / FastExplorer.dll と完全一致する場合
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string ownExe = Path.Combine(baseDir, "FastExplorer.exe");
                string ownDll = Path.Combine(baseDir, "FastExplorer.dll");
                if (clean.Equals(ownExe, StringComparison.OrdinalIgnoreCase) || clean.Equals(ownDll, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static string CleanArgumentPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string clean = raw.Trim('"', '\'', ' ', '\t', '\r', '\n');

            // 末尾や先頭に残った引用符やバックスラッシュの誤エスケープ（例: G:" や "G:\ など）を除去
            clean = clean.Trim('"', '\'');

            // file:/// 形式のデコード
            if (clean.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Uri.TryCreate(clean, UriKind.Absolute, out var uri))
                    {
                        clean = uri.LocalPath;
                    }
                }
                catch { }
            }

            // ドライブレター単体（例: "C:" または "G:"）の場合は "C:\" に正規化
            if (clean.Length == 2 && char.IsLetter(clean[0]) && clean[1] == ':')
            {
                clean += "\\";
            }

            return clean;
        }

        private static void ParseArguments(string[] cmdArgs, out string? targetPath, out string? selectItem)
        {
            targetPath = null;
            selectItem = null;

            if (cmdArgs == null || cmdArgs.Length == 0) return;

            // 1. 全引数を結合して一括解析 (スペースや引用符が分割されたケースを修復)
            string fullRaw = string.Join(" ", cmdArgs).Trim();

            // /select, "<path>" または /select, <path> または /select <path> の検出
            int selectIdx = fullRaw.IndexOf("/select", StringComparison.OrdinalIgnoreCase);
            if (selectIdx >= 0)
            {
                string afterSelect = fullRaw.Substring(selectIdx + 7).Trim();
                if (afterSelect.StartsWith(","))
                {
                    afterSelect = afterSelect.Substring(1).Trim();
                }
                string cleanPath = CleanArgumentPath(afterSelect);

                if (!string.IsNullOrEmpty(cleanPath))
                {
                    try
                    {
                        if (File.Exists(cleanPath))
                        {
                            string? parent = Path.GetDirectoryName(cleanPath);
                            targetPath = string.IsNullOrEmpty(parent) ? cleanPath : parent;
                            selectItem = Path.GetFileName(cleanPath);
                            return;
                        }
                        if (Directory.Exists(cleanPath))
                        {
                            targetPath = cleanPath;
                            return;
                        }
                        
                        targetPath = cleanPath;
                        return;
                    }
                    catch { }
                }
            }

            // 2. 実行バイナリ名を除去した有効な引数リストを作成
            var validArgs = new List<string>();
            int startIndex = 0;
            if (cmdArgs.Length > 0 && IsOwnBinaryOrFlag(cmdArgs[0]))
            {
                startIndex = 1;
            }

            for (int i = startIndex; i < cmdArgs.Length; i++)
            {
                string arg = CleanArgumentPath(cmdArgs[i]);
                if (!string.IsNullOrEmpty(arg) && !IsOwnBinaryOrFlag(arg))
                {
                    validArgs.Add(arg);
                }
            }

            if (validArgs.Count == 0)
            {
                // 有効なパス指定引数がない場合は null を返す（デフォルト起動）
                return;
            }

            // 3. 単体引数の判定
            foreach (var raw in validArgs)
            {
                string clean = CleanArgumentPath(raw);
                if (string.IsNullOrEmpty(clean)) continue;

                // ごみ箱
                if (RecycleBinService.IsRecycleBinPath(clean))
                {
                    targetPath = RecycleBinService.RecycleBinUri;
                    return;
                }

                // 特殊シェルURI
                if (clean.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                    clean.StartsWith("::", StringComparison.OrdinalIgnoreCase) ||
                    clean.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = clean;
                    return;
                }

                // 絶対パスのディレクトリまたはファイル判定
                try
                {
                    if (Path.IsPathRooted(clean) || (clean.Length >= 2 && char.IsLetter(clean[0]) && clean[1] == ':'))
                    {
                        if (Directory.Exists(clean))
                        {
                            targetPath = clean;
                            return;
                        }
                        if (File.Exists(clean))
                        {
                            string? parent = Path.GetDirectoryName(clean);
                            targetPath = string.IsNullOrEmpty(parent) ? clean : parent;
                            selectItem = Path.GetFileName(clean);
                            return;
                        }

                        // Directory.Exists / File.Exists が判定できない場合（Googleドライブ仮想パスなど）もパスとして採用
                        targetPath = clean;
                        return;
                    }
                }
                catch
                {
                    targetPath = clean;
                    return;
                }
            }

            // 4. スペース区切りで分割されていた絶対パス全体の結合判定 (例: C:\Program Files\Some Folder)
            string joinedRaw = string.Join(" ", validArgs);
            string joinedPath = CleanArgumentPath(joinedRaw);
            if (!string.IsNullOrEmpty(joinedPath) && (Path.IsPathRooted(joinedPath) || (joinedPath.Length >= 2 && char.IsLetter(joinedPath[0]) && joinedPath[1] == ':')) && !IsOwnBinaryOrFlag(joinedPath))
            {
                try
                {
                    if (Directory.Exists(joinedPath))
                    {
                        targetPath = joinedPath;
                        return;
                    }
                    if (File.Exists(joinedPath))
                    {
                        string? parent = Path.GetDirectoryName(joinedPath);
                        targetPath = string.IsNullOrEmpty(parent) ? joinedPath : parent;
                        selectItem = Path.GetFileName(joinedPath);
                        return;
                    }
                    targetPath = joinedPath;
                    return;
                }
                catch
                {
                    targetPath = joinedPath;
                    return;
                }
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Log(string msg)
        {
            // Debug build only
        }

        public static void HandleRemoteActivation(string[] args)
        {
            if (HasBackgroundFlag(args))
            {
                return;
            }

            _appDispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
            {
                ParseArguments(args, out string? targetPath, out string? selectItem);
                OpenOrCreateWindow(targetPath, selectItem);
            });
        }

        public static void OpenOrCreateWindow(string? initialPath = null, string? selectItemName = null)
        {
            if (OpenWindows.Count > 0)
            {
                var existing = OpenWindows.Last();
                if (existing.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                    {
                        presenter.Restore();
                    }
                }
                existing.AppWindow.Show();
                existing.Activate();
                Win32Interop.ForceForegroundWindow(existing.WindowHandle);

                if (!string.IsNullOrEmpty(initialPath))
                {
                    existing.CreateNewTab(initialPath, selectItemName);
                }
                else if (existing.TabCount == 0)
                {
                    // タブが0個で非表示待機していた場合はホームタブを1つ作成
                    existing.CreateNewTab(ConfigService.Current.Startup.DefaultPath, null);
                }
            }
            else
            {
                var window = new MainWindow(initialPath: initialPath, selectItemName: selectItemName);
                RegisterWindow(window);
                window.Activate();
                Win32Interop.ForceForegroundWindow(window.WindowHandle);
            }
        }

        public static void RegisterWindow(MainWindow window)
        {
            if (!OpenWindows.Contains(window))
            {
                OpenWindows.Add(window);
                window.Closed += (s, e) =>
                {
                    if (s is MainWindow mw)
                    {
                        OpenWindows.Remove(mw);
                    }
                };
            }
        }
    }
}

