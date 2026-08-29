#pragma once

#include "FastExplorerWatcher.Common.h"

typedef NTSTATUS(NTAPI* pfnNtQueryInformationProcess)(
    HANDLE ProcessHandle,
    PROCESSINFOCLASS ProcessInformationClass,
    PVOID ProcessInformation,
    ULONG ProcessInformationLength,
    PULONG ReturnLength);

// プロセスのコマンドライン引数を直接メモリから高速取得
static bool GetProcessCommandLine(DWORD pid, std::wstring& outCmdLine)
{
    HANDLE hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!hProcess) return false;

    HMODULE hNtdll = GetModuleHandleW(L"ntdll.dll");
    if (!hNtdll) { CloseHandle(hProcess); return false; }

    pfnNtQueryInformationProcess NtQueryInformationProcess =
        (pfnNtQueryInformationProcess)GetProcAddress(hNtdll, "NtQueryInformationProcess");
    if (!NtQueryInformationProcess) { CloseHandle(hProcess); return false; }

    PROCESS_BASIC_INFORMATION pbi;
    ULONG len = 0;
    if (NtQueryInformationProcess(hProcess, ProcessBasicInformation, &pbi, sizeof(pbi), &len) >= 0)
    {
        PEB peb;
        if (ReadProcessMemory(hProcess, pbi.PebBaseAddress, &peb, sizeof(peb), NULL))
        {
            RTL_USER_PROCESS_PARAMETERS params;
            if (ReadProcessMemory(hProcess, peb.ProcessParameters, &params, sizeof(params), NULL))
            {
                if (params.CommandLine.Length > 0 && params.CommandLine.Buffer != NULL)
                {
                    wchar_t* buf = new wchar_t[params.CommandLine.Length / sizeof(wchar_t) + 1];
                    if (ReadProcessMemory(hProcess, params.CommandLine.Buffer, buf, params.CommandLine.Length, NULL))
                    {
                        buf[params.CommandLine.Length / sizeof(wchar_t)] = L'\0';
                        outCmdLine = buf;
                        delete[] buf;
                        CloseHandle(hProcess);
                        return true;
                    }
                    delete[] buf;
                }
            }
        }
    }
    CloseHandle(hProcess);
    return false;
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

    // 2. インストーラーが記録したレジストリ (HKCU / HKLM)
    if (TryGetExeFromRegistry(HKEY_CURRENT_USER, L"Software\\FastExplorer", L"InstallPath", outPath, maxLen) ||
        TryGetExeFromRegistry(HKEY_LOCAL_MACHINE, L"Software\\FastExplorer", L"InstallPath", outPath, maxLen))
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

    // 4. 動的に取得した Program Files 配下の FastExplorer.exe
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

static bool TrySendPipeMessage(const wchar_t* customArgs)
{
    HANDLE hPipe = CreateFileW(
        PIPE_NAME,
        GENERIC_WRITE,
        0,
        NULL,
        OPEN_EXISTING,
        0,
        NULL);

    if (hPipe != INVALID_HANDLE_VALUE)
    {
        std::wstring payload = L"FastExplorer.exe\n";
        if (customArgs != NULL && customArgs[0] != L'\0')
        {
            payload += customArgs;
        }
        payload += L"\n";

        DWORD written = 0;
        int utf8Len = WideCharToMultiByte(CP_UTF8, 0, payload.c_str(), -1, NULL, 0, NULL, NULL);
        if (utf8Len > 0)
        {
            char* utf8Buf = new char[utf8Len];
            WideCharToMultiByte(CP_UTF8, 0, payload.c_str(), -1, utf8Buf, utf8Len, NULL, NULL);
            WriteFile(hPipe, utf8Buf, (DWORD)strlen(utf8Buf), &written, NULL);
            delete[] utf8Buf;
        }
        CloseHandle(hPipe);
        LogWatcher(L"[Pipe] Sent args: %s", customArgs ? customArgs : L"(none)");
        return true;
    }
    return false;
}

static void LaunchOrActivateFastExplorer(const wchar_t* customArgs = NULL)
{
    LogWatcher(L"[LaunchOrActivate] Requested with args: %s", customArgs ? customArgs : L"(null)");

    // 1. 起動中の FastExplorer プロセスに名前付きパイプで通知
    if (TrySendPipeMessage(customArgs))
    {
        if (g_cachedHwnd != NULL && IsWindow(g_cachedHwnd))
        {
            ForceForeground(g_cachedHwnd);
            return;
        }

        HWND hExistingWnd = NULL;
        EnumWindows(EnumWindowsProc, (LPARAM)&hExistingWnd);
        if (hExistingWnd != NULL)
        {
            g_cachedHwnd = hExistingWnd;
            ForceForeground(hExistingWnd);
            return;
        }
        return;
    }

    // 2. 起動していない場合は GUI プロセスとして明示的にフォアグラウンド起動
    g_cachedHwnd = NULL;
    wchar_t exePath[MAX_PATH] = { 0 };
    if (GetFastExplorerExePath(exePath, MAX_PATH))
    {
        wchar_t exeDir[MAX_PATH] = { 0 };
        wcscpy_s(exeDir, MAX_PATH, exePath);
        wchar_t* lastSlash = wcsrchr(exeDir, L'\\');
        if (lastSlash != NULL) *lastSlash = L'\0';

        std::wstring safeArgs;
        if (customArgs != NULL && customArgs[0] != L'\0')
        {
            safeArgs = L"\"";
            safeArgs += customArgs;
            if (!safeArgs.empty() && safeArgs.back() == L'\\')
            {
                safeArgs += L'\\'; // Win32 コマンドライン引数エスケープバグ対策
            }
            safeArgs += L"\"";
        }

        SHELLEXECUTEINFOW sei = { sizeof(sei) };
        sei.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI;
        sei.lpVerb = L"open";
        sei.lpFile = exePath;
        sei.lpParameters = !safeArgs.empty() ? safeArgs.c_str() : NULL;
        sei.lpDirectory = exeDir[0] != L'\0' ? exeDir : NULL;
        sei.nShow = SW_SHOWNORMAL;

        LogWatcher(L"[Launch] Executing ShellExecuteEx: %s %s", exePath, sei.lpParameters ? sei.lpParameters : L"");

        if (ShellExecuteExW(&sei))
        {
            if (sei.hProcess != NULL)
            {
                CloseHandle(sei.hProcess);
            }
            LogWatcher(L"[Launch] ShellExecuteEx SUCCEEDED.");
            return;
        }

        // ShellExecuteEx が失敗した場合の CreateProcessW フォールバック (親プロセスの SW_HIDE 継承を明示防止)
        STARTUPINFOW si = { sizeof(si) };
        si.cb = sizeof(si);
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_SHOWNORMAL;
        PROCESS_INFORMATION pi = { 0 };
        wchar_t cmdLine[MAX_PATH * 2];

        if (!safeArgs.empty())
        {
            swprintf_s(cmdLine, L"\"%s\" %s", exePath, safeArgs.c_str());
        }
        else
        {
            swprintf_s(cmdLine, L"\"%s\"", exePath);
        }

        LogWatcher(L"[Launch] Falling back to CreateProcessW: %s", cmdLine);

        if (CreateProcessW(NULL, cmdLine, NULL, NULL, FALSE, 0, NULL, exeDir[0] != L'\0' ? exeDir : NULL, &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            LogWatcher(L"[Launch] CreateProcessW SUCCEEDED.");
        }
        else
        {
            LogWatcher(L"[Launch] CreateProcessW FAILED: %d", GetLastError());
        }
    }
}

// コマンドライン文字列から引数部分（/select,... または パス）を抽出
static std::wstring ExtractArgsFromCommandLine(const std::wstring& cmdLine)
{
    if (cmdLine.empty()) return L"";

    // 1. /select, オプションの検出
    size_t selectPos = cmdLine.find(L"/select");
    if (selectPos != std::wstring::npos)
    {
        return cmdLine.substr(selectPos);
    }

    // 2. 引数部分の抽出 (最初のトークンをスキップ)
    size_t idx = 0;
    while (idx < cmdLine.length() && iswspace(cmdLine[idx])) idx++;
    if (idx >= cmdLine.length()) return L"";

    if (cmdLine[idx] == L'"')
    {
        idx++;
        while (idx < cmdLine.length() && cmdLine[idx] != L'"') idx++;
        if (idx < cmdLine.length()) idx++;
    }
    else
    {
        while (idx < cmdLine.length() && !iswspace(cmdLine[idx])) idx++;
    }

    while (idx < cmdLine.length() && iswspace(cmdLine[idx])) idx++;
    if (idx < cmdLine.length())
    {
        return cmdLine.substr(idx);
    }

    return L"";
}
