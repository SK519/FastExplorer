mod common;
mod compress;
mod extract;
mod fs_ops;
mod list;

use common::{
    write_out_string, FaProgressCallback, ProgressContext, FA_ERR_INVALID_ARG, FA_ERR_PANIC, FA_OK,
};
use fs_ops::{FaFolderSizeCallback, FaSearchBatchCallback};
use std::ffi::{c_char, c_void, CStr, CString};
use std::panic::catch_unwind;
use std::path::Path;
use std::sync::atomic::AtomicI32;

const ENGINE_VERSION: u32 = 2;

unsafe fn read_c_str(ptr: *const c_char) -> Result<String, i32> {
    if ptr.is_null() {
        return Err(FA_ERR_INVALID_ARG);
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map(|s| s.to_string())
        .map_err(|_| FA_ERR_INVALID_ARG)
}

#[no_mangle]
pub extern "C" fn fa_version() -> u32 {
    ENGINE_VERSION
}

#[no_mangle]
pub unsafe extern "C" fn fa_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        let _ = CString::from_raw(ptr);
    }
}

#[no_mangle]
pub unsafe extern "C" fn fa_list_entries(
    archive_path: *const c_char,
    out_json: *mut *mut c_char,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if out_json.is_null() {
            return FA_ERR_INVALID_ARG;
        }
        *out_json = std::ptr::null_mut();
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }

        let path_str = match read_c_str(archive_path) {
            Ok(s) => s,
            Err(code) => return code,
        };

        match list::list_archive_entries(Path::new(&path_str)) {
            Ok(entries) => match serde_json::to_string(&entries) {
                Ok(json) => {
                    write_out_string(out_json, &json);
                    FA_OK
                }
                Err(e) => {
                    write_out_string(out_error, &e.to_string());
                    common::FA_ERR_FORMAT
                }
            },
            Err(e) => {
                write_out_string(out_error, &e);
                common::FA_ERR_FORMAT
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_extract_all(
    archive_path: *const c_char,
    dest_dir: *const c_char,
    progress_cb: Option<FaProgressCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
    pause_flag: *const AtomicI32,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let archive_str = match read_c_str(archive_path) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let dest_str = match read_c_str(dest_dir) {
            Ok(s) => s,
            Err(c) => return c,
        };

        let ctx = ProgressContext::new(progress_cb, user_data, cancel_flag, pause_flag);
        match extract::extract_all(Path::new(&archive_str), Path::new(&dest_str), &ctx) {
            Ok(()) => FA_OK,
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_extract_entry(
    archive_path: *const c_char,
    entry_key: *const c_char,
    dest_file: *const c_char,
    max_bytes: u64,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let archive_str = match read_c_str(archive_path) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let key_str = match read_c_str(entry_key) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let dest_str = match read_c_str(dest_file) {
            Ok(s) => s,
            Err(c) => return c,
        };

        match extract::extract_single_entry(
            Path::new(&archive_str),
            &key_str,
            Path::new(&dest_str),
            max_bytes,
        ) {
            Ok(()) => FA_OK,
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_compress_zip(
    paths_json: *const c_char,
    dest_zip: *const c_char,
    level: i32,
    progress_cb: Option<FaProgressCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
    pause_flag: *const AtomicI32,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let json_str = match read_c_str(paths_json) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let dest_str = match read_c_str(dest_zip) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let paths: Vec<String> = match serde_json::from_str(&json_str) {
            Ok(p) => p,
            Err(e) => {
                write_out_string(out_error, &e.to_string());
                return FA_ERR_INVALID_ARG;
            }
        };

        let ctx = ProgressContext::new(progress_cb, user_data, cancel_flag, pause_flag);
        match compress::compress_zip(&paths, Path::new(&dest_str), level, &ctx) {
            Ok(()) => FA_OK,
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_compress_7z(
    paths_json: *const c_char,
    dest_7z: *const c_char,
    level: i32,
    progress_cb: Option<FaProgressCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
    pause_flag: *const AtomicI32,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let json_str = match read_c_str(paths_json) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let dest_str = match read_c_str(dest_7z) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let paths: Vec<String> = match serde_json::from_str(&json_str) {
            Ok(p) => p,
            Err(e) => {
                write_out_string(out_error, &e.to_string());
                return FA_ERR_INVALID_ARG;
            }
        };

        let ctx = ProgressContext::new(progress_cb, user_data, cancel_flag, pause_flag);
        match compress::compress_7z(&paths, Path::new(&dest_str), level, &ctx) {
            Ok(()) => FA_OK,
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_compress_tar(
    paths_json: *const c_char,
    dest_tar: *const c_char,
    gzip_level: i32,
    progress_cb: Option<FaProgressCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
    pause_flag: *const AtomicI32,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let json_str = match read_c_str(paths_json) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let dest_str = match read_c_str(dest_tar) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let paths: Vec<String> = match serde_json::from_str(&json_str) {
            Ok(p) => p,
            Err(e) => {
                write_out_string(out_error, &e.to_string());
                return FA_ERR_INVALID_ARG;
            }
        };

        let ctx = ProgressContext::new(progress_cb, user_data, cancel_flag, pause_flag);
        match compress::compress_tar(&paths, Path::new(&dest_str), gzip_level, &ctx) {
            Ok(()) => FA_OK,
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_calc_folder_size(
    paths_json: *const c_char,
    progress_cb: Option<FaFolderSizeCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
    out_total_bytes: *mut u64,
    out_total_files: *mut u64,
    out_total_folders: *mut u64,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let json_str = match read_c_str(paths_json) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let paths: Vec<String> = match serde_json::from_str(&json_str) {
            Ok(p) => p,
            Err(e) => {
                write_out_string(out_error, &e.to_string());
                return FA_ERR_INVALID_ARG;
            }
        };

        match fs_ops::calc_folder_size(&paths, progress_cb, user_data, cancel_flag) {
            Ok((bytes, files, folders)) => {
                if !out_total_bytes.is_null() {
                    *out_total_bytes = bytes;
                }
                if !out_total_files.is_null() {
                    *out_total_files = files;
                }
                if !out_total_folders.is_null() {
                    *out_total_folders = folders;
                }
                FA_OK
            }
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[no_mangle]
pub unsafe extern "C" fn fa_search_subfolders(
    root_path: *const c_char,
    keyword: *const c_char,
    show_hidden: i32,
    skip_root_direct: i32,
    batch_cb: Option<FaSearchBatchCallback>,
    user_data: *mut c_void,
    cancel_flag: *const AtomicI32,
    out_error: *mut *mut c_char,
) -> i32 {
    let res = catch_unwind(|| {
        if !out_error.is_null() {
            *out_error = std::ptr::null_mut();
        }
        let Some(cb) = batch_cb else {
            return FA_ERR_INVALID_ARG;
        };
        let root_str = match read_c_str(root_path) {
            Ok(s) => s,
            Err(c) => return c,
        };
        let kw_str = match read_c_str(keyword) {
            Ok(s) => s,
            Err(c) => return c,
        };

        match fs_ops::search_subfolders(
            Path::new(&root_str),
            &kw_str,
            show_hidden != 0,
            skip_root_direct != 0,
            cb,
            user_data,
            cancel_flag,
        ) {
            Ok(()) => FA_OK,
            Err((code, msg)) => {
                write_out_string(out_error, &msg);
                code
            }
        }
    });

    res.unwrap_or(FA_ERR_PANIC)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn make_temp_dir(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir()
            .join("fast_archive_tests")
            .join(format!("{}_{}", name, std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn roundtrip_zip_7z_tar_targz_and_security() {
        let root = make_temp_dir("roundtrip");
        let src_dir = root.join("sample_folder");
        fs::create_dir_all(src_dir.join("sub")).unwrap();

        let payload_a = "Hello FastExplorer Rust Engine!\n".repeat(2000);
        let payload_b: Vec<u8> = (0..50_000).map(|i| (i % 251) as u8).collect();
        fs::write(src_dir.join("hello.txt"), &payload_a).unwrap();
        fs::write(src_dir.join("sub").join("data.bin"), &payload_b).unwrap();

        let input_paths = vec![src_dir.to_string_lossy().to_string()];
        let ctx = ProgressContext::new(None, std::ptr::null_mut(), std::ptr::null(), std::ptr::null());

        // 1. ZIP
        let zip_path = root.join("test.zip");
        compress::compress_zip(&input_paths, &zip_path, 2, &ctx).expect("zip compress");
        let zip_entries = list::list_archive_entries(&zip_path).expect("zip list");
        assert!(zip_entries.iter().any(|e| e.k == "sample_folder/hello.txt"));
        assert!(zip_entries.iter().any(|e| e.k == "sample_folder/sub/data.bin"));

        let zip_out = root.join("out_zip");
        extract::extract_all(&zip_path, &zip_out, &ctx).expect("zip extract");
        assert_eq!(
            fs::read_to_string(zip_out.join("sample_folder/hello.txt")).unwrap(),
            payload_a
        );
        assert_eq!(
            fs::read(zip_out.join("sample_folder/sub/data.bin")).unwrap(),
            payload_b
        );

        // Single entry extraction + size limit check
        let single_out = root.join("single_hello.txt");
        extract::extract_single_entry(&zip_path, "sample_folder/hello.txt", &single_out, 10 * 1024 * 1024)
            .expect("single extract");
        assert_eq!(fs::read_to_string(&single_out).unwrap(), payload_a);

        let limit_err = extract::extract_single_entry(&zip_path, "sample_folder/hello.txt", &root.join("too_big.txt"), 100);
        assert!(matches!(limit_err, Err((common::FA_ERR_SIZE_LIMIT, _))));

        // 2. 7z (LZMA2)
        let sz_path = root.join("test.7z");
        compress::compress_7z(&input_paths, &sz_path, 2, &ctx).expect("7z compress");
        let sz_entries = list::list_archive_entries(&sz_path).expect("7z list");
        assert!(sz_entries.iter().any(|e| e.k == "sample_folder/hello.txt"));

        let sz_out = root.join("out_7z");
        extract::extract_all(&sz_path, &sz_out, &ctx).expect("7z extract");
        assert_eq!(
            fs::read_to_string(sz_out.join("sample_folder/hello.txt")).unwrap(),
            payload_a
        );
        assert_eq!(
            fs::read(sz_out.join("sample_folder/sub/data.bin")).unwrap(),
            payload_b
        );

        // 3. TAR & TAR.GZ
        let tar_path = root.join("test.tar");
        compress::compress_tar(&input_paths, &tar_path, -1, &ctx).expect("tar compress");
        let tgz_path = root.join("test.tar.gz");
        compress::compress_tar(&input_paths, &tgz_path, 2, &ctx).expect("targz compress");

        let tgz_entries = list::list_archive_entries(&tgz_path).expect("targz list");
        assert!(tgz_entries.iter().any(|e| e.k == "sample_folder/hello.txt"));

        let tgz_out = root.join("out_tgz");
        extract::extract_all(&tgz_path, &tgz_out, &ctx).expect("targz extract");
        assert_eq!(
            fs::read_to_string(tgz_out.join("sample_folder/hello.txt")).unwrap(),
            payload_a
        );

        // 4. Zip Slip sanitization
        assert!(common::safe_join_dest(&root, "../evil.txt").is_none());
        assert!(common::safe_join_dest(&root, "C:\\Windows\\system32\\cmd.exe").is_none());
        assert!(common::safe_join_dest(&root, "a/b/../../c").is_none());

        // 5. Parallel folder size calculation
        let (bytes, files, folders) =
            fs_ops::calc_folder_size(&input_paths, None, std::ptr::null_mut(), std::ptr::null())
                .expect("calc folder size");
        assert_eq!(files, 2);
        assert!(folders >= 2);
        assert_eq!(bytes, (payload_a.len() + payload_b.len()) as u64);

        // 6. Parallel subfolder search
        extern "C" fn collect_cb(json_ptr: *const c_char, ud: *mut c_void) -> i32 {
            let s = unsafe { CStr::from_ptr(json_ptr).to_str().unwrap().to_string() };
            let vec = unsafe { &mut *(ud as *mut Vec<String>) };
            vec.push(s);
            0
        }
        let mut batches: Vec<String> = Vec::new();
        fs_ops::search_subfolders(
            &root,
            "data.bin",
            true,
            true,
            collect_cb,
            (&mut batches as *mut Vec<String>) as *mut c_void,
            std::ptr::null(),
        )
        .expect("search subfolders");
        let combined = batches.join("");
        assert!(combined.contains("data.bin"));
        assert!(!combined.contains("hello.txt"));

        let _ = fs::remove_dir_all(&root);
    }
}
