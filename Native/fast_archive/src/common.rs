use serde::Serialize;
use std::ffi::{c_char, c_void, CString};
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicI32, Ordering};
use std::time::{Duration, Instant};

/// Progress callback signature matching C# `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]`.
/// Return 0 to continue, non-zero to cancel.
pub type FaProgressCallback = extern "C" fn(
    bytes_processed: i64,
    total_bytes: i64,
    files_processed: i32,
    total_files: i32,
    current_file_utf8: *const c_char,
    user_data: *mut c_void,
) -> i32;

/// Return codes for FFI functions.
pub const FA_OK: i32 = 0;
pub const FA_ERR_INVALID_ARG: i32 = -1;
pub const FA_ERR_IO: i32 = -2;
pub const FA_ERR_FORMAT: i32 = -3;
pub const FA_ERR_CANCELLED: i32 = -4;
pub const FA_ERR_SIZE_LIMIT: i32 = -5;
pub const FA_ERR_NOT_FOUND: i32 = -6;
pub const FA_ERR_PANIC: i32 = -99;

/// Compact JSON entry descriptor returned by `fa_list_entries`.
#[derive(Serialize)]
pub struct ArchiveEntryDto {
    /// Normalized forward-slash relative path inside archive.
    pub k: String,
    /// Uncompressed size in bytes.
    pub s: u64,
    /// True if directory entry.
    pub d: bool,
    /// Last modified time as Unix timestamp (seconds), 0 if unavailable.
    pub m: i64,
}

#[derive(Debug, Clone)]
pub struct SourceFile {
    pub physical_path: PathBuf,
    pub entry_name: String,
    pub size: u64,
}

/// Thread-safe progress + pause + cancel controller shared across worker threads.
pub struct ProgressContext {
    pub cb: Option<FaProgressCallback>,
    pub user_data: usize,
    pub cancel_flag: usize,
    pub pause_flag: usize,
    last_report: std::sync::Mutex<Instant>,
}

impl ProgressContext {
    pub fn new(
        cb: Option<FaProgressCallback>,
        user_data: *mut c_void,
        cancel_flag: *const AtomicI32,
        pause_flag: *const AtomicI32,
    ) -> Self {
        Self {
            cb,
            user_data: user_data as usize,
            cancel_flag: cancel_flag as usize,
            pause_flag: pause_flag as usize,
            last_report: std::sync::Mutex::new(Instant::now() - Duration::from_secs(1)),
        }
    }

    #[inline]
    pub fn is_cancelled(&self) -> bool {
        if self.cancel_flag != 0 {
            let flag = unsafe { &*(self.cancel_flag as *const AtomicI32) };
            if flag.load(Ordering::Relaxed) != 0 {
                return true;
            }
        }
        false
    }

    #[inline]
    pub fn wait_if_paused(&self) -> bool {
        if self.pause_flag != 0 {
            let pflag = unsafe { &*(self.pause_flag as *const AtomicI32) };
            while pflag.load(Ordering::Relaxed) != 0 {
                if self.is_cancelled() {
                    return true;
                }
                std::thread::sleep(Duration::from_millis(25));
            }
        }
        self.is_cancelled()
    }

    /// Report progress (throttled to ~80ms unless `force` is true).
    /// Returns true if cancelled.
    pub fn report(
        &self,
        bytes_processed: i64,
        total_bytes: i64,
        files_processed: i32,
        total_files: i32,
        current_file: &str,
        force: bool,
    ) -> bool {
        if self.wait_if_paused() {
            return true;
        }
        let Some(cb) = self.cb else {
            return false;
        };

        if !force {
            if let Ok(mut guard) = self.last_report.try_lock() {
                if guard.elapsed() < Duration::from_millis(80) {
                    return false;
                }
                *guard = Instant::now();
            } else {
                return false;
            }
        } else if let Ok(mut guard) = self.last_report.lock() {
            *guard = Instant::now();
        }

        let c_name = CString::new(current_file.replace('\0', "")).unwrap_or_default();
        let ret = cb(
            bytes_processed,
            total_bytes,
            files_processed,
            total_files,
            c_name.as_ptr(),
            self.user_data as *mut c_void,
        );
        ret != 0 || self.is_cancelled()
    }
}

/// Supported archive kind detected by extension (including double extensions).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArchiveKind {
    Zip,
    SevenZip,
    Rar,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    TarZst,
    Gz,
    Bz2,
    Xz,
    Zst,
    Unknown,
}

pub fn detect_archive_kind(path: &Path) -> ArchiveKind {
    let name = path
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();

    if name.ends_with(".tar.gz") || name.ends_with(".tgz") {
        ArchiveKind::TarGz
    } else if name.ends_with(".tar.bz2") || name.ends_with(".tbz2") || name.ends_with(".tbz") {
        ArchiveKind::TarBz2
    } else if name.ends_with(".tar.xz") || name.ends_with(".txz") {
        ArchiveKind::TarXz
    } else if name.ends_with(".tar.zst") || name.ends_with(".tzst") {
        ArchiveKind::TarZst
    } else if name.ends_with(".zip") {
        ArchiveKind::Zip
    } else if name.ends_with(".7z") {
        ArchiveKind::SevenZip
    } else if name.ends_with(".rar") {
        ArchiveKind::Rar
    } else if name.ends_with(".tar") {
        ArchiveKind::Tar
    } else if name.ends_with(".gz") {
        ArchiveKind::Gz
    } else if name.ends_with(".bz2") {
        ArchiveKind::Bz2
    } else if name.ends_with(".xz") {
        ArchiveKind::Xz
    } else if name.ends_with(".zst") {
        ArchiveKind::Zst
    } else {
        ArchiveKind::Unknown
    }
}

/// Normalize archive entry key to forward slashes and strip leading slashes.
/// Returns `None` if the entry contains path traversal or drive prefixes.
pub fn sanitize_entry_key(raw: &str) -> Option<String> {
    let normalized = raw.replace('\\', "/");
    let trimmed = normalized.trim_start_matches('/');
    if trimmed.is_empty() {
        return None;
    }
    let mut clean_parts = Vec::new();
    for part in trimmed.split('/') {
        if part.is_empty() || part == "." {
            continue;
        }
        if part == ".." || part.contains(':') || part.contains('\0') {
            return None;
        }
        clean_parts.push(part);
    }
    if clean_parts.is_empty() {
        return None;
    }
    Some(clean_parts.join("/"))
}

/// Safely join `dest_dir` with an archive entry key, preventing Zip Slip.
pub fn safe_join_dest(dest_dir: &Path, raw_key: &str) -> Option<PathBuf> {
    let clean = sanitize_entry_key(raw_key)?;
    let mut out = dest_dir.to_path_buf();
    for seg in clean.split('/') {
        if seg.is_empty() {
            continue;
        }
        let comp_path = Path::new(seg);
        for c in comp_path.components() {
            match c {
                Component::Normal(os) => out.push(os),
                _ => return None,
            }
        }
    }
    Some(out)
}

/// Check if an existing path is a reparse point / symlink on Windows.
pub fn is_reparse_point(path: &Path) -> bool {
    if let Ok(meta) = std::fs::symlink_metadata(path) {
        if meta.file_type().is_symlink() {
            return true;
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0400;
            if (meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT) != 0 {
                return true;
            }
        }
    }
    false
}

/// Collect all files from a list of input files/directories using parallel directory walking (`jwalk`).
pub fn collect_source_files(input_paths: &[String]) -> Vec<SourceFile> {
    let mut results = Vec::new();
    for raw in input_paths {
        let p = Path::new(raw);
        if p.is_file() {
            if let Ok(meta) = std::fs::metadata(p) {
                let name = p
                    .file_name()
                    .and_then(|s| s.to_str())
                    .unwrap_or("file")
                    .to_string();
                results.push(SourceFile {
                    physical_path: p.to_path_buf(),
                    entry_name: name,
                    size: meta.len(),
                });
            }
        } else if p.is_dir() {
            let root_name = p
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or("folder")
                .to_string();

            for entry in jwalk::WalkDir::new(p).skip_hidden(false).into_iter().flatten() {
                if entry.file_type().is_file() {
                    let full = entry.path();
                    if let Ok(rel) = full.strip_prefix(p) {
                        let rel_str = rel.to_string_lossy().replace('\\', "/");
                        let entry_name = if rel_str.is_empty() {
                            root_name.clone()
                        } else {
                            format!("{}/{}", root_name, rel_str)
                        };
                        let size = entry.metadata().map(|m| m.len()).unwrap_or(0);
                        results.push(SourceFile {
                            physical_path: full,
                            entry_name,
                            size,
                        });
                    }
                }
            }
        }
    }
    results
}

/// Helper to write an error message into an FFI out-pointer.
pub unsafe fn write_out_string(out_ptr: *mut *mut c_char, msg: &str) {
    if !out_ptr.is_null() {
        let sanitized = msg.replace('\0', " ");
        if let Ok(cstr) = CString::new(sanitized) {
            *out_ptr = cstr.into_raw();
        }
    }
}
