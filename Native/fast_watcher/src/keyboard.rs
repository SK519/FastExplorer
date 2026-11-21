use crate::config::{
    is_capslock_blocking_enabled, is_copilot_blocking_enabled, is_default_explorer_enabled,
};
use crate::process::launch_or_activate_fast_explorer;
use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, GetKeyState, SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
    KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_CAPITAL, VK_CONTROL, VK_F23, VK_LWIN, VK_MENU, VK_OEM_ATTN,
    VK_RWIN, VK_SHIFT,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, HHOOK, KBDLLHOOKSTRUCT, LLKHF_EXTENDED, WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN,
    WM_SYSKEYUP,
};

pub static HOOK_HANDLE: AtomicIsize = AtomicIsize::new(0);
static IS_LWIN_DOWN: AtomicBool = AtomicBool::new(false);
static IS_RWIN_DOWN: AtomicBool = AtomicBool::new(false);
static IS_RESETTING_CAPSLOCK: AtomicBool = AtomicBool::new(false);

pub fn ensure_capslock_off() {
    unsafe {
        if (GetKeyState(VK_CAPITAL.0 as i32) & 0x0001) != 0 {
            IS_RESETTING_CAPSLOCK.store(true, Ordering::SeqCst);

            let inputs = [
                INPUT {
                    r#type: INPUT_KEYBOARD,
                    Anonymous: INPUT_0 {
                        ki: KEYBDINPUT {
                            wVk: VK_CAPITAL,
                            wScan: 0x3A,
                            dwFlags: Default::default(),
                            time: 0,
                            dwExtraInfo: 0,
                        },
                    },
                },
                INPUT {
                    r#type: INPUT_KEYBOARD,
                    Anonymous: INPUT_0 {
                        ki: KEYBDINPUT {
                            wVk: VK_CAPITAL,
                            wScan: 0x3A,
                            dwFlags: KEYEVENTF_KEYUP,
                            time: 0,
                            dwExtraInfo: 0,
                        },
                    },
                },
            ];

            let _ = SendInput(&inputs, std::mem::size_of::<INPUT>() as i32);
            IS_RESETTING_CAPSLOCK.store(false, Ordering::SeqCst);
        }
    }
}

pub unsafe extern "system" fn low_level_keyboard_proc(
    n_code: i32,
    w_param: WPARAM,
    l_param: LPARAM,
) -> LRESULT {
    let hhook = HHOOK(HOOK_HANDLE.load(Ordering::Relaxed) as *mut _);

    if n_code >= 0 && l_param.0 != 0 {
        let key = &*(l_param.0 as *const KBDLLHOOKSTRUCT);

        if IS_RESETTING_CAPSLOCK.load(Ordering::SeqCst) {
            return CallNextHookEx(hhook, n_code, w_param, l_param);
        }

        let msg = w_param.0 as u32;
        let is_down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        let is_up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        // 1. CapsLock blocking (JIS VK_OEM_ATTN, US VK_CAPITAL, physical scan code 0x3A)
        let is_capslock = key.vkCode == VK_CAPITAL.0 as u32
            || key.vkCode == VK_OEM_ATTN.0 as u32
            || (key.scanCode == 0x3A && (key.flags.0 & LLKHF_EXTENDED.0) == 0);

        if is_capslock && is_capslock_blocking_enabled() {
            ensure_capslock_off();
            return LRESULT(1);
        }

        // 2. Copilot key (Win + Shift + F23) blocking
        if key.vkCode == VK_F23.0 as u32 && is_copilot_blocking_enabled() {
            if is_down {
                let dummy = [INPUT {
                    r#type: INPUT_KEYBOARD,
                    Anonymous: INPUT_0 {
                        ki: KEYBDINPUT {
                            wVk: VIRTUAL_KEY(0xFF),
                            wScan: 0,
                            dwFlags: Default::default(),
                            time: 0,
                            dwExtraInfo: 0,
                        },
                    },
                }];
                let _ = SendInput(&dummy, std::mem::size_of::<INPUT>() as i32);
            }
            return LRESULT(1);
        }

        // 3. Track Win keys & intercept Win + E
        if key.vkCode == VK_LWIN.0 as u32 {
            if is_down {
                IS_LWIN_DOWN.store(true, Ordering::Relaxed);
            } else if is_up {
                IS_LWIN_DOWN.store(false, Ordering::Relaxed);
            }
        } else if key.vkCode == VK_RWIN.0 as u32 {
            if is_down {
                IS_RWIN_DOWN.store(true, Ordering::Relaxed);
            } else if is_up {
                IS_RWIN_DOWN.store(false, Ordering::Relaxed);
            }
        } else if (key.vkCode == b'E' as u32 || key.vkCode == b'e' as u32) && is_down {
            let lwin_phys = (GetAsyncKeyState(VK_LWIN.0 as i32) as u16 & 0x8000) != 0;
            let rwin_phys = (GetAsyncKeyState(VK_RWIN.0 as i32) as u16 & 0x8000) != 0;
            if !lwin_phys {
                IS_LWIN_DOWN.store(false, Ordering::Relaxed);
            }
            if !rwin_phys {
                IS_RWIN_DOWN.store(false, Ordering::Relaxed);
            }

            let is_win = lwin_phys
                || rwin_phys
                || (IS_LWIN_DOWN.load(Ordering::Relaxed) && lwin_phys)
                || (IS_RWIN_DOWN.load(Ordering::Relaxed) && rwin_phys);

            let is_ctrl = (GetAsyncKeyState(VK_CONTROL.0 as i32) as u16 & 0x8000) != 0;
            let is_alt = (GetAsyncKeyState(VK_MENU.0 as i32) as u16 & 0x8000) != 0;
            let is_shift = (GetAsyncKeyState(VK_SHIFT.0 as i32) as u16 & 0x8000) != 0;

            if is_win && !is_ctrl && !is_alt && !is_shift && is_default_explorer_enabled() {
                launch_or_activate_fast_explorer(None);
                return LRESULT(1);
            }
        }
    }

    CallNextHookEx(hhook, n_code, w_param, l_param)
}
