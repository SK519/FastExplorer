using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static partial class SystemIntegrationService
    {
        private static string GetCurrentExecutablePath()
        {
            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(procPath) && File.Exists(procPath))
            {
                return procPath;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exePath = Path.Combine(baseDir, "FastExplorer.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            return procPath ?? "FastExplorer.exe";
        }

        #region Default File Explorer (Folder & Directory Association)

        public static bool IsDefaultExplorerEnabled()
        {
            try
            {
                return ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] IsDefaultExplorerEnabled error: {ex.Message}");
            }
            return false;
        }

        public static void EnsureDefaultExplorerIntegration()
        {
            try
            {
                EnsureCleanExplorerDisabledHotkeys();
                if (ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer)
                {
                    SetAsDefaultExplorer(true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] EnsureDefaultExplorerIntegration error: {ex.Message}");
            }
        }

        private static readonly object _defaultExplorerLock = new();
        private static long _lastExplorerRestartTime = 0;

        public static bool SetAsDefaultExplorer(bool enable)
        {
            lock (_defaultExplorerLock)
            {
                try
                {
                    EnsureCleanExplorerDisabledHotkeys();
                    string exePath = GetCurrentExecutablePath();
                    string commandValue = $"\"{exePath}\" \"%1\"";

                    if (enable)
                    {
                        const string explorerDelegateExecuteGuid = "{11dbb47c-a525-400b-9e80-a54615a090c0}";

                        // 1. Directory (フォルダーのダブルクリック / 開く / 探索)
                        using (var shellKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell"))
                        {
                            shellKey?.SetValue(null, "open");
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\open\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.SetValue("DelegateExecute", explorerDelegateExecuteGuid);
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\explore\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.SetValue("DelegateExecute", explorerDelegateExecuteGuid);
                        }

                        // 2. Folder の不正なオーバーライドを削除して Windows シェルの破損を防止
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Folder\shell\open", throwOnMissingSubKey: false);
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Folder\shell\explore", throwOnMissingSubKey: false);
                        using (var folderShellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Folder\shell", true))
                        {
                            folderShellKey?.DeleteValue("", false);
                        }

                        // 3. Drive (ドライブのダブルクリック / 開く / 探索)
                        using (var shellKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell"))
                        {
                            shellKey?.SetValue(null, "open");
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\open\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.SetValue("DelegateExecute", explorerDelegateExecuteGuid);
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\explore\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.SetValue("DelegateExecute", explorerDelegateExecuteGuid);
                        }

                        // 4. ごみ箱 (CLSID)
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open\command"))
                        {
                            key?.SetValue(null, $"\"{exePath}\" \"shell:RecycleBinFolder\"");
                            key?.DeleteValue("DelegateExecute", false);
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\explore\command"))
                        {
                            key?.SetValue(null, $"\"{exePath}\" \"shell:RecycleBinFolder\"");
                            key?.DeleteValue("DelegateExecute", false);
                        }

                        // 5. コンテキストメニュー「FastExplorer で開く」
                        SetContextMenuIntegration(true);

                        // 6. スタートアップ & タスクスケジューラ登録（FastExplorerWatcher.exe）
                        SetStartupRunKey(true);

                        // 7. アプリケーションインストール先パスの記録
                        using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\FastExplorer"))
                        {
                            appKey?.SetValue("InstallPath", exePath);
                            appKey?.SetValue("ReplaceDefaultExplorer", 1, RegistryValueKind.DWord);
                        }
                        using (var appPathKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\FastExplorer.exe"))
                        {
                            appPathKey?.SetValue(null, exePath);
                            appPathKey?.SetValue("Path", AppDomain.CurrentDomain.BaseDirectory);
                        }
                    }
                    else
                    {
                        using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\FastExplorer"))
                        {
                            appKey?.SetValue("ReplaceDefaultExplorer", 0, RegistryValueKind.DWord);
                        }

                        // 1. 各 open / explore サブキーを完全削除して Windows 標準 (HKLM) に戻す
                        string[] openKeys = [
                            @"Software\Classes\Directory\shell\open",
                            @"Software\Classes\Directory\shell\explore",
                            @"Software\Classes\Folder\shell\open",
                            @"Software\Classes\Folder\shell\explore",
                            @"Software\Classes\Drive\shell\open",
                            @"Software\Classes\Drive\shell\explore",
                            @"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open",
                            @"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\explore"
                        ];
                        foreach (var openKey in openKeys)
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(openKey, throwOnMissingSubKey: false);
                        }

                        // 2. shell の (既定) 値を削除
                        string[] shellKeys = [
                            @"Software\Classes\Directory\shell",
                            @"Software\Classes\Folder\shell",
                            @"Software\Classes\Drive\shell"
                        ];
                        foreach (var sKey in shellKeys)
                        {
                            using var key = Registry.CurrentUser.OpenSubKey(sKey, true);
                            key?.DeleteValue("", false);
                        }

                        // 3. スタートアップ & タスクスケジューラ登録解除
                        SetStartupRunKey(false);
                        SetTaskSchedulerTask(false);
                        EnsureCleanExplorerDisabledHotkeys();
                        KillWatcherProcesses();
                        RestartWindowsExplorer();
                    }

                    ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer = enable;
                    ConfigService.Save();
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SystemIntegration] SetAsDefaultExplorer error: {ex.Message}");
                    return false;
                }
            }
        }

        public static void RestartWindowsExplorer()
        {
            try
            {
                long now = Environment.TickCount64;
                if (now - _lastExplorerRestartTime < 4000)
                {
                    // 直近4秒以内の連続再起動呼び出しを防止
                    return;
                }
                _lastExplorerRestartTime = now;

                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); } catch { }
                }

                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(800);
                    if (Process.GetProcessesByName("explorer").Length == 0)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] RestartWindowsExplorer error: {ex.Message}");
            }
        }

        private static void KillWatcherProcesses()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("FastExplorerWatcher"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        private static string GetWatcherExecutablePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "FastExplorerWatcher.exe");
            if (File.Exists(path)) return path;

            string devPath = Path.Combine(baseDir, "..", "..", "..", "Watcher", "bin", "FastExplorerWatcher.exe");
            if (File.Exists(devPath)) return Path.GetFullPath(devPath);

            return path;
        }

        public static bool SetTaskSchedulerTask(bool enable)
        {
            try
            {
                string taskName = "FastExplorer_Background";
                if (enable)
                {
                    string watcherExe = GetWatcherExecutablePath();
                    string tempXml = Path.Combine(Path.GetTempPath(), "FastExplorer_Task.xml");

                    string xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>FastExplorer Background Resident for Instant Launch</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{watcherExe}</Command>
    </Exec>
  </Actions>
</Task>";

                    File.WriteAllText(tempXml, xmlContent, System.Text.Encoding.Unicode);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/create /tn \"{taskName}\" /xml \"{tempXml}\" /f",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);

                    try { File.Delete(tempXml); } catch { }

                    // Watcher プロセスを今すぐ起動
                    if (File.Exists(watcherExe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = watcherExe,
                            UseShellExecute = true,
                            CreateNoWindow = true
                        });
                    }

                    return proc?.ExitCode == 0;
                }
                else
                {
                    // Watcher プロセスをキル
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("FastExplorerWatcher"))
                        {
                            p.Kill();
                        }
                    }
                    catch { }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/delete /tn \"{taskName}\" /f",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                    return proc?.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetTaskSchedulerTask error: {ex.Message}");
                return false;
            }
        }

        public static bool SetStartupFolderShortcut(bool enable)
        {
            try
            {
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string shortcutPath = Path.Combine(startupFolder, "FastExplorerWatcher.lnk");
                string legacyShortcut = Path.Combine(startupFolder, "FastExplorer.lnk");

                // 常に古い FastExplorer.lnk をクリーンアップ
                if (File.Exists(legacyShortcut))
                {
                    try { File.Delete(legacyShortcut); } catch { }
                }

                if (enable)
                {
                    string watcherExe = GetWatcherExecutablePath();
                    string psCommand = $"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{shortcutPath}'); $s.TargetPath = '{watcherExe}'; $s.IconLocation = '{watcherExe},0'; $s.Save()";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -Command \"{psCommand}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                    return File.Exists(shortcutPath);
                }
                else
                {
                    if (File.Exists(shortcutPath))
                    {
                        File.Delete(shortcutPath);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetStartupFolderShortcut error: {ex.Message}");
                return false;
            }
        }

        public static bool SetStartupRunKey(bool enable)
        {
            try
            {
                // 1. スタートアップフォルダー
                SetStartupFolderShortcut(enable);

                // 2. タスクスケジューラ
                SetTaskSchedulerTask(enable);

                // 3. レジストリ Run キー
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (runKey != null)
                {
                    if (enable)
                    {
                        string watcherExe = GetWatcherExecutablePath();
                        runKey.SetValue("FastExplorerWatcher", $"\"{watcherExe}\"");
                    }
                    else
                    {
                        runKey.DeleteValue("FastExplorerWatcher", false);
                        runKey.DeleteValue("FastExplorer", false);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetStartupRunKey error: {ex.Message}");
            }
            return false;
        }

        #endregion

        #region Context Menu Integration ("FastExplorer で開く")

        public static bool IsContextMenuIntegrationEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\FastExplorer");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool SetContextMenuIntegration(bool enable)
        {
            try
            {
                string exePath = GetCurrentExecutablePath();
                string menuText = "FastExplorer で開く";
                string iconValue = $"\"{exePath}\",0";

                if (enable)
                {
                    // 1. Directory (フォルダー選択時)
                    using (var dirKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\FastExplorer"))
                    {
                        dirKey?.SetValue(null, menuText);
                        dirKey?.SetValue("Icon", iconValue);
                        using var cmdKey = dirKey?.CreateSubKey("command");
                        cmdKey?.SetValue(null, $"\"{exePath}\" \"%1\"");
                    }

                    // 2. Directory Background (フォルダー背景右クリック時)
                    using (var bgKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\FastExplorer"))
                    {
                        bgKey?.SetValue(null, menuText);
                        bgKey?.SetValue("Icon", iconValue);
                        using var cmdKey = bgKey?.CreateSubKey("command");
                        cmdKey?.SetValue(null, $"\"{exePath}\" \"%V\"");
                    }

                    // 3. Drive (ドライブ選択時)
                    using (var driveKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\FastExplorer"))
                    {
                        driveKey?.SetValue(null, menuText);
                        driveKey?.SetValue("Icon", iconValue);
                        using var cmdKey = driveKey?.CreateSubKey("command");
                        cmdKey?.SetValue(null, $"\"{exePath}\" \"%1\"");
                    }

                    // 4. Folder (特殊フォルダー等)
                    using (var folderKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Folder\shell\FastExplorer"))
                    {
                        folderKey?.SetValue(null, menuText);
                        folderKey?.SetValue("Icon", iconValue);
                        using var cmdKey = folderKey?.CreateSubKey("command");
                        cmdKey?.SetValue(null, $"\"{exePath}\" \"%1\"");
                    }
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\FastExplorer", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\FastExplorer", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Drive\shell\FastExplorer", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Folder\shell\FastExplorer", false);
                }

                ConfigService.Current.SystemIntegration.AddContextMenuToFolders = enable;
                ConfigService.Save();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetContextMenuIntegration error: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
