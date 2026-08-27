using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static partial class SystemIntegrationService
    {
        public const int WIN_E_HOTKEY_ID = 9001;

        public static event Action? WinEHotKeyPressed;

        private static nint _hookHandle = nint.Zero;
        private static Win32Interop.LowLevelKeyboardProc? _hookProc;
        private static bool _isLWinDown = false;
        private static bool _isRWinDown = false;

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
                                if (ConfigService.Current.SystemIntegration.ReplaceDefaultExplorer || ConfigService.Current.SystemIntegration.InterceptWinE)
                                {
                                    Debug.WriteLine("[SystemIntegration] Intercepted Win + E globally via Hook!");
                                    WinEHotKeyPressed?.Invoke();
                                    return (nint)1; // イベントをここで消費し、Windows 標準 Explorer の起動を阻止
                                }
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
