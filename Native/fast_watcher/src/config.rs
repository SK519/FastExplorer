use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use windows::core::{PCWSTR, w};
use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::{
    RegCloseKey, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW, HKEY,
    HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WRITE, REG_DWORD, REG_SZ, REG_VALUE_TYPE,
};
use windows::Win32::System::SystemInformation::GetTickCount64;

static LAST_REG_CHECK_TICK: AtomicU64 = AtomicU64::new(0);
static CACHED_REPLACE_EXPLORER: AtomicBool = AtomicBool::new(true);
static CACHED_DISABLE_CAPSLOCK: AtomicBool = AtomicBool::new(true);
static CACHED_DISABLE_COPILOT: AtomicBool = AtomicBool::new(true);

fn read_dword(root: HKEY, subkey: PCWSTR, value_name: PCWSTR) -> Option<u32> {
    unsafe {
        let mut hkey = HKEY::default();
        if RegOpenKeyExW(root, subkey, 0, KEY_READ, &mut hkey) != ERROR_SUCCESS {
            return None;
        }
        let mut val: u32 = 0;
        let mut size = std::mem::size_of::<u32>() as u32;
        let mut reg_type = REG_DWORD;
        let res = RegQueryValueExW(
            hkey,
            value_name,
            None,
            Some(&mut reg_type),
            Some(&mut val as *mut u32 as *mut u8),
            Some(&mut size),
        );
        let _ = RegCloseKey(hkey);
        if res == ERROR_SUCCESS {
            Some(val)
        } else {
            None
        }
    }
}

pub fn refresh_registry_flags_if_needed() {
    let now = unsafe { GetTickCount64() };
    let last = LAST_REG_CHECK_TICK.load(Ordering::Relaxed);
    if last != 0 && now.saturating_sub(last) < 1000 {
        return;
    }
    LAST_REG_CHECK_TICK.store(now, Ordering::Relaxed);

    let hkcu_key = w!("Software\\FastExplorer");
    let hklm_key = w!("SOFTWARE\\FastExplorer");

    let replace = read_dword(HKEY_CURRENT_USER, hkcu_key, w!("ReplaceDefaultExplorer"))
        .or_else(|| read_dword(HKEY_LOCAL_MACHINE, hklm_key, w!("ReplaceDefaultExplorer")))
        .map(|v| v != 0)
        .unwrap_or(true);

    let disable_caps = read_dword(HKEY_CURRENT_USER, hkcu_key, w!("DisableCapsLockKey"))
        .map(|v| v != 0)
        .unwrap_or(true);

    let disable_copilot = read_dword(HKEY_CURRENT_USER, hkcu_key, w!("DisableCopilotKey"))
        .map(|v| v != 0)
        .unwrap_or(true);

    CACHED_REPLACE_EXPLORER.store(replace, Ordering::Relaxed);
    CACHED_DISABLE_CAPSLOCK.store(disable_caps, Ordering::Relaxed);
    CACHED_DISABLE_COPILOT.store(disable_copilot, Ordering::Relaxed);
}

#[inline]
pub fn is_default_explorer_enabled() -> bool {
    refresh_registry_flags_if_needed();
    CACHED_REPLACE_EXPLORER.load(Ordering::Relaxed)
}

#[inline]
pub fn is_capslock_blocking_enabled() -> bool {
    refresh_registry_flags_if_needed();
    CACHED_DISABLE_CAPSLOCK.load(Ordering::Relaxed)
}

#[inline]
pub fn is_copilot_blocking_enabled() -> bool {
    refresh_registry_flags_if_needed();
    CACHED_DISABLE_COPILOT.load(Ordering::Relaxed)
}

/// Remove 'E' / 'e' from Explorer's DisabledHotkeys so Win+E isn't blocked by Explorer itself.
pub fn clean_disabled_hotkeys() {
    unsafe {
        let mut hkey = HKEY::default();
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced"),
            0,
            KEY_READ | KEY_WRITE,
            &mut hkey,
        ) != ERROR_SUCCESS
        {
            return;
        }

        let mut buf = [0u16; 256];
        let mut size = (buf.len() * std::mem::size_of::<u16>()) as u32;
        let mut reg_type = REG_VALUE_TYPE(REG_SZ.0);

        if RegQueryValueExW(
            hkey,
            w!("DisabledHotkeys"),
            None,
            Some(&mut reg_type),
            Some(buf.as_mut_ptr() as *mut u8),
            Some(&mut size),
        ) == ERROR_SUCCESS
        {
            let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
            let filtered: Vec<u16> = buf[..len]
                .iter()
                .copied()
                .filter(|&c| c != b'E' as u16 && c != b'e' as u16)
                .collect();

            if filtered.is_empty() {
                let _ = RegDeleteValueW(hkey, w!("DisabledHotkeys"));
            } else {
                let mut wide_z = filtered;
                wide_z.push(0);
                let byte_slice = std::slice::from_raw_parts(
                    wide_z.as_ptr() as *const u8,
                    wide_z.len() * std::mem::size_of::<u16>(),
                );
                let _ = RegSetValueExW(hkey, w!("DisabledHotkeys"), 0, REG_SZ, Some(byte_slice));
            }
        }

        let _ = RegCloseKey(hkey);
    }
}
