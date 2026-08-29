#include "FastExplorerWatcher.Common.h"
#include "FastExplorerWatcher.Process.h"
#include "FastExplorerWatcher.Redirect.h"

static bool g_isLWinDown = false;
static bool g_isRWinDown = false;

static LRESULT CALLBACK LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode >= 0)
    {
        KBDLLHOOKSTRUCT* pKey = (KBDLLHOOKSTRUCT*)lParam;
        
        if (pKey->vkCode == VK_LWIN)
        {
            if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) g_isLWinDown = true;
            else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP) g_isLWinDown = false;
        }
        else if (pKey->vkCode == VK_RWIN)
        {
            if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) g_isRWinDown = true;
            else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP) g_isRWinDown = false;
        }
        else if (pKey->vkCode == 'E' || pKey->vkCode == 'e')
        {
            if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
            {
                bool isLWinPhys = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
                bool isRWinPhys = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                bool isLWinSync = (GetKeyState(VK_LWIN) & 0x8000) != 0;
                bool isRWinSync = (GetKeyState(VK_RWIN) & 0x8000) != 0;

                bool isWinDown = g_isLWinDown || g_isRWinDown || (isLWinPhys && isLWinSync) || (isRWinPhys && isRWinSync);

                bool isCtrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool isAltDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool isShiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                if (isWinDown && !isCtrlDown && !isAltDown && !isShiftDown)
                {
                    if (IsDefaultExplorerEnabledInRegistry())
                    {
                        LaunchOrActivateFastExplorer();
                        return 1;
                    }
                }
            }
        }
    }
    return CallNextHookEx(g_hHook, nCode, wParam, lParam);
}

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, PWSTR pCmdLine, int nCmdShow)
{
    g_hMutex = CreateMutexW(NULL, TRUE, MUTEX_NAME);
    if (g_hMutex == NULL || GetLastError() == ERROR_ALREADY_EXISTS)
    {
        if (g_hMutex) CloseHandle(g_hMutex);
        return 0;
    }

    CoInitialize(NULL);

    CleanDisabledHotkeys();

    g_hHook = SetWindowsHookExW(WH_KEYBOARD_LL, LowLevelKeyboardProc, hInstance, 0);

    // 有効なイベント範囲で登録 (OBJECT_CREATE 〜 OBJECT_SHOW)
    g_hWinEventHook = SetWinEventHook(
        EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW,
        NULL,
        WinEventProc,
        0, 0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
    );

    LogWatcher(L"[Main] FastExplorerWatcher started. Hook=%p", g_hWinEventHook);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_hWinEventHook != NULL)
    {
        UnhookWinEvent(g_hWinEventHook);
        g_hWinEventHook = NULL;
    }

    if (g_hHook != NULL)
    {
        UnhookWindowsHookEx(g_hHook);
        g_hHook = NULL;
    }

    CleanDisabledHotkeys();

    if (g_hMutex != NULL)
    {
        ReleaseMutex(g_hMutex);
        CloseHandle(g_hMutex);
        g_hMutex = NULL;
    }

    CoUninitialize();

    LogWatcher(L"[Main] FastExplorerWatcher exiting.");

    return 0;
}
