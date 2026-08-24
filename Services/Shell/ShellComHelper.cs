using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    internal static class ShellComHelper
    {
        public static bool TryGetContextMenuForPaths(
            nint hwnd,
            IReadOnlyList<string> validPaths,
            out nint pContextMenu,
            out List<nint> pidlsToFree)
        {
            return TryGetContextMenuForPaths(hwnd, validPaths, out pContextMenu, out _, out pidlsToFree);
        }

        public static bool TryGetContextMenuForFolderBackground(
            nint hwnd,
            string folderPath,
            out nint pContextMenu,
            out List<nint> pidlsToFree)
        {
            return TryGetContextMenuForFolderBackground(hwnd, folderPath, out pContextMenu, out _, out pidlsToFree);
        }

        public static bool TryGetContextMenuForPaths(
            nint hwnd,
            IReadOnlyList<string> validPaths,
            out nint pContextMenu,
            out List<nint> comObjectsToRelease,
            out List<nint> pidlsToFree)
        {
            pContextMenu = nint.Zero;
            comObjectsToRelease = new List<nint>();
            pidlsToFree = new List<nint>();

            if (validPaths == null || validPaths.Count == 0) return false;

            try
            {
                if (validPaths.Count == 1)
                {
                    string singlePath = (validPaths[0].Length > 3) ? validPaths[0].TrimEnd('\\', '/') : validPaths[0];

                    // 1. IShellItem::BindToHandler (BHID_SFUIObject, IID_IContextMenu) - ネットワーク/UNC/ローカルに最も安全
                    int hrItem = Win32Interop.SHCreateItemFromParsingName(singlePath, nint.Zero, in Win32Interop.IID_IShellItem, out nint pShellItem);
                    if (hrItem == 0 && pShellItem != nint.Zero)
                    {
                        comObjectsToRelease.Add(pShellItem);
                        hrItem = Win32Interop.NativeCom.ShellItem_BindToHandler(pShellItem, nint.Zero, in Win32Interop.BHID_SFUIObject, in Win32Interop.IID_IContextMenu, out pContextMenu);
                        if (hrItem == 0 && pContextMenu != nint.Zero)
                        {
                            return true;
                        }
                    }

                    // 2. フォールバック: PIDL + SHBindToParent
                    int hr = Win32Interop.SHParseDisplayName(singlePath, nint.Zero, out nint pidl, 0, out _);
                    if (hr == 0 && pidl != nint.Zero)
                    {
                        pidlsToFree.Add(pidl);

                        hr = Win32Interop.SHBindToParent(pidl, in Win32Interop.IID_IShellFolder, out nint parentFolderPtr, out nint relPidl);
                        if (hr == 0 && parentFolderPtr != nint.Zero && relPidl != nint.Zero)
                        {
                            comObjectsToRelease.Add(parentFolderPtr);
                            unsafe
                            {
                                nint* pRelPidl = &relPidl;
                                hr = Win32Interop.NativeCom.ShellFolder_GetUIObjectOf(parentFolderPtr, hwnd, 1, pRelPidl, in Win32Interop.IID_IContextMenu, out pContextMenu);
                                if (hr == 0 && pContextMenu != nint.Zero)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    nint parentFolderPtr = nint.Zero;
                    var relPidls = new List<nint>();

                    for (int i = 0; i < validPaths.Count; i++)
                    {
                        string path = (validPaths[i].Length > 3) ? validPaths[i].TrimEnd('\\', '/') : validPaths[i];
                        int hr = Win32Interop.SHParseDisplayName(path, nint.Zero, out nint pidl, 0, out _);
                        if (hr == 0 && pidl != nint.Zero)
                        {
                            pidlsToFree.Add(pidl);
                            if (parentFolderPtr == nint.Zero)
                            {
                                hr = Win32Interop.SHBindToParent(pidl, in Win32Interop.IID_IShellFolder, out parentFolderPtr, out nint relPidl);
                                if (hr == 0 && parentFolderPtr != nint.Zero && relPidl != nint.Zero)
                                {
                                    comObjectsToRelease.Add(parentFolderPtr);
                                    relPidls.Add(relPidl);
                                }
                            }
                            else
                            {
                                nint childPidl = Win32Interop.ILFindLastID(pidl);
                                if (childPidl != nint.Zero)
                                {
                                    relPidls.Add(childPidl);
                                }
                            }
                        }
                    }

                    if (parentFolderPtr != nint.Zero && relPidls.Count > 0)
                    {
                        var relPidlsArr = relPidls.ToArray();
                        unsafe
                        {
                            fixed (nint* pRelPidls = relPidlsArr)
                            {
                                int hr = Win32Interop.NativeCom.ShellFolder_GetUIObjectOf(parentFolderPtr, hwnd, (uint)relPidlsArr.Length, pRelPidls, in Win32Interop.IID_IContextMenu, out pContextMenu);
                                if (hr == 0 && pContextMenu != nint.Zero)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ShellCom] TryGetContextMenuForPaths Exception: {ex}");
                FreePidls(pidlsToFree);
                pidlsToFree.Clear();
                ReleaseComObjects(comObjectsToRelease);
                comObjectsToRelease.Clear();
                if (pContextMenu != nint.Zero)
                {
                    Win32Interop.NativeCom.Release(pContextMenu);
                    pContextMenu = nint.Zero;
                }
                return false;
            }
        }

        public static bool TryGetContextMenuForFolderBackground(
            nint hwnd,
            string folderPath,
            out nint pContextMenu,
            out List<nint> comObjectsToRelease,
            out List<nint> pidlsToFree)
        {
            pContextMenu = nint.Zero;
            comObjectsToRelease = new List<nint>();
            pidlsToFree = new List<nint>();

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return false;

            try
            {
                string normPath = (folderPath.Length > 3) ? folderPath.TrimEnd('\\', '/') : folderPath;
                int hr = Win32Interop.SHParseDisplayName(normPath, nint.Zero, out nint folderPidl, 0, out _);
                if (hr != 0 || folderPidl == nint.Zero) return false;
                pidlsToFree.Add(folderPidl);

                // SHBindToObject を使用してフォルダーの IShellFolder を安全に取得
                hr = Win32Interop.SHBindToObject(nint.Zero, folderPidl, nint.Zero, in Win32Interop.IID_IShellFolder, out nint folderPtr);
                if (hr != 0 || folderPtr == nint.Zero)
                {
                    // IShellItem 経由での取得を試行
                    int hrItem = Win32Interop.SHCreateItemFromParsingName(normPath, nint.Zero, in Win32Interop.IID_IShellItem, out nint pShellItem);
                    if (hrItem == 0 && pShellItem != nint.Zero)
                    {
                        comObjectsToRelease.Add(pShellItem);
                        Win32Interop.NativeCom.ShellItem_BindToHandler(pShellItem, nint.Zero, in Win32Interop.BHID_SFObject, in Win32Interop.IID_IShellFolder, out folderPtr);
                    }
                }

                if (folderPtr == nint.Zero) return false;
                comObjectsToRelease.Add(folderPtr);

                // 1. SHCreateDefaultContextMenu (Windows 標準のフォルダー背景メニュー API) を試行
                var dcm = new Win32Interop.DEFCONTEXTMENU
                {
                    hwnd = hwnd,
                    pcmcb = nint.Zero,
                    pidlFolder = folderPidl,
                    psf = folderPtr,
                    cidl = 0,
                    apidl = nint.Zero,
                    punkAssociationInfo = nint.Zero,
                    cKeys = 0,
                    aKeys = nint.Zero
                };

                hr = Win32Interop.SHCreateDefaultContextMenu(ref dcm, in Win32Interop.IID_IContextMenu, out pContextMenu);
                if (hr == 0 && pContextMenu != nint.Zero)
                {
                    return true;
                }

                // 2. IShellFolder::CreateViewObject を試行
                hr = Win32Interop.NativeCom.ShellFolder_CreateViewObject(folderPtr, hwnd, in Win32Interop.IID_IContextMenu, out pContextMenu);
                if (hr == 0 && pContextMenu != nint.Zero)
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ShellCom] Background Exception: {ex}");
                FreePidls(pidlsToFree);
                pidlsToFree.Clear();
                ReleaseComObjects(comObjectsToRelease);
                comObjectsToRelease.Clear();
                if (pContextMenu != nint.Zero)
                {
                    Win32Interop.NativeCom.Release(pContextMenu);
                    pContextMenu = nint.Zero;
                }
                return false;
            }
        }

        public static void FreePidls(IEnumerable<nint> pidls)
        {
            if (pidls == null) return;
            foreach (var pidl in pidls)
            {
                if (pidl != nint.Zero)
                {
                    Win32Interop.ILFree(pidl);
                }
            }
        }

        public static void ReleaseComObjects(IEnumerable<nint> comObjects)
        {
            if (comObjects == null) return;
            foreach (var obj in comObjects)
            {
                if (obj != nint.Zero)
                {
                    Win32Interop.NativeCom.Release(obj);
                }
            }
        }
    }
}
