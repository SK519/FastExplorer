use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::Mutex;
use windows::core::{PCWSTR, PWSTR, s, w};
use windows::Win32::Foundation::{
    CloseHandle, GetLastError, BOOL, ERROR_PIPE_BUSY, ERROR_SUCCESS, GENERIC_WRITE, HANDLE, HWND,
    INVALID_HANDLE_VALUE, LPARAM,
};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, WriteFile, FILE_CREATION_DISPOSITION, FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_MODE,
    OPEN_EXISTING,
};
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::System::Diagnostics::Debug::ReadProcessMemory;
use windows::Win32::System::LibraryLoader::{GetModuleFileNameW, GetModuleHandleW, GetProcAddress};
use windows::Win32::System::Pipes::WaitNamedPipeW;
use windows::Win32::System::Registry::{
    RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE,
    KEY_READ, REG_SZ, REG_VALUE_TYPE,
};
use windows::Win32::System::Threading::{
    AttachThreadInput, CreateProcessW, GetCurrentThreadId, OpenProcess, QueryFullProcessImageNameW,
    PROCESS_CREATION_FLAGS, PROCESS_INFORMATION, PROCESS_NAME_WIN32, PROCESS_QUERY_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_VM_READ, STARTF_USESHOWWINDOW, STARTUPINFOW,
};
use windows::Win32::UI::Input::KeyboardAndMouse::SetFocus;
use windows::Win32::UI::Shell::{
    FOLDERID_ProgramFiles, SHGetKnownFolderPath, ShellExecuteExW, KF_FLAG_DEFAULT,
    SEE_MASK_FLAG_NO_UI, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, EnumWindows, GetClassNameW, GetForegroundWindow, GetWindowThreadProcessId,
    IsIconic, IsWindow, IsWindowVisible, SetForegroundWindow, SetWindowPos, ShowWindow,
    HWND_NOTOPMOST, HWND_TOPMOST, SWP_NOMOVE, SWP_NOSIZE, SWP_SHOWWINDOW, SW_RESTORE, SW_SHOW,
    SW_SHOWNORMAL,
};

const PIPE_NAME: PCWSTR = w!("\\\\.\\pipe\\FastExplorer_SingleInstance_Pipe_Global");

static CACHED_HWND: AtomicIsize = AtomicIsize::new(0);
static CACHED_EXE_PATH: Mutex<Option<PathBuf>> = Mutex::new(None);

pub fn to_wide_null(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

// --- PEB Command Line Reading (0ms resolution) ---

#[repr(C)]
struct ProcessBasicInformation {
    exit_status: i32,
    peb_base_address: *const std::ffi::c_void,
    affinity_mask: usize,
    base_priority: i32,
    unique_process_id: usize,
    inherited_from_unique_process_id: usize,
}

#[repr(C)]
struct UnicodeString {
    length: u16,
    maximum_length: u16,
    buffer: *const u16,
}

type NtQueryInformationProcessFn = unsafe extern "system" fn(
    process_handle: HANDLE,
    process_information_class: u32,
    process_information: *mut std::ffi::c_void,
    process_information_length: u32,
    return_length: *mut u32,
) -> i32;

/// Read another process's command line directly from its PEB (64-bit Windows layout).
pub fn get_process_command_line(pid: u32) -> Option<String> {
    unsafe {
        let h_proc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid).ok()?;
        struct HandleGuard(HANDLE);
        impl Drop for HandleGuard {
            fn drop(&mut self) {
                unsafe {
                    let _ = CloseHandle(self.0);
                }
            }
        }
        let _guard = HandleGuard(h_proc);

        let h_ntdll = GetModuleHandleW(w!("ntdll.dll")).ok()?;
        let proc_addr = GetProcAddress(h_ntdll, s!("NtQueryInformationProcess"))?;
        let nt_query: NtQueryInformationProcessFn = std::mem::transmute(proc_addr);

        let mut pbi = std::mem::zeroed::<ProcessBasicInformation>();
        let mut ret_len: u32 = 0;
        let status = nt_query(
            h_proc,
            0, // ProcessBasicInformation
            &mut pbi as *mut _ as *mut _,
            std::mem::size_of::<ProcessBasicInformation>() as u32,
            &mut ret_len,
        );
        if status < 0 || pbi.peb_base_address.is_null() {
            return None;
        }

        // On 64-bit Windows, PEB.ProcessParameters is at offset 0x20.
        // RTL_USER_PROCESS_PARAMETERS.CommandLine (UNICODE_STRING) is at offset 0x70.
        #[cfg(target_pointer_width = "64")]
        const PEB_PROCESS_PARAMS_OFFSET: usize = 0x20;
        #[cfg(target_pointer_width = "64")]
        const PARAMS_CMDLINE_OFFSET: usize = 0x70;

        #[cfg(target_pointer_width = "32")]
        const PEB_PROCESS_PARAMS_OFFSET: usize = 0x10;
        #[cfg(target_pointer_width = "32")]
        const PARAMS_CMDLINE_OFFSET: usize = 0x40;

        let mut params_ptr: *const u8 = std::ptr::null();
        let peb_params_addr = (pbi.peb_base_address as *const u8).add(PEB_PROCESS_PARAMS_OFFSET);
        ReadProcessMemory(
            h_proc,
            peb_params_addr as *const _,
            &mut params_ptr as *mut _ as *mut _,
            std::mem::size_of::<*const u8>(),
            None,
        )
        .ok()?;
        if params_ptr.is_null() {
            return None;
        }

        let mut cmd_str = std::mem::zeroed::<UnicodeString>();
        let cmd_addr = params_ptr.add(PARAMS_CMDLINE_OFFSET);
        ReadProcessMemory(
            h_proc,
            cmd_addr as *const _,
            &mut cmd_str as *mut _ as *mut _,
            std::mem::size_of::<UnicodeString>(),
            None,
        )
        .ok()?;

        if cmd_str.length == 0 || cmd_str.buffer.is_null() {
            return None;
        }

        let char_count = (cmd_str.length as usize) / 2;
        if char_count > 32768 {
            return None;
        }
        let mut buf = vec![0u16; char_count];
        ReadProcessMemory(
            h_proc,
            cmd_str.buffer as *const _,
            buf.as_mut_ptr() as *mut _,
            cmd_str.length as usize,
            None,
        )
        .ok()?;

        Some(String::from_utf16_lossy(&buf))
    }
}

/// Extract `/select,...` or path argument from a full process command line.
pub fn extract_args_from_command_line(cmd_line: &str) -> String {
    if cmd_line.is_empty() {
        return String::new();
    }
    if let Some(pos) = cmd_line.find("/select") {
        return cmd_line[pos..].to_string();
    }

    let trimmed = cmd_line.trim_start();
    if trimmed.is_empty() {
        return String::new();
    }

    let rest = if let Some(after_quote) = trimmed.strip_prefix('"') {
        match after_quote.find('"') {
            Some(end_idx) => &after_quote[end_idx + 1..],
            None => "",
        }
    } else {
        match trimmed.find(char::is_whitespace) {
            Some(ws_idx) => &trimmed[ws_idx..],
            None => "",
        }
    };

    rest.trim_start().to_string()
}

// --- Window Enumeration & Foreground ---

unsafe extern "system" fn enum_windows_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
    if !IsWindowVisible(hwnd).as_bool() {
        return BOOL(1);
    }

    let mut pid: u32 = 0;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid == 0 {
        return BOOL(1);
    }

    if let Ok(h_proc) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
        let mut exe_buf = [0u16; 1024];
        let mut size = exe_buf.len() as u32;
        let ok = QueryFullProcessImageNameW(
            h_proc,
            PROCESS_NAME_WIN32,
            PWSTR(exe_buf.as_mut_ptr()),
            &mut size,
        )
        .is_ok();
        let _ = CloseHandle(h_proc);

        if ok {
            let exe_str = String::from_utf16_lossy(&exe_buf[..size as usize]);
            if let Some(fname) = Path::new(&exe_str).file_name().and_then(|s| s.to_str()) {
                if fname.eq_ignore_ascii_case("FastExplorer.exe") {
                    let mut cls_buf = [0u16; 256];
                    let cls_len = GetClassNameW(hwnd, &mut cls_buf);
                    if cls_len > 0 {
                        let cls_str = String::from_utf16_lossy(&cls_buf[..cls_len as usize]);
                        if cls_str == "WinUIDesktopWin32WindowClass" {
                            let out = &mut *(lparam.0 as *mut HWND);
                            *out = hwnd;
                            return BOOL(0);
                        }
                    }
                }
            }
        }
    }

    BOOL(1)
}

pub fn force_foreground(hwnd: HWND) {
    if hwnd.0.is_null() {
        return;
    }
    unsafe {
        let h_fg = GetForegroundWindow();
        let fg_thread = if !h_fg.0.is_null() {
            GetWindowThreadProcessId(h_fg, None)
        } else {
            0
        };
        let cur_thread = GetCurrentThreadId();

        if fg_thread != 0 && fg_thread != cur_thread {
            let _ = AttachThreadInput(cur_thread, fg_thread, true);
        }

        if IsIconic(hwnd).as_bool() {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        } else {
            let _ = ShowWindow(hwnd, SW_SHOW);
        }

        let _ = SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
        );
        let _ = SetWindowPos(
            hwnd,
            HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
        );

        let _ = SetForegroundWindow(hwnd);
        let _ = BringWindowToTop(hwnd);
        let _ = SetFocus(hwnd);

        if fg_thread != 0 && fg_thread != cur_thread {
            let _ = AttachThreadInput(cur_thread, fg_thread, false);
        }
    }
}

// --- Executable Path Discovery (dynamic PathBuf, no MAX_PATH cap) ---

fn try_get_exe_from_registry(root: HKEY, subkey: PCWSTR, value_name: PCWSTR) -> Option<PathBuf> {
    unsafe {
        let mut hkey = HKEY::default();
        if RegOpenKeyExW(root, subkey, 0, KEY_READ, &mut hkey) != ERROR_SUCCESS {
            return None;
        }
        let mut buf = [0u16; 1024];
        let mut size = (buf.len() * std::mem::size_of::<u16>()) as u32;
        let mut reg_type = REG_VALUE_TYPE(REG_SZ.0);
        let res = RegQueryValueExW(
            hkey,
            value_name,
            None,
            Some(&mut reg_type),
            Some(buf.as_mut_ptr() as *mut u8),
            Some(&mut size),
        );
        let _ = RegCloseKey(hkey);
        if res != ERROR_SUCCESS {
            return None;
        }

        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        let raw = String::from_utf16_lossy(&buf[..len]);
        let cleaned = if let Some(after_q) = raw.strip_prefix('"') {
            after_q.split('"').next().unwrap_or("")
        } else {
            raw.split_whitespace().next().unwrap_or("")
        };

        if !cleaned.is_empty() {
            let p = PathBuf::from(cleaned);
            if p.is_file() {
                return Some(p);
            }
        }
        None
    }
}

fn get_fast_explorer_exe_path() -> Option<PathBuf> {
    if let Ok(guard) = CACHED_EXE_PATH.lock() {
        if let Some(ref cached) = *guard {
            if cached.is_file() {
                return Some(cached.clone());
            }
        }
    }

    let mut found: Option<PathBuf> = None;

    // 1. Same directory as FastExplorerWatcher.exe
    unsafe {
        let mut buf = [0u16; 1024];
        let len = GetModuleFileNameW(None, &mut buf) as usize;
        if len > 0 && len < buf.len() {
            let self_path = PathBuf::from(String::from_utf16_lossy(&buf[..len]));
            if let Some(dir) = self_path.parent() {
                let candidate = dir.join("FastExplorer.exe");
                if candidate.is_file() {
                    found = Some(candidate);
                }
            }
        }
    }

    // 2. Installer registry (HKCU / HKLM)
    if found.is_none() {
        found = try_get_exe_from_registry(
            HKEY_CURRENT_USER,
            w!("Software\\FastExplorer"),
            w!("InstallPath"),
        )
        .or_else(|| {
            try_get_exe_from_registry(
                HKEY_LOCAL_MACHINE,
                w!("Software\\FastExplorer"),
                w!("InstallPath"),
            )
        });
    }

    // 3. App Paths (HKCU / HKLM)
    if found.is_none() {
        let app_paths = w!("Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\FastExplorer.exe");
        found = try_get_exe_from_registry(HKEY_CURRENT_USER, app_paths, PCWSTR::null())
            .or_else(|| try_get_exe_from_registry(HKEY_LOCAL_MACHINE, app_paths, PCWSTR::null()));
    }

    // 4. Program Files\FastExplorer\FastExplorer.exe
    if found.is_none() {
        unsafe {
            if let Ok(pwstr) = SHGetKnownFolderPath(&FOLDERID_ProgramFiles, KF_FLAG_DEFAULT, None) {
                if let Ok(pf) = pwstr.to_string() {
                    let candidate = PathBuf::from(pf).join("FastExplorer").join("FastExplorer.exe");
                    if candidate.is_file() {
                        found = Some(candidate);
                    }
                }
                CoTaskMemFree(Some(pwstr.0 as *const _));
            }
        }
    }

    if let Some(ref p) = found {
        if let Ok(mut guard) = CACHED_EXE_PATH.lock() {
            *guard = Some(p.clone());
        }
    }

    found
}

// --- Named Pipe IPC & Launch ---

fn open_pipe() -> Option<HANDLE> {
    unsafe {
        let open_once = || {
            CreateFileW(
                PIPE_NAME,
                GENERIC_WRITE.0,
                FILE_SHARE_MODE(0),
                None,
                FILE_CREATION_DISPOSITION(OPEN_EXISTING.0),
                FILE_FLAGS_AND_ATTRIBUTES(0),
                None,
            )
        };

        match open_once() {
            Ok(h) if h != INVALID_HANDLE_VALUE => Some(h),
            _ => {
                if GetLastError() == ERROR_PIPE_BUSY && WaitNamedPipeW(PIPE_NAME, 300).as_bool() {
                    open_once().ok().filter(|&h| h != INVALID_HANDLE_VALUE)
                } else {
                    None
                }
            }
        }
    }
}

pub fn try_send_pipe_message(custom_args: Option<&str>) -> bool {
    let Some(h_pipe) = open_pipe() else {
        return false;
    };

    let mut payload = String::from("FastExplorer.exe\n");
    if let Some(args) = custom_args {
        if !args.is_empty() {
            payload.push_str(args);
        }
    }
    payload.push('\n');

    unsafe {
        let mut written: u32 = 0;
        let _ = WriteFile(h_pipe, Some(payload.as_bytes()), Some(&mut written), None);
        let _ = CloseHandle(h_pipe);
    }
    true
}

pub fn launch_or_activate_fast_explorer(custom_args: Option<&str>) {
    // 1. Existing running instance via Named Pipe
    if try_send_pipe_message(custom_args) {
        let cached = HWND(CACHED_HWND.load(Ordering::Relaxed) as *mut _);
        unsafe {
            if !cached.0.is_null() && IsWindow(cached).as_bool() {
                force_foreground(cached);
                return;
            }
            let mut found = HWND::default();
            let _ = EnumWindows(
                Some(enum_windows_proc),
                LPARAM(&mut found as *mut HWND as isize),
            );
            if !found.0.is_null() {
                CACHED_HWND.store(found.0 as isize, Ordering::Relaxed);
                force_foreground(found);
            }
        }
        return;
    }

    // 2. Launch new GUI instance
    CACHED_HWND.store(0, Ordering::Relaxed);
    let Some(exe_path) = get_fast_explorer_exe_path() else {
        return;
    };
    let exe_str = exe_path.to_string_lossy().to_string();
    let dir_str = exe_path
        .parent()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_default();

    let safe_args = match custom_args {
        Some(a) if !a.is_empty() => {
            if a.starts_with('"') || a.to_ascii_lowercase().starts_with("/select") {
                a.to_string()
            } else if a.ends_with('\\') {
                format!("\"{}\\\"", a)
            } else {
                format!("\"{}\"", a)
            }
        }
        _ => String::new(),
    };

    let exe_w = to_wide_null(&exe_str);
    let dir_w = to_wide_null(&dir_str);
    let args_w = to_wide_null(&safe_args);

    unsafe {
        let mut sei = SHELLEXECUTEINFOW {
            cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
            fMask: SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI,
            lpVerb: w!("open"),
            lpFile: PCWSTR(exe_w.as_ptr()),
            lpParameters: if !safe_args.is_empty() {
                PCWSTR(args_w.as_ptr())
            } else {
                PCWSTR::null()
            },
            lpDirectory: if !dir_str.is_empty() {
                PCWSTR(dir_w.as_ptr())
            } else {
                PCWSTR::null()
            },
            nShow: SW_SHOWNORMAL.0,
            ..Default::default()
        };

        if ShellExecuteExW(&mut sei).is_ok() {
            if !sei.hProcess.is_invalid() && !sei.hProcess.0.is_null() {
                let _ = CloseHandle(sei.hProcess);
            }
            return;
        }

        // Fallback: CreateProcessW
        let full_cmd = if !safe_args.is_empty() {
            format!("\"{}\" {}", exe_str, safe_args)
        } else {
            format!("\"{}\"", exe_str)
        };
        let mut full_cmd_w = to_wide_null(&full_cmd);
        let si = STARTUPINFOW {
            cb: std::mem::size_of::<STARTUPINFOW>() as u32,
            dwFlags: STARTF_USESHOWWINDOW,
            wShowWindow: SW_SHOWNORMAL.0 as u16,
            ..Default::default()
        };
        let mut pi = PROCESS_INFORMATION::default();
        if CreateProcessW(
            None,
            PWSTR(full_cmd_w.as_mut_ptr()),
            None,
            None,
            false,
            PROCESS_CREATION_FLAGS(0),
            None,
            if !dir_str.is_empty() {
                PCWSTR(dir_w.as_ptr())
            } else {
                PCWSTR::null()
            },
            &si,
            &mut pi,
        )
        .is_ok()
        {
            let _ = CloseHandle(pi.hThread);
            let _ = CloseHandle(pi.hProcess);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_extract_args_from_command_line() {
        assert_eq!(
            extract_args_from_command_line(r#"C:\Windows\explorer.exe /select,"C:\Temp\a.txt""#),
            r#"/select,"C:\Temp\a.txt""#
        );
        assert_eq!(
            extract_args_from_command_line(r#""C:\Windows\explorer.exe" "C:\Users\Test""#),
            r#""C:\Users\Test""#
        );
        assert_eq!(
            extract_args_from_command_line(r#"explorer.exe"#),
            ""
        );
    }
}
