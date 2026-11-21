use crate::config::is_default_explorer_enabled;
use crate::process::{
    extract_args_from_command_line, get_process_command_line, launch_or_activate_fast_explorer,
    to_wide_null,
};
use std::time::Duration;
use windows::core::{Interface, PCWSTR, PWSTR, VARIANT, w};
use windows::Win32::Foundation::{HANDLE, HWND, LPARAM, RECT, WPARAM};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoUninitialize, CLSCTX_ALL, COINIT_APARTMENTTHREADED,
};
use windows::Win32::UI::Accessibility::HWINEVENTHOOK;
use windows::Win32::UI::Shell::{
    IShellFolderViewDual, IShellWindows, IWebBrowserApp, PathCreateFromUrlW, ShellWindows,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetAncestor, GetClassNameW, GetPropW, GetWindowLongPtrW, GetWindowRect,
    GetWindowThreadProcessId, IsWindow, IsWindowVisible, PostMessageW, RemovePropW,
    SetForegroundWindow, SetLayeredWindowAttributes, SetPropW, SetWindowLongPtrW, SetWindowPos,
    ShowWindow, CHILDID_SELF, EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, GA_ROOT, GWL_EXSTYLE,
    GWL_STYLE, HWND_TOP, LWA_ALPHA, OBJID_WINDOW, SWP_HIDEWINDOW, SWP_NOACTIVATE, SWP_NOSIZE,
    SWP_NOZORDER, SWP_SHOWWINDOW, SW_HIDE, SW_SHOW, WM_CLOSE, WS_EX_LAYERED, WS_EX_TOOLWINDOW,
    WS_VISIBLE,
};

const HOOK_PROP_NAME: PCWSTR = w!("FastExplorer_WindowHooked");

struct RedirectContext {
    hwnd_raw: isize,
    pid: u32,
    orig_rect: RECT,
    orig_ex_style: isize,
    was_visible: bool,
}

fn resolve_path_via_shell_windows(hwnd: HWND) -> Option<String> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        struct ComGuard;
        impl Drop for ComGuard {
            fn drop(&mut self) {
                unsafe { CoUninitialize() };
            }
        }
        let _com = ComGuard;

        for _ in 0..50 {
            if !IsWindow(hwnd).as_bool() {
                break;
            }

            if let Ok(sw) = CoCreateInstance::<_, IShellWindows>(&ShellWindows, None, CLSCTX_ALL) {
                let count = sw.Count().unwrap_or(0);
                for i in 0..count {
                    let var = VARIANT::from(i);
                    if let Ok(disp) = sw.Item(&var) {
                        if let Ok(browser) = disp.cast::<IWebBrowserApp>() {
                            if let Ok(shwnd) = browser.HWND() {
                                let item_hwnd = HWND(shwnd.0 as *mut _);
                                let matched = item_hwnd == hwnd
                                    || GetAncestor(item_hwnd, GA_ROOT) == hwnd
                                    || GetAncestor(hwnd, GA_ROOT) == item_hwnd;

                                if matched {
                                    // 1. Check SelectedItems (/select support)
                                    if let Ok(doc) = browser.Document() {
                                        if let Ok(folder_view) = doc.cast::<IShellFolderViewDual>()
                                        {
                                            if let Ok(selected) = folder_view.SelectedItems() {
                                                if selected.Count().unwrap_or(0) > 0 {
                                                    let idx0 = VARIANT::from(0i32);
                                                    if let Ok(item) = selected.Item(&idx0) {
                                                        if let Ok(path_bstr) = item.Path() {
                                                            let p = path_bstr.to_string();
                                                            if !p.is_empty() {
                                                                return Some(format!(
                                                                    "/select,\"{}\"",
                                                                    p
                                                                ));
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    // 2. Fallback to LocationURL
                                    if let Ok(url_bstr) = browser.LocationURL() {
                                        let url = url_bstr.to_string();
                                        if !url.is_empty() {
                                            let url_w = to_wide_null(&url);
                                            let mut path_buf = [0u16; 2084];
                                            let mut path_len = path_buf.len() as u32;
                                            if PathCreateFromUrlW(
                                                PCWSTR(url_w.as_ptr()),
                                                PWSTR(path_buf.as_mut_ptr()),
                                                &mut path_len,
                                                0,
                                            )
                                            .is_ok()
                                                && path_len > 0
                                            {
                                                let p = String::from_utf16_lossy(
                                                    &path_buf[..path_len as usize],
                                                );
                                                let p = p.trim_end_matches('\0');
                                                if !p.is_empty() {
                                                    return Some(p.to_string());
                                                }
                                            } else if url.starts_with("file://")
                                                || url.starts_with("shell:")
                                            {
                                                return Some(url);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            std::thread::sleep(Duration::from_millis(10));
        }
    }
    None
}

fn run_redirect_worker(ctx: RedirectContext) {
    let hwnd = HWND(ctx.hwnd_raw as *mut _);

    // 1. 0ms resolution from PEB command line
    let mut target_path = get_process_command_line(ctx.pid)
        .map(|cmd| extract_args_from_command_line(&cmd))
        .filter(|s| !s.is_empty());

    // 2. Fallback: IShellWindows COM polling (10ms x 50)
    if target_path.is_none() {
        target_path = resolve_path_via_shell_windows(hwnd);
    }

    unsafe {
        let Some(path) = target_path else {
            // Could not resolve: safely restore Explorer window if it was originally visible
            if IsWindow(hwnd).as_bool() {
                let _ = RemovePropW(hwnd, HOOK_PROP_NAME);
                SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ctx.orig_ex_style);
                if ctx.was_visible {
                    let w = ctx.orig_rect.right - ctx.orig_rect.left;
                    let h = ctx.orig_rect.bottom - ctx.orig_rect.top;
                    if w > 100 && h > 100 {
                        let _ = SetWindowPos(
                            hwnd,
                            HWND_TOP,
                            ctx.orig_rect.left,
                            ctx.orig_rect.top,
                            w,
                            h,
                            SWP_SHOWWINDOW,
                        );
                    } else {
                        let _ = SetWindowPos(
                            hwnd,
                            HWND_TOP,
                            100,
                            100,
                            1000,
                            700,
                            SWP_SHOWWINDOW,
                        );
                    }
                    let _ = ShowWindow(hwnd, SW_SHOW);
                    let _ = SetForegroundWindow(hwnd);
                }
            }
            return;
        };

        if IsWindow(hwnd).as_bool() {
            let _ = ShowWindow(hwnd, SW_HIDE);
            let _ = RemovePropW(hwnd, HOOK_PROP_NAME);
            let _ = PostMessageW(hwnd, WM_CLOSE, WPARAM(0), LPARAM(0));
        }

        launch_or_activate_fast_explorer(Some(&path));
    }
}

pub unsafe extern "system" fn win_event_proc(
    _h_win_event_hook: HWINEVENTHOOK,
    event: u32,
    hwnd: HWND,
    id_object: i32,
    id_child: i32,
    _id_event_thread: u32,
    _dwms_event_time: u32,
) {
    if hwnd.0.is_null() || id_object != OBJID_WINDOW.0 || id_child != CHILDID_SELF as i32 {
        return;
    }
    if event != EVENT_OBJECT_CREATE && event != EVENT_OBJECT_SHOW {
        return;
    }

    let mut class_buf = [0u16; 256];
    let len = GetClassNameW(hwnd, &mut class_buf);
    if len <= 0 {
        return;
    }
    let class_name = String::from_utf16_lossy(&class_buf[..len as usize]);
    if !class_name.eq_ignore_ascii_case("CabinetWClass")
        && !class_name.eq_ignore_ascii_case("ExploreWClass")
    {
        return;
    }

    if !is_default_explorer_enabled() {
        return;
    }

    let orig_style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    let is_visible = IsWindowVisible(hwnd).as_bool() || (orig_style & WS_VISIBLE.0 as isize) != 0;
    if event == EVENT_OBJECT_CREATE && !is_visible {
        return;
    }

    let existing_prop = GetPropW(hwnd, HOOK_PROP_NAME);
    if !existing_prop.0.is_null() {
        return;
    }
    let _ = SetPropW(hwnd, HOOK_PROP_NAME, HANDLE(1 as *mut _));

    let mut orig_rect = RECT::default();
    let _ = GetWindowRect(hwnd, &mut orig_rect);
    let orig_ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);

    // Zero-flicker offscreen + transparent + hidden
    let _ = SetWindowPos(
        hwnd,
        HWND::default(),
        -32000,
        -32000,
        0,
        0,
        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE | SWP_HIDEWINDOW,
    );
    SetWindowLongPtrW(
        hwnd,
        GWL_EXSTYLE,
        orig_ex_style | WS_EX_LAYERED.0 as isize | WS_EX_TOOLWINDOW.0 as isize,
    );
    let _ = SetLayeredWindowAttributes(hwnd, windows::Win32::Foundation::COLORREF(0), 0, LWA_ALPHA);
    let _ = ShowWindow(hwnd, SW_HIDE);

    let mut pid: u32 = 0;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));

    let ctx = RedirectContext {
        hwnd_raw: hwnd.0 as isize,
        pid,
        orig_rect,
        orig_ex_style,
        was_visible: is_visible,
    };

    let _ = std::thread::spawn(move || run_redirect_worker(ctx));
}
