using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static partial class QuickAccessService
    {
        /// <summary>
        /// フォルダーを Windows Explorer のクイックアクセス / ホームにピン留め (Shell.Application COM + IContextMenu + AppConfig fallback)
        /// </summary>
        public static bool PinFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            bool success = false;
            try
            {
                success |= InvokeShellApplicationReflection(path, isPin: true);
                success |= InvokeShellVerbOnPath(path, "pintohome");
                success |= InvokeShellVerbOnPath(path, "pintofrequent");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuickAccessService] PinFolder error: {ex.Message}");
            }

            try
            {
                var custom = ConfigService.Current.CustomPinnedFolders;
                if (!custom.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    custom.Add(path);
                    ConfigService.Save();
                }
            }
            catch { }

            try
            {
                Win32Interop.SHChangeNotify(0x08000000, 0x1000, nint.Zero, nint.Zero);
            }
            catch { }

            NotifyPinnedChanged();
            return true;
        }

        /// <summary>
        /// フォルダーを Windows Explorer のクイックアクセス / ホームからピン留め解除 (Shell.Application COM + IContextMenu + AppConfig fallback)
        /// </summary>
        public static bool UnpinFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            bool success = false;
            try
            {
                success |= UnpinFromHomeNamespaceReflection(path);
                success |= InvokeShellApplicationReflection(path, isPin: false);
                success |= InvokeShellVerbOnPath(path, "unpinfromhome");
                success |= InvokeShellVerbOnPath(path, "unpinfrequent");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuickAccessService] UnpinFolder error: {ex.Message}");
            }

            try
            {
                var custom = ConfigService.Current.CustomPinnedFolders;
                int removed = custom.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    ConfigService.Save();
                }
            }
            catch { }

            try
            {
                Win32Interop.SHChangeNotify(0x08000000, 0x1000, nint.Zero, nint.Zero);
            }
            catch { }

            NotifyPinnedChanged();
            return true;
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:TargetRequiresUnreferencedCode")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:TargetRequiresUnreferencedCode")]
        private static bool UnpinFromHomeNamespaceReflection(string path)
        {
            string[] namespaces = [
                Win11HomeShellNamespace,
                Win10QuickAccessShellNamespace
            ];

            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;
                object? shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;

                foreach (var ns in namespaces)
                {
                    object? home = null;
                    try
                    {
                        home = shellType.InvokeMember("Namespace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [ns]);
                    }
                    catch { }
                    if (home == null) continue;

                    object? items = null;
                    try
                    {
                        items = home.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, home, null);
                    }
                    catch { }
                    if (items == null) continue;

                    int count = 0;
                    try
                    {
                        count = Convert.ToInt32(items.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null));
                    }
                    catch { }

                    for (int i = 0; i < count; i++)
                    {
                        object? it = null;
                        try
                        {
                            it = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, [i]);
                        }
                        catch { }
                        if (it == null) continue;

                        string itPath = (string)(it.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, it, null) ?? "");
                        if (itPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                        {
                            object? verbs = null;
                            try
                            {
                                verbs = it.GetType().InvokeMember("Verbs", System.Reflection.BindingFlags.InvokeMethod, null, it, null);
                            }
                            catch { }

                            if (verbs != null)
                            {
                                Type verbsType = verbs.GetType();
                                int verbCount = 0;
                                try
                                {
                                    verbCount = Convert.ToInt32(verbsType.InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, verbs, null));
                                }
                                catch { }

                                for (int v = 0; v < verbCount; v++)
                                {
                                    try
                                    {
                                        object? verb = verbsType.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, verbs, [v]);
                                        if (verb == null) continue;
                                        Type verbType = verb.GetType();
                                        string name = (string)(verbType.InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, verb, null) ?? "");
                                        string clean = name.Replace("&", "").Trim().ToLowerInvariant();

                                        if (clean.Contains("外す") || clean.Contains("解除") || clean.Contains("unpin"))
                                        {
                                            verbType.InvokeMember("DoIt", System.Reflection.BindingFlags.InvokeMethod, null, verb, null);
                                            return true;
                                        }
                                    }
                                    catch { }
                                }
                            }

                            // フォールバック: InvokeVerb
                            try { it.GetType().InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, it, ["unpinfromhome"]); } catch { }
                            try { it.GetType().InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, it, ["unpinfrequent"]); } catch { }
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnpinFromHomeNamespaceReflection] Error: {ex.Message}");
            }
            return false;
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:TargetRequiresUnreferencedCode")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:TargetRequiresUnreferencedCode")]
        private static bool InvokeShellApplicationReflection(string path, bool isPin)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;
                object? shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;

                object? folderItem = null;
                Type? folderItemType = null;

                // 1. Namespace(parent).ParseName(fileName) を最優先で試行（シェル項目としての正確なコンテキストメニューを取得）
                try
                {
                    string? parentDir = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                    string fileName = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        object? parentFolder = shellType.InvokeMember("Namespace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [parentDir]);
                        if (parentFolder != null)
                        {
                            folderItem = parentFolder.GetType().InvokeMember("ParseName", System.Reflection.BindingFlags.InvokeMethod, null, parentFolder, [fileName]);
                            folderItemType = folderItem?.GetType();
                        }
                    }
                }
                catch { }

                // 2. フォールバックとして Namespace(path).Self を試行
                if (folderItem == null)
                {
                    try
                    {
                        object? folder = shellType.InvokeMember("Namespace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
                        if (folder != null)
                        {
                            folderItem = folder.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
                            folderItemType = folderItem?.GetType();
                        }
                    }
                    catch { }
                }

                if (folderItem != null && folderItemType != null)
                {
                    // 3. Verbs コレクションから「クイック アクセスにピン留め」を厳密に特定して DoIt() を実行
                    object? verbs = folderItemType.InvokeMember("Verbs", System.Reflection.BindingFlags.InvokeMethod, null, folderItem, null);
                    if (verbs != null)
                    {
                        Type verbsType = verbs.GetType();
                        int count = 0;
                        try
                        {
                            count = Convert.ToInt32(verbsType.InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, verbs, null));
                        }
                        catch { }

                        for (int i = 0; i < count; i++)
                        {
                            try
                            {
                                object? verb = verbsType.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, verbs, [i]);
                                if (verb == null) continue;
                                Type verbType = verb.GetType();
                                string name = (string)(verbType.InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, verb, null) ?? "");
                                string clean = name.Replace("&", "").Trim().ToLowerInvariant();

                                // スタート画面やタスクバーへのピン留めを完全に除外
                                if (clean.Contains("スタート") || clean.Contains("start") || clean.Contains("タスク") || clean.Contains("taskbar"))
                                {
                                    continue;
                                }

                                bool isQuickAccessPin = (clean.Contains("クイック アクセス") || clean.Contains("quick access") || clean.Contains("ホーム") || clean.Contains("home") || clean.Contains("pintohome") || clean.Contains("pintofrequent"))
                                    && (clean.Contains("ピン留め") || clean.Contains("pin"))
                                    && !clean.Contains("外す") && !clean.Contains("解除") && !clean.Contains("unpin");

                                bool isQuickAccessUnpin = (clean.Contains("外す") || clean.Contains("解除") || clean.Contains("unpin"));

                                if (isPin && isQuickAccessPin)
                                {
                                    verbType.InvokeMember("DoIt", System.Reflection.BindingFlags.InvokeMethod, null, verb, null);
                                    return true;
                                }
                                if (!isPin && isQuickAccessUnpin)
                                {
                                    verbType.InvokeMember("DoIt", System.Reflection.BindingFlags.InvokeMethod, null, verb, null);
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }

                    // 4. フォールバックとして InvokeVerb
                    string canonicalVerb = isPin ? "pintohome" : "unpinfromhome";
                    string frequentVerb = isPin ? "pintofrequent" : "unpinfrequent";
                    try { folderItemType.InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, folderItem, [canonicalVerb]); } catch { }
                    try { folderItemType.InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, folderItem, [frequentVerb]); } catch { }
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InvokeShellApplicationReflection] Error: {ex.Message}");
            }
            return false;
        }

        private static bool InvokeShellVerbOnPath(string path, string verb)
        {
            if (!ShellComHelper.TryGetContextMenuForPaths(nint.Zero, [path], out nint pContextMenu, out var pidlsToFree))
            {
                return false;
            }

            try
            {
                nint hMenu = Win32Interop.CreatePopupMenu();
                if (hMenu == nint.Zero) return false;

                try
                {
                    uint flags = Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE | Win32Interop.CMF_EXTENDEDVERBS;
                    int hrQuery = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(pContextMenu, hMenu, 0, 1, 0x7FFF, flags);
                    if (hrQuery < 0)
                    {
                        hrQuery = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(pContextMenu, hMenu, 0, 1, 0x7FFF, Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE);
                    }

                    int itemCount = Win32Interop.GetMenuItemCount(hMenu);
                    bool isPin = verb.StartsWith("pin", StringComparison.OrdinalIgnoreCase);
                    uint? targetCmdOffset = null;

                    // 1. hMenu 内の項目からピン留め / ピン留め解除のコマンドIDを検索
                    for (int i = 0; i < itemCount; i++)
                    {
                        uint cmdId = Win32Interop.GetMenuItemID(hMenu, i);
                        if (cmdId == uint.MaxValue || cmdId < 1) continue;
                        uint offset = cmdId - 1;

                        // Canonical Verb を取得
                        string canonicalVerb = GetCanonicalVerb(pContextMenu, offset);
                        if (!string.IsNullOrEmpty(canonicalVerb))
                        {
                            if (isPin && (canonicalVerb.Equals("pintohome", StringComparison.OrdinalIgnoreCase) || canonicalVerb.Equals("pintofrequent", StringComparison.OrdinalIgnoreCase)))
                            {
                                targetCmdOffset = offset;
                                break;
                            }
                            if (!isPin && (canonicalVerb.Equals("unpinfromhome", StringComparison.OrdinalIgnoreCase) || canonicalVerb.Equals("unpinfrequent", StringComparison.OrdinalIgnoreCase)))
                            {
                                targetCmdOffset = offset;
                                break;
                            }
                        }

                        // メニュー文字列によるフォールバック照合
                        var sb = new StringBuilder(256);
                        Win32Interop.GetMenuString(hMenu, (uint)i, sb, sb.Capacity, Win32Interop.MF_BYPOSITION);
                        string text = sb.ToString().Replace("&", "").Trim().ToLowerInvariant();

                        if (isPin && (text.Contains("ピン留め") || text.Contains("pin to quick access") || text.Contains("pin to home")) && !text.Contains("外す") && !text.Contains("unpin"))
                        {
                            targetCmdOffset = offset;
                            break;
                        }
                        if (!isPin && (text.Contains("ピン留めを外す") || text.Contains("ピン留めの解除") || text.Contains("unpin from quick access") || text.Contains("unpin from home") || text.Contains("unpin")))
                        {
                            targetCmdOffset = offset;
                            break;
                        }
                    }

                    // 2. コマンドを実行
                    if (targetCmdOffset.HasValue)
                    {
                        var ici = new Win32Interop.CMINVOKECOMMANDINFOEX
                        {
                            cbSize = (uint)Marshal.SizeOf<Win32Interop.CMINVOKECOMMANDINFOEX>(),
                            fMask = Win32Interop.CMIC_MASK_FLAG_NO_UI,
                            hwnd = nint.Zero,
                            lpVerb = (nint)targetCmdOffset.Value,
                            lpVerbW = (nint)targetCmdOffset.Value,
                            nShow = 1
                        };

                        nint pIci = Marshal.AllocHGlobal(Marshal.SizeOf(ici));
                        try
                        {
                            Marshal.StructureToPtr(ici, pIci, false);
                            int hr = Win32Interop.NativeCom.ContextMenu_InvokeCommand(pContextMenu, pIci);
                            return hr >= 0;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pIci);
                        }
                    }
                    else
                    {
                        // 3. 文字列 Verb による直接実行
                        nint pVerbAnsi = Marshal.StringToHGlobalAnsi(verb);
                        nint pVerbUni = Marshal.StringToHGlobalUni(verb);
                        try
                        {
                            var ici = new Win32Interop.CMINVOKECOMMANDINFOEX
                            {
                                cbSize = (uint)Marshal.SizeOf<Win32Interop.CMINVOKECOMMANDINFOEX>(),
                                fMask = Win32Interop.CMIC_MASK_UNICODE | Win32Interop.CMIC_MASK_FLAG_NO_UI,
                                hwnd = nint.Zero,
                                lpVerb = pVerbAnsi,
                                lpVerbW = pVerbUni,
                                nShow = 1
                            };

                            nint pIci = Marshal.AllocHGlobal(Marshal.SizeOf(ici));
                            try
                            {
                                Marshal.StructureToPtr(ici, pIci, false);
                                int hr = Win32Interop.NativeCom.ContextMenu_InvokeCommand(pContextMenu, pIci);
                                return hr >= 0;
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(pIci);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pVerbAnsi);
                            Marshal.FreeHGlobal(pVerbUni);
                        }
                    }
                }
                finally
                {
                    Win32Interop.DestroyMenu(hMenu);
                }
            }
            finally
            {
                Win32Interop.NativeCom.Release(pContextMenu);
                ShellComHelper.FreePidls(pidlsToFree);
            }
        }

        private static string GetCanonicalVerb(nint pContextMenu, uint cmdOffset)
        {
            try
            {
                byte[] buf = new byte[256];
                unsafe
                {
                    fixed (byte* pBuf = buf)
                    {
                        // GCS_VERBW = 0x00000004
                        int hr = Win32Interop.NativeCom.ContextMenu_GetCommandString(pContextMenu, (nuint)cmdOffset, 0x00000004, pBuf, (uint)buf.Length / 2);
                        if (hr >= 0)
                        {
                            return Marshal.PtrToStringUni((nint)pBuf) ?? string.Empty;
                        }

                        // GCS_VERBA = 0x00000000
                        hr = Win32Interop.NativeCom.ContextMenu_GetCommandString(pContextMenu, (nuint)cmdOffset, 0x00000000, pBuf, (uint)buf.Length);
                        if (hr >= 0)
                        {
                            return Marshal.PtrToStringAnsi((nint)pBuf) ?? string.Empty;
                        }
                    }
                }
            }
            catch { }
            return string.Empty;
        }
    }
}
