#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <psapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <exdisp.h>
#include <winternl.h>
#include <string>
#include <fstream>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")

static HHOOK g_hHook = NULL;
static HWINEVENTHOOK g_hWinEventHook = NULL;
static HWINEVENTHOOK g_hFgHook = NULL;
static HANDLE g_hMutex = NULL;
static const wchar_t* MUTEX_NAME = L"FastExplorerWatcherMutex_Global";
static const wchar_t* PIPE_NAME = L"\\\\.\\pipe\\FastExplorer_SingleInstance_Pipe_Global";
static const wchar_t* APP_MUTEX_NAME = L"FastExplorer_SingleInstance_Mutex_Global";
static const wchar_t* HOOK_PROP_NAME = L"FastExplorer_WindowHooked";

static inline void LogWatcher(const wchar_t* format, ...)
{
    // Release build: no-op for zero I/O overhead
}

static void CleanDisabledHotkeys()
{
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", 0, KEY_READ | KEY_WRITE, &hKey) == ERROR_SUCCESS)
    {
        wchar_t buffer[256] = { 0 };
        DWORD type = REG_SZ;
        DWORD size = sizeof(buffer);
        if (RegQueryValueExW(hKey, L"DisabledHotkeys", NULL, &type, (LPBYTE)buffer, &size) == ERROR_SUCCESS)
        {
            std::wstring val(buffer);
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

static bool IsDefaultExplorerEnabledInRegistry()
{
    // HKLM または HKCU をチェック
    DWORD val = 0;
    DWORD type = REG_DWORD;
    DWORD size = sizeof(val);

    HKEY hKey;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\FastExplorer", 0, KEY_READ, &hKey) == ERROR_SUCCESS)
    {
        if (RegQueryValueExW(hKey, L"ReplaceDefaultExplorer", NULL, &type, (LPBYTE)&val, &size) == ERROR_SUCCESS)
        {
            RegCloseKey(hKey);
            return val != 0;
        }
        RegCloseKey(hKey);
    }

    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\FastExplorer", 0, KEY_READ, &hKey) == ERROR_SUCCESS)
    {
        if (RegQueryValueExW(hKey, L"ReplaceDefaultExplorer", NULL, &type, (LPBYTE)&val, &size) == ERROR_SUCCESS)
        {
            RegCloseKey(hKey);
            return val != 0;
        }
        RegCloseKey(hKey);
    }

    return true; // デフォルトは有効
}
