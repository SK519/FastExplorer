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
                UpdateKeyBlockingRegistry(
                    ConfigService.Current.SystemIntegration.DisableCopilotKey,
                    ConfigService.Current.SystemIntegration.DisableCapsLockKey
                );
                if (ConfigService.Current.SystemIntegration.DisableCapsLockKey)
                {
                    EnsureCapsLockOff();
                }
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

        public static void EnsureCapsLockOff()
        {
            try
            {
                if ((Win32Interop.GetKeyState(0x14) & 0x0001) != 0)
                {
                    Win32Interop.keybd_event(0x14, 0x3A, 0, 0);
                    Win32Interop.keybd_event(0x14, 0x3A, 2 /* KEYEVENTF_KEYUP */, 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] EnsureCapsLockOff error: {ex.Message}");
            }
        }

        public static void UpdateKeyBlockingRegistry(bool disableCopilot, bool disableCapsLock)
        {
            try
            {
                using var appKey = Registry.CurrentUser.CreateSubKey(@"Software\FastExplorer");
                if (appKey != null)
                {
                    appKey.SetValue("DisableCopilotKey", disableCopilot ? 1 : 0, RegistryValueKind.DWord);
                    appKey.SetValue("DisableCapsLockKey", disableCapsLock ? 1 : 0, RegistryValueKind.DWord);
                }

                if (disableCapsLock)
                {
                    EnsureCapsLockOff();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] UpdateKeyBlockingRegistry error: {ex.Message}");
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
                        // 1. Directory (フォルダーのダブルクリック / 開く / 探索)
                        // ※ Windows 標準の DelegateExecute (CLSID_ExecuteFolder) を残すと標準エクスプローラーが優先起動する場合があるため削除する
                        using (var shellKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell"))
                        {
                            shellKey?.SetValue(null, "open");
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\open\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.DeleteValue("DelegateExecute", false);
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\explore\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.DeleteValue("DelegateExecute", false);
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
                            key?.DeleteValue("DelegateExecute", false);
                        }
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\explore\command"))
                        {
                            key?.SetValue(null, commandValue);
                            key?.DeleteValue("DelegateExecute", false);
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
                        try { SetContextMenuIntegration(true); } catch { }

                        // 6. スタートアップ & タスクスケジューラ登録（FastExplorerWatcher.exe）
                        try { SetStartupRunKey(true); } catch { }

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
