using System;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region Native COM Direct VTable Invoker (AOT / CoreCLR / Release Safe)

        public static unsafe class NativeCom
        {
            public static int Release(nint pUnk)
            {
                if (pUnk == nint.Zero) return 0;
                var vtbl = *(nint**)pUnk;
                var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2];
                return (int)func(pUnk);
            }

            public static int QueryInterface(nint pUnk, in Guid riid, out nint ppv)
            {
                ppv = nint.Zero;
                if (pUnk == nint.Zero) return unchecked((int)0x80004003);
                fixed (Guid* pRiid = &riid)
                {
                    fixed (nint* pPpv = &ppv)
                    {
                        var vtbl = *(nint**)pUnk;
                        var func = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[0];
                        return func(pUnk, pRiid, pPpv);
                    }
                }
            }

            public static int ShellFolder_GetUIObjectOf(nint pFolder, nint hwndOwner, uint cidl, nint* apidl, in Guid riid, out nint ppv)
            {
                ppv = nint.Zero;
                if (pFolder == nint.Zero) return unchecked((int)0x80004003);
                fixed (Guid* pRiid = &riid)
                {
                    fixed (nint* pPpv = &ppv)
                    {
                        var vtbl = *(nint**)pFolder;
                        var func = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, Guid*, nint, nint*, int>)vtbl[10];
                        return func(pFolder, hwndOwner, cidl, apidl, pRiid, nint.Zero, pPpv);
                    }
                }
            }

            public static int ShellFolder_CreateViewObject(nint pFolder, nint hwndOwner, in Guid riid, out nint ppv)
            {
                ppv = nint.Zero;
                if (pFolder == nint.Zero) return unchecked((int)0x80004003);
                fixed (Guid* pRiid = &riid)
                {
                    fixed (nint* pPpv = &ppv)
                    {
                        var vtbl = *(nint**)pFolder;
                        var func = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[8];
                        return func(pFolder, hwndOwner, pRiid, pPpv);
                    }
                }
            }

            public static int ShellFolder_GetAttributesOf(nint pFolder, uint cidl, nint* apidl, ref uint rgfInOut)
            {
                if (pFolder == nint.Zero) return unchecked((int)0x80004003);
                fixed (uint* pFlags = &rgfInOut)
                {
                    var vtbl = *(nint**)pFolder;
                    var func = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)vtbl[9];
                    return func(pFolder, cidl, apidl, pFlags);
                }
            }

            public static int ShellItem_BindToHandler(nint pItem, nint pbc, in Guid bhid, in Guid riid, out nint ppv)
            {
                ppv = nint.Zero;
                if (pItem == nint.Zero) return unchecked((int)0x80004003);
                fixed (Guid* pBhid = &bhid)
                {
                    fixed (Guid* pRiid = &riid)
                    {
                        fixed (nint* pPpv = &ppv)
                        {
                            var vtbl = *(nint**)pItem;
                            var func = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)vtbl[3];
                            return func(pItem, pbc, pBhid, pRiid, pPpv);
                        }
                    }
                }
            }

            public static int ContextMenu_QueryContextMenu(nint pMenu, nint hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags)
            {
                if (pMenu == nint.Zero) return unchecked((int)0x80004003);
                var vtbl = *(nint**)pMenu;
                var func = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, int>)vtbl[3];
                return func(pMenu, hMenu, indexMenu, idCmdFirst, idCmdLast, uFlags);
            }

            public static int ContextMenu_InvokeCommand(nint pMenu, nint pici)
            {
                if (pMenu == nint.Zero) return unchecked((int)0x80004003);
                var vtbl = *(nint**)pMenu;
                var func = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[4];
                return func(pMenu, pici);
            }

            public static int ContextMenu_GetCommandString(nint pMenu, nuint idCmd, uint uFlags, byte* buffer, uint cch)
            {
                if (pMenu == nint.Zero) return unchecked((int)0x80004003);
                var vtbl = *(nint**)pMenu;
                var func = (delegate* unmanaged[Stdcall]<nint, nuint, uint, nint, byte*, uint, int>)vtbl[5];
                return func(pMenu, idCmd, uFlags, nint.Zero, buffer, cch);
            }

            public static int ContextMenu2_HandleMenuMsg(nint pMenu2, uint uMsg, nint wParam, nint lParam)
            {
                if (pMenu2 == nint.Zero) return unchecked((int)0x80004003);
                var vtbl = *(nint**)pMenu2;
                var func = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)vtbl[6];
                return func(pMenu2, uMsg, wParam, lParam);
            }

            public static int ContextMenu3_HandleMenuMsg2(nint pMenu3, uint uMsg, nint wParam, nint lParam, out nint lResult)
            {
                lResult = nint.Zero;
                if (pMenu3 == nint.Zero) return unchecked((int)0x80004003);
                fixed (nint* pRes = &lResult)
                {
                    var vtbl = *(nint**)pMenu3;
                    var func = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint*, int>)vtbl[7];
                    return func(pMenu3, uMsg, wParam, lParam, pRes);
                }
            }

            public static int ShellFolder_EnumObjects(nint pFolder, nint hwnd, uint grfFlags, out nint ppenumIDList)
            {
                ppenumIDList = nint.Zero;
                if (pFolder == nint.Zero) return unchecked((int)0x80004003);
                fixed (nint* pEnum = &ppenumIDList)
                {
                    var vtbl = *(nint**)pFolder;
                    var func = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)vtbl[4];
                    return func(pFolder, hwnd, grfFlags, pEnum);
                }
            }

            public static int ShellFolder_BindToObject(nint pFolder, nint pidl, in Guid riid, out nint ppv)
            {
                ppv = nint.Zero;
                if (pFolder == nint.Zero) return unchecked((int)0x80004003);
                fixed (Guid* pRiid = &riid)
                {
                    fixed (nint* pPpv = &ppv)
                    {
                        var vtbl = *(nint**)pFolder;
                        var func = (delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)vtbl[5];
                        return func(pFolder, pidl, nint.Zero, pRiid, pPpv);
                    }
                }
            }

            public static int ShellFolder_GetDisplayNameOf(nint pFolder, nint pidl, uint uFlags, out STRRET pName)
            {
                pName = default;
                if (pFolder == nint.Zero) return unchecked((int)0x80004003);
                fixed (STRRET* pStr = &pName)
                {
                    var vtbl = *(nint**)pFolder;
                    var func = (delegate* unmanaged[Stdcall]<nint, nint, uint, STRRET*, int>)vtbl[11];
                    return func(pFolder, pidl, uFlags, pStr);
                }
            }

            public static int EnumIDList_Next(nint pEnum, uint celt, out nint rgelt, out uint pceltFetched)
            {
                rgelt = nint.Zero;
                pceltFetched = 0;
                if (pEnum == nint.Zero) return unchecked((int)0x80004003);
                fixed (nint* pRgelt = &rgelt)
                {
                    fixed (uint* pFetched = &pceltFetched)
                    {
                        var vtbl = *(nint**)pEnum;
                        var func = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)vtbl[3];
                        return func(pEnum, celt, pRgelt, pFetched);
                    }
                }
            }

            public static int EnumAssocHandlers_Next(nint pEnum, uint celt, out nint pHandler, out uint pceltFetched)
            {
                pHandler = nint.Zero;
                pceltFetched = 0;
                if (pEnum == nint.Zero) return unchecked((int)0x80004003);
                fixed (nint* pOutHandler = &pHandler)
                {
                    fixed (uint* pFetched = &pceltFetched)
                    {
                        var vtbl = *(nint**)pEnum;
                        var func = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)vtbl[3];
                        return func(pEnum, celt, pOutHandler, pFetched);
                    }
                }
            }

            public static int AssocHandler_GetName(nint pHandler, out string name)
            {
                name = "";
                if (pHandler == nint.Zero) return unchecked((int)0x80004003);
                nint pStr = nint.Zero;
                var vtbl = *(nint**)pHandler;
                var func = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[3];
                int hr = func(pHandler, &pStr);
                if (hr == 0 && pStr != nint.Zero)
                {
                    try { name = Marshal.PtrToStringUni(pStr) ?? ""; }
                    finally { Marshal.FreeCoTaskMem(pStr); }
                }
                return hr;
            }

            public static int AssocHandler_GetUIName(nint pHandler, out string uiName)
            {
                uiName = "";
                if (pHandler == nint.Zero) return unchecked((int)0x80004003);
                nint pStr = nint.Zero;
                var vtbl = *(nint**)pHandler;
                var func = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[4];
                int hr = func(pHandler, &pStr);
                if (hr == 0 && pStr != nint.Zero)
                {
                    try { uiName = Marshal.PtrToStringUni(pStr) ?? ""; }
                    finally { Marshal.FreeCoTaskMem(pStr); }
                }
                return hr;
            }

            public static int AssocHandler_GetIconLocation(nint pHandler, out string iconPath, out int iconIndex)
            {
                iconPath = "";
                iconIndex = 0;
                if (pHandler == nint.Zero) return unchecked((int)0x80004003);
                nint pStr = nint.Zero;
                int idx = 0;
                var vtbl = *(nint**)pHandler;
                var func = (delegate* unmanaged[Stdcall]<nint, nint*, int*, int>)vtbl[5];
                int hr = func(pHandler, &pStr, &idx);
                if (hr == 0)
                {
                    iconIndex = idx;
                    if (pStr != nint.Zero)
                    {
                        try { iconPath = Marshal.PtrToStringUni(pStr) ?? ""; }
                        finally { Marshal.FreeCoTaskMem(pStr); }
                    }
                }
                return hr;
            }

            public static int AssocHandler_IsRecommended(nint pHandler)
            {
                if (pHandler == nint.Zero) return unchecked((int)0x80004003);
                var vtbl = *(nint**)pHandler;
                var func = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[6];
                return func(pHandler);
            }

            public static int AssocHandler_Invoke(nint pHandler, nint pDataObject)
            {
                if (pHandler == nint.Zero) return unchecked((int)0x80004003);
                var vtbl = *(nint**)pHandler;
                var func = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtbl[8];
                return func(pHandler, pDataObject);
            }
        }

        #endregion
    }
}
