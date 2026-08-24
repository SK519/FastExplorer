using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static class ShellContextMenuService
    {
        private const uint CMD_FIRST = 1;
        private const uint CMD_LAST = 0x7FFF;

        #region Facade Delegates for Backward Compatibility

        public static bool HasAnyShellExtractionEnabled(Models.ShellMenuConfig config) => ShellMenuFilter.HasAnyShellExtractionEnabled(config);
        public static bool IsBuiltinDuplicate(string text) => ShellMenuFilter.IsBuiltinDuplicate(text);
        public static bool MatchesShellConfig(string itemText, Models.ShellMenuConfig config, out string glyph) => ShellMenuFilter.MatchesShellConfig(itemText, config, out glyph);
        public static bool MatchesShellConfig(string parentLabel, string childText, Models.ShellMenuConfig config, out string glyph) => ShellMenuFilter.MatchesShellConfig(parentLabel, childText, config, out glyph);
        public static string GetSmartGlyph(string text) => ShellMenuFilter.GetSmartGlyph(text);
        public static string? FindArchiverExe(string menuLabel) => ShellMenuFilter.FindArchiverExe(menuLabel);
        public static void InvokeShellCommand(nint hwnd, IReadOnlyList<string> filePaths, ExtractedShellItem item) => ShellCommandExecutor.InvokeShellCommand(hwnd, filePaths, item);

        #endregion

        private static List<string> FilterValidPaths(IReadOnlyList<string> filePaths)
        {
            var result = new List<string>(filePaths.Count);
            foreach (var p in filePaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    if (File.Exists(p) || Directory.Exists(p))
                    {
                        result.Add(p);
                        continue;
                    }
                }
                catch { }

                if (p.StartsWith(@"\\") || (p.Length >= 2 && p[1] == ':'))
                {
                    result.Add(p);
                }
            }
            return result;
        }

        public static List<ExtractedShellItem> ExtractMatchingShellItems(nint hwnd, IReadOnlyList<string> filePaths)
        {
            var result = new List<ExtractedShellItem>();
            if (filePaths == null || filePaths.Count == 0) return result;

            var validPaths = FilterValidPaths(filePaths);
            if (validPaths.Count == 0) return result;

            if (!ShellComHelper.TryGetContextMenuForPaths(hwnd, validPaths, out var pContextMenu, out var comObjectsToRelease, out var pidlsToFree))
            {
                return result;
            }

            try
            {
                nint hMenu = Win32Interop.CreatePopupMenu();
                if (hMenu == nint.Zero) return result;

                try
                {
                    int hr = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(pContextMenu, hMenu, 0, CMD_FIRST, CMD_LAST, Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE | Win32Interop.CMF_EXTENDEDVERBS);
                    if (hr >= 0)
                    {
                        int itemCount = Win32Interop.GetMenuItemCount(hMenu);
                        var config = ConfigService.Current.ShellMenu;

                        for (int i = 0; i < itemCount; i++)
                        {
                            var sb = new System.Text.StringBuilder(256);
                            Win32Interop.GetMenuString(hMenu, (uint)i, sb, sb.Capacity, Win32Interop.MF_BYPOSITION);
                            string rawText = sb.ToString();
                            if (string.IsNullOrWhiteSpace(rawText)) continue;

                            string cleanText = rawText.Replace("&", "").Trim();
                            uint cmdId = Win32Interop.GetMenuItemID(hMenu, i);
                            if (IsBuiltinDuplicate(cleanText)) continue;

                            if (MatchesShellConfig(cleanText, config, out string glyph))
                            {
                                string? verb = ShellCommandExecutor.GetVerbFromContextMenu(pContextMenu, cmdId);
                                result.Add(new ExtractedShellItem
                                {
                                    CommandId = cmdId,
                                    Text = rawText,
                                    CleanText = cleanText,
                                    Glyph = glyph,
                                    Verb = verb
                                });
                            }
                        }
                    }
                }
                finally
                {
                    Win32Interop.DestroyMenu(hMenu);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExtractShell] Error: {ex.Message}");
            }
            finally
            {
                if (pContextMenu != nint.Zero) Win32Interop.NativeCom.Release(pContextMenu);
                ShellComHelper.ReleaseComObjects(comObjectsToRelease);
                ShellComHelper.FreePidls(pidlsToFree);
            }

            return result;
        }

        #region Subclassing & Message Handling for IContextMenu2 / IContextMenu3

        [StructLayout(LayoutKind.Sequential)]
        private struct MenuSubclassState
        {
            public nint PContextMenu2;
            public nint PContextMenu3;
        }

        private static readonly Win32Interop.SubclassProc _menuSubclassProc = MenuSubclassProc;

        private static unsafe nint MenuSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
        {
            if (dwRefData != nint.Zero)
            {
                try
                {
                    var pState = (MenuSubclassState*)dwRefData;
                    switch (uMsg)
                    {
                        case Win32Interop.WM_INITMENUPOPUP:
                            if (pState->PContextMenu3 != nint.Zero)
                            {
                                if (Win32Interop.NativeCom.ContextMenu3_HandleMenuMsg2(pState->PContextMenu3, uMsg, wParam, lParam, out nint lResult) == 0 /* S_OK */)
                                {
                                    return lResult;
                                }
                            }
                            else if (pState->PContextMenu2 != nint.Zero)
                            {
                                if (Win32Interop.NativeCom.ContextMenu2_HandleMenuMsg(pState->PContextMenu2, uMsg, wParam, lParam) == 0 /* S_OK */)
                                {
                                    return nint.Zero;
                                }
                            }
                            break;

                        case Win32Interop.WM_DRAWITEM:
                        case Win32Interop.WM_MEASUREITEM:
                            if (pState->PContextMenu3 != nint.Zero)
                            {
                                if (Win32Interop.NativeCom.ContextMenu3_HandleMenuMsg2(pState->PContextMenu3, uMsg, wParam, lParam, out nint lResult) == 0 /* S_OK */)
                                {
                                    return lResult;
                                }
                            }
                            else if (pState->PContextMenu2 != nint.Zero)
                            {
                                if (Win32Interop.NativeCom.ContextMenu2_HandleMenuMsg(pState->PContextMenu2, uMsg, wParam, lParam) == 0 /* S_OK */)
                                {
                                    return (nint)1; // TRUE: handled
                                }
                            }
                            break;

                        case Win32Interop.WM_MENUCHAR:
                            if (pState->PContextMenu3 != nint.Zero)
                            {
                                if (Win32Interop.NativeCom.ContextMenu3_HandleMenuMsg2(pState->PContextMenu3, uMsg, wParam, lParam, out nint lResult) == 0 /* S_OK */)
                                {
                                    return lResult;
                                }
                            }
                            break;
                    }
                }
                catch
                {
                    // 例外発生時は DefSubclassProc に委譲
                }
            }

            return Win32Interop.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        #endregion

        public static void ShowContextMenuAsync(nint hwnd, IReadOnlyList<string> filePaths, Windows.Foundation.Point? screenPos = null, bool isShift = false)
        {
            if (filePaths == null || filePaths.Count == 0) return;
            var capturedPaths = filePaths.ToList();

            var thread = new System.Threading.Thread(() =>
            {
                Win32Interop.OleInitialize(nint.Zero);
                try
                {
                    ShowContextMenu(hwnd, capturedPaths, screenPos, isShift);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] Async Thread error: {ex.Message}");
                }
                finally
                {
                    Win32Interop.OleUninitialize();
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "ShellContextMenuThread";
            thread.Start();
        }

        public static void ShowFolderBackgroundContextMenuAsync(nint hwnd, string folderPath, Windows.Foundation.Point? screenPos = null, bool isShift = false)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            var thread = new System.Threading.Thread(() =>
            {
                Win32Interop.OleInitialize(nint.Zero);
                try
                {
                    ShowFolderBackgroundContextMenu(hwnd, folderPath, screenPos, isShift);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] Background Async Thread error: {ex.Message}");
                }
                finally
                {
                    Win32Interop.OleUninitialize();
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "ShellContextMenuThread";
            thread.Start();
        }

        public static void ShowContextMenu(nint hwnd, IReadOnlyList<string> filePaths, Windows.Foundation.Point? screenPos = null, bool isShift = false)
        {
            if (filePaths == null || filePaths.Count == 0) return;
            var validPaths = FilterValidPaths(filePaths);
            if (validPaths.Count == 0) return;

            string? workingDir = null;
            try
            {
                workingDir = Directory.Exists(validPaths[0]) ? validPaths[0] : Path.GetDirectoryName(validPaths[0]);
            }
            catch { }

            if (!ShellComHelper.TryGetContextMenuForPaths(hwnd, validPaths, out var pContextMenu, out var comObjectsToRelease, out var pidlsToFree))
                return;

            try
            {
                uint flags = Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE;
                if (isShift) flags |= Win32Interop.CMF_EXTENDEDVERBS;

                DisplayTrackPopupMenu(hwnd, pContextMenu, screenPos, flags, workingDir);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] Error: {ex.Message}");
            }
            finally
            {
                if (pContextMenu != nint.Zero) Win32Interop.NativeCom.Release(pContextMenu);
                ShellComHelper.ReleaseComObjects(comObjectsToRelease);
                ShellComHelper.FreePidls(pidlsToFree);
            }
        }

        public static void ShowFolderBackgroundContextMenu(nint hwnd, string folderPath, Windows.Foundation.Point? screenPos = null, bool isShift = false)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            try { if (!Directory.Exists(folderPath)) return; } catch { return; }

            if (!ShellComHelper.TryGetContextMenuForFolderBackground(hwnd, folderPath, out var pContextMenu, out var comObjectsToRelease, out var pidlsToFree))
            {
                // フォールバック: フォルダー自身の通常コンテキストメニュー
                ShowContextMenu(hwnd, [folderPath], screenPos, isShift);
                return;
            }

            try
            {
                uint flags = Win32Interop.CMF_NORMAL;
                if (isShift) flags |= Win32Interop.CMF_EXTENDEDVERBS;

                DisplayTrackPopupMenu(hwnd, pContextMenu, screenPos, flags, folderPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShellContextMenu] Background Error: {ex.Message}");
            }
            finally
            {
                if (pContextMenu != nint.Zero) Win32Interop.NativeCom.Release(pContextMenu);
                ShellComHelper.ReleaseComObjects(comObjectsToRelease);
                ShellComHelper.FreePidls(pidlsToFree);
            }
        }

        private static void DisplayTrackPopupMenu(nint hwnd, nint pContextMenu, Windows.Foundation.Point? screenPos, uint flags, string? workingDir = null)
        {
            nint hMenu = Win32Interop.CreatePopupMenu();
            if (hMenu == nint.Zero) return;

            nint pContextMenu2 = nint.Zero;
            nint pContextMenu3 = nint.Zero;

            try
            {
                Win32Interop.NativeCom.QueryInterface(pContextMenu, in Win32Interop.IID_IContextMenu3, out pContextMenu3);
                if (pContextMenu3 == nint.Zero)
                {
                    Win32Interop.NativeCom.QueryInterface(pContextMenu, in Win32Interop.IID_IContextMenu2, out pContextMenu2);
                }

                int hr = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(pContextMenu, hMenu, 0, CMD_FIRST, CMD_LAST, flags);
                if (hr < 0 && (flags & Win32Interop.CMF_EXTENDEDVERBS) != 0)
                {
                    flags &= ~Win32Interop.CMF_EXTENDEDVERBS;
                    hr = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(pContextMenu, hMenu, 0, CMD_FIRST, CMD_LAST, flags);
                }
                if (hr < 0) return;

                int menuItemCount = Win32Interop.GetMenuItemCount(hMenu);
                if (menuItemCount <= 0) return;

                Win32Interop.POINT pt;
                if (screenPos.HasValue) pt = new Win32Interop.POINT { X = (int)screenPos.Value.X, Y = (int)screenPos.Value.Y };
                else Win32Interop.GetCursorPos(out pt);

                // 親ウィンドウに nint.Zero を指定することで、別スレッドのメッセージキュー同期によるデッドロック（応答なし）を回避
                nint hostHwnd = Win32Interop.CreateWindowExW(
                    0,
                    "STATIC",
                    "FastExplorerMenuHost",
                    0x80000000 /* WS_POPUP */,
                    0, 0, 0, 0,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero);

                nint targetMenuHwnd = (hostHwnd != nint.Zero) ? hostHwnd : hwnd;

                var state = new MenuSubclassState
                {
                    PContextMenu2 = pContextMenu2,
                    PContextMenu3 = pContextMenu3
                };
                bool subclassed = false;

                unsafe
                {
                    if (targetMenuHwnd != nint.Zero && (pContextMenu2 != nint.Zero || pContextMenu3 != nint.Zero))
                    {
                        subclassed = Win32Interop.SetWindowSubclass(targetMenuHwnd, _menuSubclassProc, 1, (nint)(&state));
                    }
                }

                try
                {
                    ApplyDarkThemeToMenu(targetMenuHwnd);

                    if (targetMenuHwnd != nint.Zero)
                    {
                        Win32Interop.SetForegroundWindow(targetMenuHwnd);
                    }

                    uint cmd = 0;
                    try
                    {
                        cmd = Win32Interop.TrackPopupMenuEx(
                            hMenu,
                            Win32Interop.TPM_RETURNCMD | Win32Interop.TPM_RIGHTBUTTON | Win32Interop.TPM_LEFTALIGN,
                            pt.X,
                            pt.Y,
                            targetMenuHwnd,
                            nint.Zero);
                    }
                    finally
                    {
                        if (subclassed && targetMenuHwnd != nint.Zero)
                        {
                            Win32Interop.RemoveWindowSubclass(targetMenuHwnd, _menuSubclassProc, 1);
                            subclassed = false;
                        }

                        if (hostHwnd != nint.Zero)
                        {
                            Win32Interop.PostMessage(hostHwnd, 0, nint.Zero, nint.Zero);
                            Win32Interop.DestroyWindow(hostHwnd);
                        }
                        if (hwnd != nint.Zero)
                        {
                            Win32Interop.SetForegroundWindow(hwnd);
                        }
                    }

                    if (cmd >= CMD_FIRST && cmd <= CMD_LAST)
                    {
                        int structSize = Marshal.SizeOf<Win32Interop.CMINVOKECOMMANDINFOEX>();
                        nint pici = Marshal.AllocHGlobal(structSize);
                        try
                        {
                            var zeroBuffer = new byte[structSize];
                            Marshal.Copy(zeroBuffer, 0, pici, structSize);

                            var invoke = new Win32Interop.CMINVOKECOMMANDINFOEX
                            {
                                cbSize = (uint)structSize,
                                fMask = Win32Interop.CMIC_MASK_UNICODE | Win32Interop.CMIC_MASK_PTINVOKE,
                                hwnd = hwnd,
                                lpVerb = (nint)(cmd - CMD_FIRST),
                                lpVerbW = (nint)(cmd - CMD_FIRST),
                                lpDirectory = workingDir,
                                lpDirectoryW = workingDir,
                                nShow = Win32Interop.SW_SHOWNORMAL,
                                ptInvoke = pt
                            };
                            Marshal.StructureToPtr(invoke, pici, false);
                            Win32Interop.NativeCom.ContextMenu_InvokeCommand(pContextMenu, pici);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pici);
                        }
                    }
                }
                finally
                {
                    if (subclassed && targetMenuHwnd != nint.Zero)
                    {
                        Win32Interop.RemoveWindowSubclass(targetMenuHwnd, _menuSubclassProc, 1);
                    }
                }
            }
            finally
            {
                if (pContextMenu2 != nint.Zero) Win32Interop.NativeCom.Release(pContextMenu2);
                if (pContextMenu3 != nint.Zero) Win32Interop.NativeCom.Release(pContextMenu3);
                Win32Interop.DestroyMenu(hMenu);
            }
        }

        private static bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var val = key.GetValue("AppsUseLightTheme");
                    if (val is int intVal)
                    {
                        return intVal == 0;
                    }
                }
            }
            catch { }
            return true;
        }

        private static void ApplyDarkThemeToMenu(nint hwnd)
        {
            try
            {
                bool isDark = true;
                try
                {
                    var theme = ConfigService.Current.Ui.Theme;
                    if (theme.Equals("light", StringComparison.OrdinalIgnoreCase))
                        isDark = false;
                    else if (theme.Equals("dark", StringComparison.OrdinalIgnoreCase))
                        isDark = true;
                    else
                        isDark = IsSystemDarkTheme();
                }
                catch { }

                Win32Interop.SetPreferredAppMode(isDark ? Win32Interop.PreferredAppMode.ForceDark : Win32Interop.PreferredAppMode.ForceLight);
                if (hwnd != nint.Zero)
                {
                    Win32Interop.AllowDarkModeForWindow(hwnd, isDark);
                    Win32Interop.SetWindowTheme(hwnd, isDark ? "DarkMode_Explorer" : "Explorer", null);
                }
                Win32Interop.FlushMenuThemes();
            }
            catch { }
        }
    }
}
