#pragma once

#include "FastExplorerWatcher.Common.h"
#include "FastExplorerWatcher.Process.h"

struct ExplorerRedirectContext
{
    HWND hwnd;
    DWORD pid;
    RECT origRect;
    LONG_PTR origExStyle;
};

// バックグラウンドでコマンドラインまたは IShellWindows からフォルダパスを解決して FastExplorer に転送 (超高速・低遅延最適化)
static DWORD WINAPI ExplorerRedirectThread(LPVOID lpParam)
{
    ExplorerRedirectContext* ctx = (ExplorerRedirectContext*)lpParam;
    if (!ctx) return 0;

    HWND hwnd = ctx->hwnd;
    DWORD pid = ctx->pid;
    RECT origRect = ctx->origRect;
    LONG_PTR origExStyle = ctx->origExStyle;
    delete ctx;

    std::wstring targetPath = L"";

    // 1. プロセスのコマンドライン引数を直接高速取得 (0ミリ秒解決)
    std::wstring rawCmdLine;
    if (GetProcessCommandLine(pid, rawCmdLine))
    {
        std::wstring args = ExtractArgsFromCommandLine(rawCmdLine);
        if (!args.empty())
        {
            targetPath = args;
        }
    }

    // 2. コマンドラインから取れなかった場合のみ、IShellWindows で高速走査 (10ms 周期、最大500ms)
    if (targetPath.empty())
    {
        CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);

        for (int retry = 0; retry < 50; retry++)
        {
            if (!IsWindow(hwnd)) break;

            IShellWindows* pSW = NULL;
            if (SUCCEEDED(CoCreateInstance(CLSID_ShellWindows, NULL, CLSCTX_ALL, IID_IShellWindows, (void**)&pSW)) && pSW != NULL)
            {
                long count = 0;
                pSW->get_Count(&count);
                for (long i = 0; i < count; i++)
                {
                    VARIANT v;
                    VariantInit(&v);
                    v.vt = VT_I4;
                    v.lVal = i;
                    IDispatch* pDisp = NULL;
                    if (SUCCEEDED(pSW->Item(v, &pDisp)) && pDisp != NULL)
                    {
                        IWebBrowserApp* pBrowser = NULL;
                        if (SUCCEEDED(pDisp->QueryInterface(IID_IWebBrowserApp, (void**)&pBrowser)) && pBrowser != NULL)
                        {
                            SHANDLE_PTR sHwnd = 0;
                            if (SUCCEEDED(pBrowser->get_HWND(&sHwnd)))
                            {
                                HWND itemHwnd = (HWND)sHwnd;
                                if (itemHwnd == hwnd || GetAncestor(itemHwnd, GA_ROOT) == hwnd || GetAncestor(hwnd, GA_ROOT) == itemHwnd)
                                {
                                    // 2-1. /select による選択アイテムの取得を試みる
                                    IDispatch* pDoc = NULL;
                                    if (SUCCEEDED(pBrowser->get_Document(&pDoc)) && pDoc != NULL)
                                    {
                                        IShellFolderViewDual* pFolderView = NULL;
                                        if (SUCCEEDED(pDoc->QueryInterface(IID_IShellFolderViewDual, (void**)&pFolderView)) && pFolderView != NULL)
                                        {
                                            FolderItems* pSelected = NULL;
                                            if (SUCCEEDED(pFolderView->SelectedItems(&pSelected)) && pSelected != NULL)
                                            {
                                                long selCount = 0;
                                                pSelected->get_Count(&selCount);
                                                if (selCount > 0)
                                                {
                                                    VARIANT vIdx;
                                                    VariantInit(&vIdx);
                                                    vIdx.vt = VT_I4;
                                                    vIdx.lVal = 0;
                                                    FolderItem* pItem = NULL;
                                                    if (SUCCEEDED(pSelected->Item(vIdx, &pItem)) && pItem != NULL)
                                                    {
                                                        BSTR bstrItemPath = NULL;
                                                        if (SUCCEEDED(pItem->get_Path(&bstrItemPath)) && bstrItemPath != NULL)
                                                        {
                                                            if (bstrItemPath[0] != L'\0')
                                                            {
                                                                targetPath = L"/select,\"" + std::wstring(bstrItemPath) + L"\"";
                                                            }
                                                            SysFreeString(bstrItemPath);
                                                        }
                                                        pItem->Release();
                                                    }
                                                }
                                                pSelected->Release();
                                            }
                                            pFolderView->Release();
                                        }
                                        pDoc->Release();
                                    }

                                    // 2-2. 選択アイテムがない場合はフォルダー URL を取得
                                    if (targetPath.empty())
                                    {
                                        BSTR bstrUrl = NULL;
                                        if (SUCCEEDED(pBrowser->get_LocationURL(&bstrUrl)) && bstrUrl != NULL)
                                        {
                                            if (bstrUrl[0] != L'\0')
                                            {
                                                wchar_t path[2084] = { 0 };
                                                DWORD pathLen = 2084;
                                                if (SUCCEEDED(PathCreateFromUrlW(bstrUrl, path, &pathLen, 0)) && path[0] != L'\0')
                                                {
                                                    targetPath = path;
                                                }
                                                else if (wcsncmp(bstrUrl, L"file://", 7) == 0 || wcsncmp(bstrUrl, L"shell:", 6) == 0)
                                                {
                                                    targetPath = bstrUrl;
                                                }
                                            }
                                            SysFreeString(bstrUrl);
                                        }
                                    }
                                }
                            }
                            pBrowser->Release();
                        }
                        pDisp->Release();
                    }

                    if (!targetPath.empty()) break;
                }
                pSW->Release();
            }

            if (!targetPath.empty()) break;
            Sleep(10);
        }

        CoUninitialize();
    }

    if (targetPath.empty())
    {
        // タイムアウト等でパス解決ができなかった場合はエクスプローラーを破棄せず、元の状態に復元
        if (IsWindow(hwnd))
        {
            RemovePropW(hwnd, HOOK_PROP_NAME);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, origExStyle);
            int width = origRect.right - origRect.left;
            int height = origRect.bottom - origRect.top;
            if (width > 100 && height > 100)
            {
                SetWindowPos(hwnd, HWND_TOP, origRect.left, origRect.top, width, height, SWP_SHOWWINDOW);
            }
            else
            {
                SetWindowPos(hwnd, HWND_TOP, 100, 100, 1000, 700, SWP_SHOWWINDOW);
            }
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        LogWatcher(L"[Redirect] Fallback: targetPath could not be resolved, restored Explorer window.");
        return 0;
    }

    if (IsWindow(hwnd))
    {
        ShowWindow(hwnd, SW_HIDE);
        RemovePropW(hwnd, HOOK_PROP_NAME);
        PostMessageW(hwnd, WM_CLOSE, 0, 0);
    }

    LaunchOrActivateFastExplorer(targetPath.c_str());

    return 0;
}

// Explorer (CabinetWClass) ウィンドウを捕捉し、画面表示前に即座に非表示（完全透明化）にして FastExplorer にリダイレクト
static void CALLBACK WinEventProc(
    HWINEVENTHOOK hWinEventHook,
    DWORD event,
    HWND hwnd,
    LONG idObject,
    LONG idChild,
    DWORD idEventThread,
    DWORD dwmsEventTime)
{
    if (hwnd == NULL || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
    if (event != EVENT_OBJECT_CREATE && event != EVENT_OBJECT_SHOW) return;

    wchar_t className[256] = { 0 };
    if (GetClassNameW(hwnd, className, 256) <= 0) return;

    // CabinetWClass (通常のエクスプローラー) および ExploreWClass 以外は絶対に介入しない (タスクバーやメニューへの誤爆を防止)
    if (_wcsicmp(className, L"CabinetWClass") != 0 && _wcsicmp(className, L"ExploreWClass") != 0)
    {
        return;
    }

    if (!IsDefaultExplorerEnabledInRegistry()) return;

    if (GetPropW(hwnd, HOOK_PROP_NAME) != NULL) return;
    SetPropW(hwnd, HOOK_PROP_NAME, (HANDLE)1);

    RECT origRect = { 0 };
    GetWindowRect(hwnd, &origRect);
    LONG_PTR origExStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);

    // チラつき・描画枠・タスクバーアイコンを完全に防止するため即座に画面外移動 & 完全透明化 & 非表示化
    SetWindowPos(hwnd, NULL, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE | SWP_HIDEWINDOW);
    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, origExStyle | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
    SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
    ShowWindow(hwnd, SW_HIDE);

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);

    LogWatcher(L"[WinEvent] Intercepted %s HWND=0x%p PID=%d event=0x%X", className, hwnd, pid, event);

    ExplorerRedirectContext* ctx = new ExplorerRedirectContext();
    ctx->hwnd = hwnd;
    ctx->pid = pid;
    ctx->origRect = origRect;
    ctx->origExStyle = origExStyle;

    HANDLE hThread = CreateThread(NULL, 0, ExplorerRedirectThread, ctx, 0, NULL);
    if (hThread != NULL)
    {
        CloseHandle(hThread);
    }
    else
    {
        delete ctx;
    }
}
