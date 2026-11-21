use crate::common::{FA_ERR_CANCELLED, FA_ERR_IO, FA_OK};
use serde::Serialize;
use std::ffi::{c_char, c_void, CString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicI32, AtomicU64, Ordering};
use std::time::{Duration, Instant, UNIX_EPOCH};

#[cfg(windows)]
use std::os::windows::fs::MetadataExt;

const FILE_ATTRIBUTE_HIDDEN: u32 = 0x0002;
const FILE_ATTRIBUTE_SYSTEM: u32 = 0x0004;
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0400;

/// Progress callback for parallel folder size calculation.
/// Return non-zero to cancel.
pub type FaFolderSizeCallback = extern "C" fn(
    total_bytes: u64,
    total_files: u64,
    total_folders: u64,
    user_data: *mut c_void,
) -> i32;

/// Batch streaming callback for parallel subfolder search.
/// Return non-zero to cancel.
pub type FaSearchBatchCallback =
    extern "C" fn(batch_json_utf8: *const c_char, user_data: *mut c_void) -> i32;

#[derive(Serialize)]
pub struct SearchHitDto {
    /// File or directory name.
    pub n: String,
    /// Full path.
    pub p: String,
    /// True if directory.
    pub d: bool,
    /// File size in bytes (0 for directories).
    pub s: u64,
    /// Last modified Unix timestamp (seconds).
    pub m: i64,
    /// Raw Windows file attributes.
    pub a: u32,
}

#[inline]
fn is_cancelled(cancel_flag: usize) -> bool {
    if cancel_flag != 0 {
        let flag = unsafe { &*(cancel_flag as *const AtomicI32) };
        return flag.load(Ordering::Relaxed) != 0;
    }
    false
}

#[inline]
fn metadata_attributes(meta: &std::fs::Metadata) -> u32 {
    #[cfg(windows)]
    {
        meta.file_attributes()
    }
    #[cfg(not(windows))]
    {
        let _ = meta;
        0
    }
}

#[inline]
fn metadata_mtime_secs(meta: &std::fs::Metadata) -> i64 {
    meta.modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

#[inline]
fn is_reparse_or_symlink(meta: &std::fs::Metadata) -> bool {
    if meta.file_type().is_symlink() {
        return true;
    }
    (metadata_attributes(meta) & FILE_ATTRIBUTE_REPARSE_POINT) != 0
}

/// Parallel folder size & item count calculation across one or more paths.
pub fn calc_folder_size(
    paths: &[String],
    progress_cb: Option<FaFolderSizeCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
) -> Result<(u64, u64, u64), (i32, String)> {
    let cancel_addr = cancel_flag as usize;
    let user_addr = user_data as usize;

    let total_bytes = AtomicU64::new(0);
    let total_files = AtomicU64::new(0);
    let total_folders = AtomicU64::new(0);
    let mut last_report = Instant::now();

    let threads = std::thread::available_parallelism()
        .map(|n| n.get().clamp(2, 8))
        .unwrap_or(4);

    for raw in paths {
        if is_cancelled(cancel_addr) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }

        let p = Path::new(raw);
        let meta = match std::fs::symlink_metadata(p) {
            Ok(m) => m,
            Err(_) => continue,
        };

        if meta.is_file() {
            total_bytes.fetch_add(meta.len(), Ordering::Relaxed);
            total_files.fetch_add(1, Ordering::Relaxed);
            continue;
        }

        if !meta.is_dir() {
            continue;
        }

        let walker = jwalk::WalkDir::new(p)
            .skip_hidden(false)
            .follow_links(false)
            .parallelism(jwalk::Parallelism::RayonNewPool(threads))
            .process_read_dir(move |_depth, _path, _state, children| {
                if is_cancelled(cancel_addr) {
                    children.clear();
                    return;
                }
                for child_res in children.iter_mut() {
                    if let Ok(entry) = child_res {
                        if entry.file_type().is_dir() {
                            if let Ok(m) = entry.metadata() {
                                if is_reparse_or_symlink(&m) {
                                    entry.read_children_path = None;
                                }
                            }
                        }
                    }
                }
            });

        for entry_res in walker {
            if is_cancelled(cancel_addr) {
                return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
            }
            let Ok(entry) = entry_res else {
                continue;
            };
            let ft = entry.file_type();
            if ft.is_dir() {
                total_folders.fetch_add(1, Ordering::Relaxed);
            } else if ft.is_file() {
                if let Ok(m) = entry.metadata() {
                    total_bytes.fetch_add(m.len(), Ordering::Relaxed);
                }
                total_files.fetch_add(1, Ordering::Relaxed);
            }

            if let Some(cb) = progress_cb {
                if last_report.elapsed() >= Duration::from_millis(100) {
                    last_report = Instant::now();
                    let ret = cb(
                        total_bytes.load(Ordering::Relaxed),
                        total_files.load(Ordering::Relaxed),
                        total_folders.load(Ordering::Relaxed),
                        user_addr as *mut c_void,
                    );
                    if ret != 0 || is_cancelled(cancel_addr) {
                        return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
                    }
                }
            }
        }
    }

    let b = total_bytes.load(Ordering::Relaxed);
    let f = total_files.load(Ordering::Relaxed);
    let d = total_folders.load(Ordering::Relaxed);
    if let Some(cb) = progress_cb {
        cb(b, f, d, user_addr as *mut c_void);
    }

    let _ = FA_OK;
    Ok((b, f, d))
}

/// Fast case-insensitive match supporting both substring and `*`/`?` wildcards.
fn matches_query(name: &str, query_lower: &str, is_wildcard: bool) -> bool {
    if query_lower.is_empty() {
        return true;
    }
    if !is_wildcard {
        // Fast ASCII path avoids heap allocation for ASCII filenames
        if name.is_ascii() && query_lower.is_ascii() {
            let nb = name.as_bytes();
            let qb = query_lower.as_bytes();
            if qb.len() > nb.len() {
                return false;
            }
            return nb
                .windows(qb.len())
                .any(|w| w.iter().zip(qb).all(|(a, b)| a.to_ascii_lowercase() == *b));
        }
        return name.to_lowercase().contains(query_lower);
    }

    let name_lower = name.to_lowercase();
    wildcard_match(
        &name_lower.chars().collect::<Vec<_>>(),
        &query_lower.chars().collect::<Vec<_>>(),
    )
}

fn wildcard_match(text: &[char], pat: &[char]) -> bool {
    let (mut ti, mut pi) = (0usize, 0usize);
    let mut star_idx: Option<usize> = None;
    let mut match_idx: usize = 0;

    while ti < text.len() {
        if pi < pat.len() && (pat[pi] == '?' || pat[pi] == text[ti]) {
            ti += 1;
            pi += 1;
        } else if pi < pat.len() && pat[pi] == '*' {
            star_idx = Some(pi);
            match_idx = ti;
            pi += 1;
        } else if let Some(si) = star_idx {
            pi = si + 1;
            match_idx += 1;
            ti = match_idx;
        } else {
            return false;
        }
    }
    while pi < pat.len() && pat[pi] == '*' {
        pi += 1;
    }
    pi == pat.len()
}

/// Parallel recursive subfolder search with streaming batched JSON callbacks.
pub fn search_subfolders(
    root_path: &Path,
    keyword: &str,
    show_hidden: bool,
    skip_root_direct: bool,
    batch_cb: FaSearchBatchCallback,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
) -> Result<(), (i32, String)> {
    if !root_path.is_dir() {
        return Err((FA_ERR_IO, "Root path is not a directory".to_string()));
    }

    let cancel_addr = cancel_flag as usize;
    let user_addr = user_data as usize;
    let query_lower = keyword.trim().to_lowercase();
    if query_lower.is_empty() {
        return Ok(());
    }
    let is_wildcard = query_lower.contains('*') || query_lower.contains('?');

    let threads = std::thread::available_parallelism()
        .map(|n| n.get().clamp(2, 8))
        .unwrap_or(4);

    let min_depth = if skip_root_direct { 2 } else { 1 };

    let walker = jwalk::WalkDir::new(root_path)
        .min_depth(min_depth)
        .skip_hidden(false)
        .follow_links(false)
        .parallelism(jwalk::Parallelism::RayonNewPool(threads))
        .process_read_dir(move |_depth, _path, _state, children| {
            if is_cancelled(cancel_addr) {
                children.clear();
                return;
            }
            children.retain(|child_res| {
                let Ok(entry) = child_res else {
                    return false;
                };
                if !show_hidden {
                    if let Ok(m) = entry.metadata() {
                        let attrs = metadata_attributes(&m);
                        if (attrs & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM)) != 0 {
                            return false;
                        }
                    }
                }
                true
            });
            for child_res in children.iter_mut() {
                if let Ok(entry) = child_res {
                    if entry.file_type().is_dir() {
                        let name = entry.file_name().to_string_lossy();
                        if name.eq_ignore_ascii_case("$Recycle.Bin")
                            || name.eq_ignore_ascii_case("System Volume Information")
                        {
                            entry.read_children_path = None;
                            continue;
                        }
                        if let Ok(m) = entry.metadata() {
                            if is_reparse_or_symlink(&m) {
                                entry.read_children_path = None;
                            }
                        }
                    }
                }
            }
        });

    let mut batch: Vec<SearchHitDto> = Vec::with_capacity(64);
    let mut last_flush = Instant::now();

    let flush = |batch: &mut Vec<SearchHitDto>, last_flush: &mut Instant| -> Result<(), (i32, String)> {
        if batch.is_empty() {
            return Ok(());
        }
        if let Ok(json) = serde_json::to_string(batch) {
            if let Ok(cstr) = CString::new(json) {
                let ret = batch_cb(cstr.as_ptr(), user_addr as *mut c_void);
                if ret != 0 || is_cancelled(cancel_addr) {
                    return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
                }
            }
        }
        batch.clear();
        *last_flush = Instant::now();
        Ok(())
    };

    for entry_res in walker {
        if is_cancelled(cancel_addr) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let Ok(entry) = entry_res else {
            continue;
        };

        let name_str = entry.file_name().to_string_lossy();
        if !matches_query(&name_str, &query_lower, is_wildcard) {
            continue;
        }

        let is_dir = entry.file_type().is_dir();
        let (size, mtime, attrs) = match entry.metadata() {
            Ok(m) => (
                if is_dir { 0 } else { m.len() },
                metadata_mtime_secs(&m),
                metadata_attributes(&m),
            ),
            Err(_) => (0, 0, 0),
        };

        let full_path: PathBuf = entry.path();
        batch.push(SearchHitDto {
            n: name_str.into_owned(),
            p: full_path.to_string_lossy().into_owned(),
            d: is_dir,
            s: size,
            m: mtime,
            a: attrs,
        });

        if batch.len() >= 40 || last_flush.elapsed() >= Duration::from_millis(100) {
            flush(&mut batch, &mut last_flush)?;
        }
    }

    flush(&mut batch, &mut last_flush)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_wildcard_and_substring_matching() {
        assert!(matches_query("FastExplorer.csproj", "fastexplorer", false));
        assert!(matches_query("FastExplorer.csproj", "*.csproj", true));
        assert!(matches_query("FastExplorer.csproj", "fast*proj", true));
        assert!(!matches_query("FastExplorer.csproj", "*.rs", true));
        assert!(matches_query("日本語ファイル.txt", "ファイル", false));
        assert!(matches_query("file12.txt", "file??.txt", true));
        assert!(!matches_query("file123.txt", "file??.txt", true));
    }
}
