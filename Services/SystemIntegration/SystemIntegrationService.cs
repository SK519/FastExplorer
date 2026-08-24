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
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Folder\shell\open\command");
                if (key != null)
                {
                    string? val = key.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(val) && val.Contains("FastExplorer", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] IsDefaultExplorerEnabled error: {ex.Message}");
            }
            return false;
        }

        public static bool SetAsDefaultExplorer(bool enable)
        {
            try
            {
                string exePath = GetCurrentExecutablePath();
                string commandValue = $"\"{exePath}\" \"%1\"";

                string[] baseShells = [
                    @"Software\Classes\Directory\shell",
                    @"Software\Classes\Folder\shell",
                    @"Software\Classes\Drive\shell",
                    @"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell"
                ];

                string[] commandKeys = [
                    @"Software\Classes\Directory\shell\open\command",
                    @"Software\Classes\Folder\shell\open\command",
                    @"Software\Classes\Drive\shell\open\command",
                    @"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open\command"
                ];

                if (enable)
                {
                    // 1. 各 shell の既定動作を open に設定
                    foreach (var shellPath in baseShells)
                    {
                        using var shellKey = Registry.CurrentUser.CreateSubKey(shellPath);
                        shellKey?.SetValue(null, "open");
                    }

                    // 2. 各 command に FastExplorer を設定し、Explorer の DelegateExecute を空文字列で無効化
                    foreach (var cmdPath in commandKeys)
                    {
                        using var cmdKey = Registry.CurrentUser.CreateSubKey(cmdPath);
                        cmdKey?.SetValue(null, commandValue);
                        cmdKey?.SetValue("DelegateExecute", string.Empty);
                    }
                }
                else
                {
                    // 1. 各 command を削除
                    foreach (var cmdPath in commandKeys)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(cmdPath, throwOnMissingSubKey: false);
                    }

                    // 2. shell の既定値をクリア
                    foreach (var shellPath in baseShells)
                    {
                        try
                        {
                            using var shellKey = Registry.CurrentUser.OpenSubKey(shellPath, true);
                            shellKey?.DeleteValue(string.Empty, false);
                        }
                        catch { }
                    }

                    // 3. 空の open サブキーをクリーンアップ
                    string[] parentKeys = [
                        @"Software\Classes\Directory\shell\open",
                        @"Software\Classes\Folder\shell\open",
                        @"Software\Classes\Drive\shell\open",
                        @"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell\open"
                    ];
                    foreach (var parent in parentKeys)
                    {
                        try
                        {
                            using var pk = Registry.CurrentUser.OpenSubKey(parent);
                            if (pk != null && pk.SubKeyCount == 0 && pk.ValueCount == 0)
                            {
                                Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false);
                            }
                        }
                        catch { }
                    }
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

        public static bool RegisterWinEHotKey(nint hWnd)
        {
            // 1. レジストリで Explorer の Win+E を無効化する (DisabledHotkeys)
            try
            {
                using var advKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (advKey != null)
                {
                    string existing = (advKey.GetValue("DisabledHotkeys") as string) ?? string.Empty;
                    if (!existing.Contains("E", StringComparison.OrdinalIgnoreCase))
                    {
                        advKey.SetValue("DisabledHotkeys", existing + "E");
                    }
                }
            }
            catch { }

            // 2. RegisterHotKey を試行
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

            // 3. 低レベルキーボードフックを常駐 (100% 確実に Win+E をインターセプト)
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
            // 1. UnregisterHotKey
            if (hWnd != nint.Zero)
            {
                try
                {
                    Win32Interop.UnregisterHotKey(hWnd, WIN_E_HOTKEY_ID);
                }
                catch { }
            }

            // 2. フック解除
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

        private static nint HookCallback(int nCode, nuint wParam, nint lParam)
        {
            if (nCode >= 0 && (wParam == Win32Interop.WM_KEYDOWN || wParam == Win32Interop.WM_SYSKEYDOWN))
            {
                try
                {
                    var hookStruct = Marshal.PtrToStructure<Win32Interop.KBDLLHOOKSTRUCT>(lParam);
                    if (hookStruct.vkCode == Win32Interop.VK_E)
                    {
                        bool isLWinDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_LWIN) & 0x8000) != 0;
                        bool isRWinDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_RWIN) & 0x8000) != 0;

                        if (isLWinDown || isRWinDown)
                        {
                            Debug.WriteLine("[SystemIntegration] Intercepted Win + E globally via Hook!");
                            WinEHotKeyPressed?.Invoke();
                            return (nint)1; // イベントをここで消費し、Windows 標準 Explorer の起動を完全に阻止
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
