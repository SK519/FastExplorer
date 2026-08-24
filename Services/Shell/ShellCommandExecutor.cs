using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static class ShellCommandExecutor
    {
        private const uint CMD_FIRST = 1;
        private const uint CMD_LAST = 0x7FFF;

        public static string? GetVerbFromContextMenu(nint pContextMenu, uint cmdId)
        {
            if (pContextMenu == nint.Zero || cmdId < CMD_FIRST || cmdId > CMD_LAST) return null;
            try
            {
                byte[] buffer = new byte[512];
                int hr;
                unsafe
                {
                    fixed (byte* pBuffer = buffer)
                    {
                        hr = Win32Interop.NativeCom.ContextMenu_GetCommandString(pContextMenu, cmdId - CMD_FIRST, 0x00000004 /* GCS_VERBW */, pBuffer, (uint)(buffer.Length / 2));
                    }
                }
                if (hr == 0)
                {
                    string v = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            return null;
        }

        public static void InvokeShellCommand(nint hwnd, IReadOnlyList<string> filePaths, ExtractedShellItem item)
        {
            if (filePaths == null || filePaths.Count == 0 || item == null) return;
            var validPaths = filePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (validPaths.Count == 0) return;

            string targetPath = validPaths[0];

            // 1. 既知の直接実行パスがある場合 (登録された外部ツール等の独立プロセス起動)
            if (!string.IsNullOrEmpty(item.DirectLaunchPath))
            {
                try
                {
                    string args = (item.DirectLaunchArgs ?? "{files}").Replace("{files}", string.Join(" ", validPaths.Select(p => $"\"{p}\"")));
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.DirectLaunchPath, args)
                    {
                        WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty,
                        UseShellExecute = true
                    });
                    return;
                }
                catch { }
            }

            // 2. 一般化された COM STA スレッド実行 (すべての OS シェル拡張、アプリ登録コマンド、コンテキストメニュー項目)
            InvokeCommandOnStaThread(hwnd, validPaths, item.CommandId, item.Verb);
        }

        public static void InvokeCommandOnStaThread(nint hwnd, IReadOnlyList<string> filePaths, uint cmdId, string? verb = null)
        {
            if (filePaths == null || filePaths.Count == 0) return;

            var staThread = new System.Threading.Thread(() =>
            {
                Win32Interop.OleInitialize(nint.Zero);
                try
                {
                    var validPaths = filePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                    if (validPaths.Count == 0) return;

                    string? workingDir = null;
                    try
                    {
                        workingDir = Directory.Exists(validPaths[0]) ? validPaths[0] : Path.GetDirectoryName(validPaths[0]);
                    }
                    catch { }

                    if (!ShellComHelper.TryGetContextMenuForPaths(hwnd, validPaths, out var pContextMenu, out var pidlsToFree))
                    {
                        return;
                    }

                    try
                    {
                        nint hMenu = Win32Interop.CreatePopupMenu();
                        if (hMenu != nint.Zero)
                        {
                            try
                            {
                                Win32Interop.NativeCom.ContextMenu_QueryContextMenu(pContextMenu, hMenu, 0, CMD_FIRST, CMD_LAST, Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE | Win32Interop.CMF_EXTENDEDVERBS);
                            }
                            finally
                            {
                                Win32Interop.DestroyMenu(hMenu);
                            }
                        }

                        Win32Interop.GetCursorPos(out var pt);
                        int structSize = Marshal.SizeOf<Win32Interop.CMINVOKECOMMANDINFOEX>();
                        nint pici = Marshal.AllocHGlobal(structSize);
                        nint pVerbAnsi = nint.Zero;
                        nint pVerbUnicode = nint.Zero;

                        try
                        {
                            byte[] zeroBuffer = new byte[structSize];
                            Marshal.Copy(zeroBuffer, 0, pici, structSize);

                            if (!string.IsNullOrEmpty(verb))
                            {
                                pVerbAnsi = Marshal.StringToHGlobalAnsi(verb);
                                pVerbUnicode = Marshal.StringToHGlobalUni(verb);
                            }

                            var invoke = new Win32Interop.CMINVOKECOMMANDINFOEX
                            {
                                cbSize = (uint)structSize,
                                fMask = Win32Interop.CMIC_MASK_UNICODE | Win32Interop.CMIC_MASK_PTINVOKE,
                                hwnd = hwnd,
                                lpVerb = pVerbAnsi != nint.Zero ? pVerbAnsi : (nint)(cmdId - CMD_FIRST),
                                lpVerbW = pVerbUnicode != nint.Zero ? pVerbUnicode : (nint)(cmdId - CMD_FIRST),
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
                            if (pVerbAnsi != nint.Zero) Marshal.FreeHGlobal(pVerbAnsi);
                            if (pVerbUnicode != nint.Zero) Marshal.FreeHGlobal(pVerbUnicode);
                            Marshal.FreeHGlobal(pici);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InvokeShell] Error: {ex.Message}");
                    }
                    finally
                    {
                        if (pContextMenu != nint.Zero)
                        {
                            Win32Interop.NativeCom.Release(pContextMenu);
                        }
                        ShellComHelper.FreePidls(pidlsToFree);
                    }
                }
                finally
                {
                    Win32Interop.OleUninitialize();
                }
            });
            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.Start();
        }
    }
}
