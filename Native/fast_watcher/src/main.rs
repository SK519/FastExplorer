#![windows_subsystem = "windows"]

mod config;
mod keyboard;
mod process;
mod redirect;

use std::sync::atomic::Ordering;
use windows::core::w;
use windows::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HWND};
use windows::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateMutexW, ReleaseMutex};
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent};
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetMessageW, SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx,
    EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, MSG, WH_KEYBOARD_LL, WINEVENT_OUTOFCONTEXT,
    WINEVENT_SKIPOWNPROCESS,
};

fn main() {
    unsafe {
        let Ok(h_mutex) = CreateMutexW(None, true, w!("FastExplorerWatcherMutex_Global")) else {
            return;
        };
        if GetLastError() == ERROR_ALREADY_EXISTS {
            let _ = CloseHandle(h_mutex);
            return;
        }

        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);

        config::clean_disabled_hotkeys();
        if config::is_capslock_blocking_enabled() {
            keyboard::ensure_capslock_off();
        }

        let h_inst = GetModuleHandleW(None).unwrap_or_default();
        let h_kb_hook = SetWindowsHookExW(
            WH_KEYBOARD_LL,
            Some(keyboard::low_level_keyboard_proc),
            h_inst,
            0,
        )
        .ok();
        if let Some(h) = h_kb_hook {
            keyboard::HOOK_HANDLE.store(h.0 as isize, Ordering::Relaxed);
        }

        let h_win_event = SetWinEventHook(
            EVENT_OBJECT_CREATE,
            EVENT_OBJECT_SHOW,
            None,
            Some(redirect::win_event_proc),
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
        );

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, HWND::default(), 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        if !h_win_event.is_invalid() {
            let _ = UnhookWinEvent(h_win_event);
        }
        if let Some(h) = h_kb_hook {
            let _ = UnhookWindowsHookEx(h);
            keyboard::HOOK_HANDLE.store(0, Ordering::Relaxed);
        }

        config::clean_disabled_hotkeys();

        let _ = ReleaseMutex(h_mutex);
        let _ = CloseHandle(h_mutex);
        CoUninitialize();
    }
}
