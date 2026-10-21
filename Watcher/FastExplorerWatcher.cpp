#include "FastExplorerWatcher.Common.h"
#include "FastExplorerWatcher.Process.h"
#include "FastExplorerWatcher.Redirect.h"

static bool g_isLWinDown = false;
static bool g_isRWinDown = false;
static bool g_isResettingCapsLock = false;

static void EnsureCapsLockOff()
{
    // CapsLock状態 (最下位ビットが1ならON)
    if ((GetKeyState(VK_CAPITAL) & 0x0001) != 0)
    {
        g_isResettingCapsLock = true;

        INPUT inputs[2] = { 0 };
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_CAPITAL;
        inputs[0].ki.wScan = 0x3A;
        inputs[0].ki.dwFlags = 0;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_CAPITAL;
        inputs[1].ki.wScan = 0x3A;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(2, inputs, sizeof(INPUT));

        g_isResettingCapsLock = false;
    }
}

static VOID CALLBACK CapsLockWatchTimerProc(HWND hwnd, UINT uMsg, UINT_PTR idEvent, DWORD dwTime)
{
    if (IsCapsLockBlockingEnabledInRegistry())
    {
        EnsureCapsLockOff();
    }
}

static LRESULT CALLBACK LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode >= 0)
    {
        KBDLLHOOKSTRUCT* pKey = (KBDLLHOOKSTRUCT*)lParam;

        if (g_isResettingCapsLock)
        {
            return CallNextHookEx(g_hHook, nCode, wParam, lParam);
        }
        
        // 1. CapsLock キーの無効化 (設定で有効時のみ)
        // 日本語 JIS 配列の単体押し (VK_OEM_ATTN / 0xF0)、
        // 英語配列や Shift+CapsLock (VK_CAPITAL / 0x14)、
        // 物理スキャンコード (0x3A) のいずれにも対応
        bool isCapsLockKey = (pKey->vkCode == VK_CAPITAL) ||
                             (pKey->vkCode == VK_OEM_ATTN) ||
                             (pKey->scanCode == 0x3A && !(pKey->flags & LLKHF_EXTENDED));

        if (isCapsLockKey)
        {
            if (IsCapsLockBlockingEnabledInRegistry())
            {
                EnsureCapsLockOff();
                return 1;
            }
        }

        // 2. Copilot キー (Win + Shift + F23) の無効化 (設定で有効時のみ)
        if (pKey->vkCode == VK_F23)
        {
            if (IsCopilotBlockingEnabledInRegistry())
            {
                // Winキー単体押し判定によるスタートメニュー誤爆を防ぐためダミーキーを送信
                if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
                {
                    INPUT dummyInput = { 0 };
                    dummyInput.type = INPUT_KEYBOARD;
                    dummyInput.ki.wVk = 0xFF;
                    SendInput(1, &dummyInput, sizeof(INPUT));
                }
                return 1;
            }
        }

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
                if (!isLWinPhys) g_isLWinDown = false;
                if (!isRWinPhys) g_isRWinDown = false;

                bool isWinDown = (g_isLWinDown && isLWinPhys) || (g_isRWinDown && isRWinPhys) || isLWinPhys || isRWinPhys;

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

    // CapsLock無効化が有効かつCapsLockがON状態なら初期化時に解除
    if (IsCapsLockBlockingEnabledInRegistry())
    {
        EnsureCapsLockOff();
    }

    g_hHook = SetWindowsHookExW(WH_KEYBOARD_LL, LowLevelKeyboardProc, hInstance, 0);

    // 有効なイベント範囲で登録 (OBJECT_CREATE 〜 OBJECT_SHOW)
    g_hWinEventHook = SetWinEventHook(
        EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW,
        NULL,
        WinEventProc,
        0, 0,
        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
    );

    // CapsLock状態の定期監視タイマー (1秒間隔)
    UINT_PTR capsTimerId = SetTimer(NULL, 0, 1000, CapsLockWatchTimerProc);

    LogWatcher(L"[Main] FastExplorerWatcher started. Hook=%p", g_hWinEventHook);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (capsTimerId != 0)
    {
        KillTimer(NULL, capsTimerId);
        capsTimerId = 0;
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
