using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static class SystemIntegrationService
    {
        public const int WIN_E_HOTKEY_ID = 9001;

        public static event Action? WinEHotKeyPressed;

        private static nint _hookHandle = nint.Zero;
        private static Win32Interop.LowLevelKeyboardProc? _hookProc;

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

        public static bool SetAsDefaultExplorer(bool enable)
        {
            try
            {
                string exePath = GetCurrentExecutablePath();
                string commandValue = $"\"{exePath}\" \"%1\"";

                if (enable)
                {
                    // 1. Directory (フォルダーのダブルクリック / 開く)
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\open\command"))
                    {
                        key?.SetValue(null, commandValue);
                    }

                    // 2. Folder の open/explore は設定しない
                    // (※ HKCU の Folder\shell\open に DelegateExecute="" を設定すると、Windows Shell の COM 処理が失敗し
                    // スタートメニュー「ファイルの場所を開く」や特殊システムフォルダーが開けなくなるため。
                    // 過去の設定が残っている場合はクリーンアップする)
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Folder\shell\open", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Folder\shell\explore", throwOnMissingSubKey: false);

                    // 3. Drive (ドライブのダブルクリック / 開く)
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\open\command"))
                    {
                        key?.SetValue(null, commandValue);
                    }

                    // 4. ごみ箱 (CLSID)
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open\command"))
                    {
                        key?.SetValue(null, $"\"{exePath}\" \"shell:RecycleBinFolder\"");
                    }

                    // 5. コンテキストメニュー「FastExplorer で開く」
                    SetContextMenuIntegration(true);

                    // 6. スタートアップ & タスクスケジューラ登録（FastExplorerWatcher.exe）
                    SetStartupRunKey(true);

                    // 7. アプリケーションインストール先パスの記録
                    using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\FastExplorer"))
                    {
                        appKey?.SetValue("InstallPath", exePath);
                    }
                    using (var appPathKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\FastExplorer.exe"))
                    {
                        appPathKey?.SetValue(null, exePath);
                        appPathKey?.SetValue("Path", AppDomain.CurrentDomain.BaseDirectory);
                    }
                }
                else
                {
                    // 1. 各 open サブキーを完全削除して Windows 標準 (HKLM) に戻す
                    string[] openKeys = [
                        @"Software\Classes\Directory\shell\open",
                        @"Software\Classes\Folder\shell\open",
                        @"Software\Classes\Folder\shell\explore",
                        @"Software\Classes\Drive\shell\open",
                        @"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open"
                    ];
                    foreach (var openKey in openKeys)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(openKey, throwOnMissingSubKey: false);
                    }

                    // 2. スタートアップ & タスクスケジューラ登録解除
                    SetStartupRunKey(false);
                    SetTaskSchedulerTask(false);
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

        #region Win + E Global Keyboard Interception

        public static void EnsureCleanExplorerDisabledHotkeys()
        {
            try
            {
                using var advKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
                if (advKey != null)
                {
                    string existing = (advKey.GetValue("DisabledHotkeys") as string) ?? string.Empty;
                    if (existing.Contains("E", StringComparison.OrdinalIgnoreCase))
                    {
                        string updated = existing.Replace("E", "", StringComparison.OrdinalIgnoreCase).Replace("e", "", StringComparison.OrdinalIgnoreCase);
                        if (string.IsNullOrEmpty(updated))
                        {
                            advKey.DeleteValue("DisabledHotkeys", false);
                        }
                        else
                        {
                            advKey.SetValue("DisabledHotkeys", updated);
                        }
                    }
                }
            }
            catch { }
        }

        public static bool RegisterWinEHotKey(nint hWnd)
        {
            // レジストリの DisabledHotkeys は不要（低レベルキーボードフック WH_KEYBOARD_LL 単体で Explorer より先に消費できるため）
            // 過去に設定された DisabledHotkeys があれば念のため削除して Windows 標準の動作を保護
            EnsureCleanExplorerDisabledHotkeys();

            // 1. RegisterHotKey を試行
            if (hWnd != nint.Zero)
            {
                try
                {
                    Win32Interop.RegisterHotKey(
                        hWnd,
                        WIN_E_HOTKEY_ID,
                        Win32Interop.MOD_WIN | Win32Interop.MOD_NOREPEAT,
                        Win32Interop.VK_E);
                }
                catch { }
            }

            // 2. 低レベルキーボードフックを常駐 (100% 確実に Win+E をインターセプト)
            try
            {
                if (_hookHandle == nint.Zero)
                {
                    _hookProc = HookCallback;
                    using var curProcess = Process.GetCurrentProcess();
                    using var curModule = curProcess.MainModule;
                    nint hModule = Win32Interop.GetModuleHandle(curModule?.ModuleName);

                    _hookHandle = Win32Interop.SetWindowsHookEx(
                        Win32Interop.WH_KEYBOARD_LL,
                        _hookProc,
                        hModule,
                        0);

                    Debug.WriteLine($"[SystemIntegration] SetWindowsHookEx returned: {_hookHandle != nint.Zero}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] RegisterWinEHotKey hook error: {ex.Message}");
                return false;
            }
        }

        public static bool UnregisterWinEHotKey(nint hWnd)
        {
            // 1. レジストリの DisabledHotkeys のクリーンアップ
            EnsureCleanExplorerDisabledHotkeys();

            // 2. UnregisterHotKey
            if (hWnd != nint.Zero)
            {
                try
                {
                    Win32Interop.UnregisterHotKey(hWnd, WIN_E_HOTKEY_ID);
                }
                catch { }
            }

            // 3. フック解除
            if (_hookHandle != nint.Zero)
            {
                try
                {
                    Win32Interop.UnhookWindowsHookEx(_hookHandle);
                }
                catch { }
                _hookHandle = nint.Zero;
                _hookProc = null;
            }

            return true;
        }

        private static bool _isLWinDown = false;
        private static bool _isRWinDown = false;

        private static nint HookCallback(int nCode, nuint wParam, nint lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<Win32Interop.KBDLLHOOKSTRUCT>(lParam);
                    if (hookStruct.vkCode == Win32Interop.VK_LWIN)
                    {
                        if (wParam == Win32Interop.WM_KEYDOWN || wParam == Win32Interop.WM_SYSKEYDOWN)
                            _isLWinDown = true;
                        else if (wParam == Win32Interop.WM_KEYUP || wParam == Win32Interop.WM_SYSKEYUP)
                            _isLWinDown = false;
                    }
                    else if (hookStruct.vkCode == Win32Interop.VK_RWIN)
                    {
                        if (wParam == Win32Interop.WM_KEYDOWN || wParam == Win32Interop.WM_SYSKEYDOWN)
                            _isRWinDown = true;
                        else if (wParam == Win32Interop.WM_KEYUP || wParam == Win32Interop.WM_SYSKEYUP)
                            _isRWinDown = false;
                    }
                    else if (hookStruct.vkCode == Win32Interop.VK_E)
                    {
                        if (wParam == Win32Interop.WM_KEYDOWN || wParam == Win32Interop.WM_SYSKEYDOWN)
                        {
                            bool isLWinPhys = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_LWIN) & 0x8000) != 0;
                            bool isRWinPhys = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_RWIN) & 0x8000) != 0;
                            bool isLWinSync = (Win32Interop.GetKeyState(Win32Interop.VK_LWIN) & 0x8000) != 0;
                            bool isRWinSync = (Win32Interop.GetKeyState(Win32Interop.VK_RWIN) & 0x8000) != 0;

                            bool isWinDown = _isLWinDown || _isRWinDown || (isLWinPhys && isLWinSync) || (isRWinPhys && isRWinSync);

                            bool isCtrlDown = (Win32Interop.GetAsyncKeyState(0x11) & 0x8000) != 0;
                            bool isAltDown = (Win32Interop.GetAsyncKeyState(0x12) & 0x8000) != 0;
                            bool isShiftDown = (Win32Interop.GetAsyncKeyState(0x10) & 0x8000) != 0;

                            if (isWinDown && !isCtrlDown && !isAltDown && !isShiftDown)
                            {
                                Debug.WriteLine("[SystemIntegration] Intercepted Win + E globally via Hook!");
                                WinEHotKeyPressed?.Invoke();
                                return (nint)1; // イベントをここで消費し、Windows 標準 Explorer の起動を完全に阻止
                            }
                        }
                    }
                }
                catch { }
            }

            return Win32Interop.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        #endregion
    }
}
