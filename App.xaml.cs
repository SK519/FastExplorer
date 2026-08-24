using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    string crashLog = Path.Combine(AppContext.BaseDirectory, "crash.log");
                    File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UnhandledException: {e.Message}\r\n{e.Exception}\r\n\r\n");
                }
                catch { }
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    string crashLog = Path.Combine(AppContext.BaseDirectory, "crash.log");
                    File.AppendAllText(crashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AppDomain Exception: {e.ExceptionObject}\r\n\r\n");
                }
                catch { }
            };

            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            string[] cmdArgs = Environment.GetCommandLineArgs();
            string? targetPath = null;
            string? selectItem = null;

            if (cmdArgs.Length > 1)
            {
                for (int i = 1; i < cmdArgs.Length; i++)
                {
                    string arg = cmdArgs[i].Trim('"', ' ');
                    if (string.IsNullOrWhiteSpace(arg)) continue;

                    if (arg.StartsWith("/select,", StringComparison.OrdinalIgnoreCase))
                    {
                        string selectPath = arg.Substring(8).Trim('"', ' ');
                        if (string.IsNullOrEmpty(selectPath) && i + 1 < cmdArgs.Length)
                        {
                            selectPath = cmdArgs[++i].Trim('"', ' ');
                        }

                        if (!string.IsNullOrEmpty(selectPath))
                        {
                            if (File.Exists(selectPath) || Directory.Exists(selectPath))
                            {
                                string? parent = Path.GetDirectoryName(selectPath);
                                targetPath = string.IsNullOrEmpty(parent) ? selectPath : parent;
                                selectItem = Path.GetFileName(selectPath);
                            }
                            else
                            {
                                targetPath = selectPath;
                            }
                            break;
                        }
                    }
                    else if (RecycleBinService.IsRecycleBinPath(arg))
                    {
                        targetPath = RecycleBinService.RecycleBinUri;
                        break;
                    }
                    else if (Directory.Exists(arg))
                    {
                        targetPath = arg;
                        break;
                    }
                    else if (File.Exists(arg))
                    {
                        string? parent = Path.GetDirectoryName(arg);
                        targetPath = string.IsNullOrEmpty(parent) ? arg : parent;
                        selectItem = Path.GetFileName(arg);
                        break;
                    }
                    else if (arg.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                             arg.StartsWith("::", StringComparison.OrdinalIgnoreCase) ||
                             arg.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                             arg.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) ||
                             arg.Equals("Home", StringComparison.OrdinalIgnoreCase))
                    {
                        targetPath = arg;
                        break;
                    }
                }
            }

            var window = new MainWindow(initialPath: targetPath, selectItemName: selectItem);
            RegisterWindow(window);
            window.Activate();

            // システム連携設定の同期・常駐
            try
            {
                if (ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer)
                {
                    SystemIntegrationService.SetAsDefaultExplorer(true);
                }
                if (ConfigService.Current.SystemIntegration.AddContextMenuToFolders)
                {
                    SystemIntegrationService.SetContextMenuIntegration(true);
                }
                if (ConfigService.Current.SystemIntegration.InterceptWinE)
                {
                    SystemIntegrationService.RegisterWinEHotKey(window.WindowHandle);
                }
            }
            catch { }
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

