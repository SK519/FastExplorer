#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <psapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <string>

static HHOOK g_hHook = NULL;
static HANDLE g_hMutex = NULL;
static const wchar_t* MUTEX_NAME = L"FastExplorerWatcherMutex_Global";

static void SetDisabledHotkeys(bool disable)
{
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", 0, KEY_READ | KEY_WRITE, &hKey) == ERROR_SUCCESS)
    {
        wchar_t buffer[256] = { 0 };
        DWORD type = REG_SZ;
        DWORD size = sizeof(buffer);
        RegQueryValueExW(hKey, L"DisabledHotkeys", NULL, &type, (LPBYTE)buffer, &size);
        std::wstring val(buffer);

        if (disable)
        {
            if (val.find(L'E') == std::wstring::npos && val.find(L'e') == std::wstring::npos)
            {
                val += L"E";
                RegSetValueExW(hKey, L"DisabledHotkeys", 0, REG_SZ, (const BYTE*)val.c_str(), (DWORD)((val.length() + 1) * sizeof(wchar_t)));
            }
        }
        else
        {
            size_t pos;
            while ((pos = val.find(L'E')) != std::wstring::npos) val.erase(pos, 1);
            while ((pos = val.find(L'e')) != std::wstring::npos) val.erase(pos, 1);

            if (val.empty())
            {
                RegDeleteValueW(hKey, L"DisabledHotkeys");
            }
            else
            {
                RegSetValueExW(hKey, L"DisabledHotkeys", 0, REG_SZ, (const BYTE*)val.c_str(), (DWORD)((val.length() + 1) * sizeof(wchar_t)));
            }
        }
        RegCloseKey(hKey);
    }
}

// 実行ファイル名が FastExplorer.exe であり、かつ WinUI 3 メインウィンドウであるものを厳格に検索
static BOOL CALLBACK EnumWindowsProc(HWND hWnd, LPARAM lParam)
{
    if (!IsWindowVisible(hWnd)) return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hWnd, &pid);
    if (pid == 0) return TRUE;

    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (hProc != NULL)
    {
        wchar_t exePath[MAX_PATH] = { 0 };
        DWORD size = MAX_PATH;
        if (QueryFullProcessImageNameW(hProc, 0, exePath, &size))
        {
            wchar_t* fileName = wcsrchr(exePath, L'\\');
            if (fileName != NULL)
            {
                fileName++;
                if (_wcsicmp(fileName, L"FastExplorer.exe") == 0)
                {
                    wchar_t className[256] = { 0 };
                    GetClassNameW(hWnd, className, 256);
                    if (wcscmp(className, L"WinUIDesktopWin32WindowClass") == 0)
                    {
                        HWND* pResult = (HWND*)lParam;
                        *pResult = hWnd;
                        CloseHandle(hProc);
                        return FALSE;
                    }
                }
            }
        }
        CloseHandle(hProc);
    }
    return TRUE;
}

static HWND g_cachedHwnd = NULL;
static wchar_t g_cachedExePath[MAX_PATH] = { 0 };
static const wchar_t* APP_MUTEX_NAME = L"FastExplorer_SingleInstance_Mutex_Global";

static bool TryGetExeFromRegistry(HKEY rootKey, const wchar_t* subKey, const wchar_t* valueName, wchar_t* outPath, DWORD maxLen)
{
    HKEY hKey;
    if (RegOpenKeyExW(rootKey, subKey, 0, KEY_READ, &hKey) == ERROR_SUCCESS)
    {
        wchar_t buf[MAX_PATH * 2] = { 0 };
        DWORD type = REG_SZ;
        DWORD size = sizeof(buf);
        if (RegQueryValueExW(hKey, valueName, NULL, &type, (LPBYTE)buf, &size) == ERROR_SUCCESS)
        {
            RegCloseKey(hKey);
            wchar_t* pStart = buf;
            if (*pStart == L'"') pStart++;
            wchar_t* pEnd = wcschr(pStart, L'"');
            if (pEnd != NULL) *pEnd = L'\0';
            else
            {
                wchar_t* pSpace = wcschr(pStart, L' ');
                if (pSpace != NULL) *pSpace = L'\0';
            }

            if (pStart[0] != L'\0' && GetFileAttributesW(pStart) != INVALID_FILE_ATTRIBUTES)
            {
                wcscpy_s(outPath, maxLen, pStart);
                return true;
            }
        }
        else
        {
            RegCloseKey(hKey);
        }
    }
    return false;
}

// FastExplorer.exe の正確な実在パスを取得 (初回にキャッシュ)
static bool GetFastExplorerExePath(wchar_t* outPath, DWORD maxLen)
{
    if (g_cachedExePath[0] != L'\0' && GetFileAttributesW(g_cachedExePath) != INVALID_FILE_ATTRIBUTES)
    {
        wcscpy_s(outPath, maxLen, g_cachedExePath);
        return true;
    }

    // 1. 同一ディレクトリの FastExplorer.exe (インストール環境での最優先)
    wchar_t dirPath[MAX_PATH];
    GetModuleFileNameW(NULL, dirPath, MAX_PATH);
    wchar_t* lastSlash = wcsrchr(dirPath, L'\\');
    if (lastSlash != NULL)
    {
        *(lastSlash + 1) = L'\0';
        wcscpy_s(outPath, maxLen, dirPath);
        wcscat_s(outPath, maxLen, L"FastExplorer.exe");
        if (GetFileAttributesW(outPath) != INVALID_FILE_ATTRIBUTES)
        {
            wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
            return true;
        }
    }

    // 2. インストーラーが記録したレジストリ (HKCU\Software\FastExplorer\InstallPath)
    if (TryGetExeFromRegistry(HKEY_CURRENT_USER, L"Software\\FastExplorer", L"InstallPath", outPath, maxLen))
    {
        wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
        return true;
    }

    // 3. Windows App Paths (HKCU / HKLM)
    if (TryGetExeFromRegistry(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\FastExplorer.exe", NULL, outPath, maxLen) ||
        TryGetExeFromRegistry(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\FastExplorer.exe", NULL, outPath, maxLen))
    {
        wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
        return true;
    }

    // 4. レジストリ登録パスからの取得 (Software\Classes\Directory\shell\open\command)
    if (TryGetExeFromRegistry(HKEY_CURRENT_USER, L"Software\\Classes\\Directory\\shell\\open\\command", NULL, outPath, maxLen))
    {
        wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
        return true;
    }

    // 5. 動的に取得した Program Files 配下の FastExplorer.exe (Cドライブ固定を排除)
    PWSTR pKnownPath = NULL;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, 0, NULL, &pKnownPath)))
    {
        wchar_t pfPath[MAX_PATH];
        wcscpy_s(pfPath, MAX_PATH, pKnownPath);
        wcscat_s(pfPath, MAX_PATH, L"\\FastExplorer\\FastExplorer.exe");
        CoTaskMemFree(pKnownPath);

        if (GetFileAttributesW(pfPath) != INVALID_FILE_ATTRIBUTES)
        {
            wcscpy_s(outPath, maxLen, pfPath);
            wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
            return true;
        }
    }

    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFilesX86, 0, NULL, &pKnownPath)))
    {
        wchar_t pfPath[MAX_PATH];
        wcscpy_s(pfPath, MAX_PATH, pKnownPath);
        wcscat_s(pfPath, MAX_PATH, L"\\FastExplorer\\FastExplorer.exe");
        CoTaskMemFree(pKnownPath);

        if (GetFileAttributesW(pfPath) != INVALID_FILE_ATTRIBUTES)
        {
            wcscpy_s(outPath, maxLen, pfPath);
            wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
            return true;
        }
    }

    // 6. 開発ディレクトリの FastExplorer.exe (win-x64 / win-arm64, Debug / Release)
    const wchar_t* devRelativePaths[] = {
        L"..\\..\\bin\\x64\\Debug\\net10.0-windows10.0.19041.0\\win-x64\\FastExplorer.exe",
        L"..\\..\\bin\\Release\\net10.0-windows10.0.19041.0\\win-x64\\FastExplorer.exe",
        L"..\\..\\bin\\ARM64\\Debug\\net10.0-windows10.0.19041.0\\win-arm64\\FastExplorer.exe",
        L"..\\..\\bin\\Release\\net10.0-windows10.0.19041.0\\win-arm64\\FastExplorer.exe",
        L"bin\\x64\\Debug\\net10.0-windows10.0.19041.0\\win-x64\\FastExplorer.exe",
        L"bin\\Release\\net10.0-windows10.0.19041.0\\win-x64\\publish\\FastExplorer.exe"
    };

    if (lastSlash != NULL)
    {
        for (const wchar_t* rel : devRelativePaths)
        {
            wchar_t devPath[MAX_PATH];
            wcscpy_s(devPath, MAX_PATH, dirPath);
            wcscat_s(devPath, MAX_PATH, rel);
            if (GetFileAttributesW(devPath) != INVALID_FILE_ATTRIBUTES)
            {
                wcscpy_s(outPath, maxLen, devPath);
                wcscpy_s(g_cachedExePath, MAX_PATH, outPath);
                return true;
            }
        }
    }

    return false;
}

static void ForceForeground(HWND hWnd)
{
    if (hWnd == NULL) return;

    HWND hFg = GetForegroundWindow();
    DWORD fgThread = hFg != NULL ? GetWindowThreadProcessId(hFg, NULL) : 0;
    DWORD curThread = GetCurrentThreadId();

    if (fgThread != 0 && fgThread != curThread)
    {
        AttachThreadInput(curThread, fgThread, TRUE);
    }

    if (IsIconic(hWnd))
    {
        ShowWindow(hWnd, SW_RESTORE);
    }
    else
    {
        ShowWindow(hWnd, SW_SHOW);
    }

    SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

    SetForegroundWindow(hWnd);
    BringWindowToTop(hWnd);
    SetFocus(hWnd);

    if (fgThread != 0 && fgThread != curThread)
    {
        AttachThreadInput(curThread, fgThread, FALSE);
    }
}

static bool IsFastExplorerRunning()
{
    HANDLE hMutex = OpenMutexW(SYNCHRONIZE, FALSE, APP_MUTEX_NAME);
    if (hMutex != NULL)
    {
        CloseHandle(hMutex);
        return true;
    }
    return false;
}

static void LaunchOrActivateFastExplorer(const wchar_t* customArgs = NULL)
{
    // 1. プロセスが起動しているかを O(1) で即時判定
    if (IsFastExplorerRunning())
    {
        // キャッシュされたウィンドウハンドルが有効かチェック
        if (g_cachedHwnd != NULL && IsWindow(g_cachedHwnd))
        {
            ForceForeground(g_cachedHwnd);
            return;
        }

        // キャッシュが無効な場合のみ EnumWindows で検索
        HWND hExistingWnd = NULL;
        EnumWindows(EnumWindowsProc, (LPARAM)&hExistingWnd);

        if (hExistingWnd != NULL)
        {
            g_cachedHwnd = hExistingWnd;
            ForceForeground(hExistingWnd);
            return;
        }
    }

    // 2. 起動していない場合は重いウィンドウ探索を完全にスキップして即座にプロセス起動
    g_cachedHwnd = NULL;
    wchar_t exePath[MAX_PATH] = { 0 };
    if (GetFastExplorerExePath(exePath, MAX_PATH))
    {
        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = { 0 };
        wchar_t cmdLine[MAX_PATH * 2];

        if (customArgs != NULL && customArgs[0] != L'\0')
        {
            swprintf_s(cmdLine, L"\"%s\" %s", exePath, customArgs);
        }
        else
        {
            swprintf_s(cmdLine, L"\"%s\"", exePath);
        }

        if (CreateProcessW(NULL, cmdLine, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        else
        {
            ShellExecuteW(NULL, L"open", exePath, customArgs, NULL, SW_SHOWNORMAL);
        }
    }
}

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
                    LaunchOrActivateFastExplorer();
                    return 1; // Windows標準Explorerの起動をブロック
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

    SetDisabledHotkeys(true);

    g_hHook = SetWindowsHookExW(WH_KEYBOARD_LL, LowLevelKeyboardProc, hInstance, 0);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_hHook != NULL)
    {
        UnhookWindowsHookEx(g_hHook);
        g_hHook = NULL;
    }

    SetDisabledHotkeys(false);

    if (g_hMutex != NULL)
    {
        ReleaseMutex(g_hMutex);
        CloseHandle(g_hMutex);
        g_hMutex = NULL;
    }

    CoUninitialize();

    return 0;
}
