use crate::common::{
    detect_archive_kind, is_reparse_point, safe_join_dest, ArchiveKind, ProgressContext,
    FA_ERR_CANCELLED, FA_ERR_FORMAT, FA_ERR_IO,
};
pub use crate::extract_single::extract_single_entry;
use rayon::prelude::*;
use std::fs::{self, File};
use std::io::{BufWriter, Read, Write};
use std::path::Path;
use std::sync::atomic::{AtomicI32, AtomicI64, Ordering};

const BUF_SIZE: usize = 256 * 1024;

pub fn extract_all(
    archive_path: &Path,
    dest_dir: &Path,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    fs::create_dir_all(dest_dir).map_err(|e| (FA_ERR_IO, e.to_string()))?;

    match detect_archive_kind(archive_path) {
        ArchiveKind::Zip => extract_zip_all(archive_path, dest_dir, ctx),
        ArchiveKind::SevenZip => extract_7z_all(archive_path, dest_dir, ctx),
        ArchiveKind::Rar => extract_rar_all(archive_path, dest_dir, ctx),
        ArchiveKind::Tar => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_all(f, dest_dir, ctx)
        }
        ArchiveKind::TarGz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_all(flate2::read::GzDecoder::new(f), dest_dir, ctx)
        }
        ArchiveKind::TarBz2 => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_all(bzip2::read::BzDecoder::new(f), dest_dir, ctx)
        }
        ArchiveKind::TarXz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_all(lzma_rust2::XzReader::new(f, true), dest_dir, ctx)
        }
        ArchiveKind::TarZst => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let dec = zstd::stream::read::Decoder::new(f)
                .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
            extract_tar_all(dec, dest_dir, ctx)
        }
        ArchiveKind::Gz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_single_stream(archive_path, flate2::read::GzDecoder::new(f), dest_dir, ctx)
        }
        ArchiveKind::Bz2 => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_single_stream(archive_path, bzip2::read::BzDecoder::new(f), dest_dir, ctx)
        }
        ArchiveKind::Xz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_single_stream(
                archive_path,
                lzma_rust2::XzReader::new(f, true),
                dest_dir,
                ctx,
            )
        }
        ArchiveKind::Zst => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let dec = zstd::stream::read::Decoder::new(f)
                .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
            extract_single_stream(archive_path, dec, dest_dir, ctx)
        }
        ArchiveKind::Unknown => Err((FA_ERR_FORMAT, "Unsupported archive format".to_string())),
    }
}

/// Parallel ZIP extraction using rayon: each thread opens its own file handle for lock-free decompression.
fn extract_zip_all(
    archive_path: &Path,
    dest_dir: &Path,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    let mut archive = zip::ZipArchive::new(f).map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;

    let mut total_bytes: i64 = 0;
    let mut file_indices: Vec<usize> = Vec::with_capacity(archive.len());

    // Pass 1: create directories & collect file indices
    for i in 0..archive.len() {
        let entry = archive
            .by_index_raw(i)
            .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
        let raw_name = entry.name().to_string();
        let is_dir = entry.is_dir() || raw_name.ends_with('/') || raw_name.ends_with('\\');
        let Some(out_path) = safe_join_dest(dest_dir, &raw_name) else {
            continue;
        };

        if is_dir {
            let _ = fs::create_dir_all(&out_path);
        } else {
            if let Some(parent) = out_path.parent() {
                let _ = fs::create_dir_all(parent);
            }
            total_bytes += entry.size() as i64;
            file_indices.push(i);
        }
    }
    drop(archive);

    let total_files = file_indices.len().max(1) as i32;
    let bytes_done = AtomicI64::new(0);
    let files_done = AtomicI32::new(0);

    // Parallel extraction across CPU cores (capped at 8 to avoid thrashing HDD/SATA)
    let pool = rayon::ThreadPoolBuilder::new()
        .num_threads(
            std::thread::available_parallelism()
                .map(|n| n.get().min(8))
                .unwrap_or(4),
        )
        .build()
        .map_err(|e| (FA_ERR_IO, e.to_string()))?;

    let res: Result<(), (i32, String)> = pool.install(|| {
        file_indices.par_chunks(32).try_for_each(|chunk| {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let mut local_zip =
                zip::ZipArchive::new(f).map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
            let mut buf = vec![0u8; BUF_SIZE];

            for &idx in chunk {
                if ctx.wait_if_paused() {
                    return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
                }
                let mut entry = local_zip
                    .by_index(idx)
                    .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
                let raw_name = entry.name().to_string();
                let Some(out_path) = safe_join_dest(dest_dir, &raw_name) else {
                    continue;
                };
                if is_reparse_point(&out_path) {
                    continue;
                }
                if let Some(parent) = out_path.parent() {
                    let _ = fs::create_dir_all(parent);
                }

                let fname = out_path
                    .file_name()
                    .and_then(|s| s.to_str())
                    .unwrap_or("");
                let out_file = File::create(&out_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
                let mut writer = BufWriter::with_capacity(BUF_SIZE, out_file);

                loop {
                    if ctx.wait_if_paused() {
                        return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
                    }
                    let n = entry
                        .read(&mut buf)
                        .map_err(|e| (FA_ERR_IO, e.to_string()))?;
                    if n == 0 {
                        break;
                    }
                    writer
                        .write_all(&buf[..n])
                        .map_err(|e| (FA_ERR_IO, e.to_string()))?;
                    let b = bytes_done.fetch_add(n as i64, Ordering::Relaxed) + n as i64;
                    let fd = files_done.load(Ordering::Relaxed);
                    if ctx.report(b, total_bytes, fd, total_files, fname, false) {
                        return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
                    }
                }
                writer.flush().map_err(|e| (FA_ERR_IO, e.to_string()))?;
                files_done.fetch_add(1, Ordering::Relaxed);
            }
            Ok(())
        })
    });

    res?;
    ctx.report(total_bytes, total_bytes, total_files, total_files, "", true);
    Ok(())
}

fn extract_7z_all(
    archive_path: &Path,
    dest_dir: &Path,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let mut f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    let archive =
        sevenz_rust2::Archive::read(&mut f, &[]).map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;

    let total_bytes: i64 = archive
        .files
        .iter()
        .filter(|e| !e.is_directory)
        .map(|e| e.size as i64)
        .sum();
    let total_files = archive
        .files
        .iter()
        .filter(|e| !e.is_directory)
        .count()
        .max(1) as i32;
    drop(archive);
    drop(f);

    let bytes_done = AtomicI64::new(0);
    let files_done = AtomicI32::new(0);
    let mut buf = vec![0u8; BUF_SIZE];

    let cancelled = std::sync::atomic::AtomicBool::new(false);

    let res = sevenz_rust2::decompress_file_with_extract_fn(
        archive_path,
        dest_dir,
        |entry, reader, _| {
            if ctx.wait_if_paused() {
                cancelled.store(true, Ordering::Relaxed);
                return Err(sevenz_rust2::Error::other("Cancelled"));
            }
            let Some(out_path) = safe_join_dest(dest_dir, &entry.name) else {
                return Ok(true);
            };
            if entry.is_directory {
                let _ = fs::create_dir_all(&out_path);
                return Ok(true);
            }
            if is_reparse_point(&out_path) {
                return Ok(true);
            }
            if let Some(parent) = out_path.parent() {
                let _ = fs::create_dir_all(parent);
            }
            let fname = out_path
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or("");
            let out_file =
                File::create(&out_path).map_err(|e| sevenz_rust2::Error::other(e.to_string()))?;
            let mut writer = BufWriter::with_capacity(BUF_SIZE, out_file);
            loop {
                if ctx.wait_if_paused() {
                    cancelled.store(true, Ordering::Relaxed);
                    return Err(sevenz_rust2::Error::other("Cancelled"));
                }
                let n = reader
                    .read(&mut buf)
                    .map_err(|e| sevenz_rust2::Error::other(e.to_string()))?;
                if n == 0 {
                    break;
                }
                writer
                    .write_all(&buf[..n])
                    .map_err(|e| sevenz_rust2::Error::other(e.to_string()))?;
                let b = bytes_done.fetch_add(n as i64, Ordering::Relaxed) + n as i64;
                let fd = files_done.load(Ordering::Relaxed);
                if ctx.report(b, total_bytes, fd, total_files, fname, false) {
                    cancelled.store(true, Ordering::Relaxed);
                    return Err(sevenz_rust2::Error::other("Cancelled"));
                }
            }
            writer
                .flush()
                .map_err(|e| sevenz_rust2::Error::other(e.to_string()))?;
            files_done.fetch_add(1, Ordering::Relaxed);
            Ok(true)
        },
    );

    if cancelled.load(Ordering::Relaxed) {
        return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
    }
    res.map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
    ctx.report(total_bytes, total_bytes, total_files, total_files, "", true);
    Ok(())
}

fn extract_rar_all(
    archive_path: &Path,
    dest_dir: &Path,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    // Pass 1: count totals
    let mut total_bytes: i64 = 0;
    let mut total_files: i32 = 0;
    if let Ok(mut list_arc) = unrar::Archive::new(archive_path).open_for_listing() {
        while let Ok(Some(h)) = list_arc.read_header() {
            if !h.entry().is_directory() {
                total_bytes += h.entry().unpacked_size as i64;
                total_files += 1;
            }
            match h.skip() {
                Ok(next) => list_arc = next,
                Err(_) => break,
            }
        }
    }
    total_files = total_files.max(1);

    let mut archive = unrar::Archive::new(archive_path)
        .open_for_processing()
        .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?;

    let mut bytes_done: i64 = 0;
    let mut files_done: i32 = 0;

    while let Some(header) = archive
        .read_header()
        .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?
    {
        if ctx.wait_if_paused() {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let raw_name = header.entry().filename.to_string_lossy().to_string();
        let is_dir = header.entry().is_directory();
        let unpacked_size = header.entry().unpacked_size as i64;

        let Some(out_path) = safe_join_dest(dest_dir, &raw_name) else {
            archive = header
                .skip()
                .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?;
            continue;
        };

        if is_dir {
            let _ = fs::create_dir_all(&out_path);
            archive = header
                .skip()
                .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?;
            continue;
        }

        if is_reparse_point(&out_path) {
            archive = header
                .skip()
                .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?;
            continue;
        }

        if let Some(parent) = out_path.parent() {
            let _ = fs::create_dir_all(parent);
        }

        let fname = out_path
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("")
            .to_string();

        archive = header
            .extract_to(&out_path)
            .map_err(|e| (FA_ERR_IO, format!("{:?}", e)))?;

        bytes_done += unpacked_size;
        files_done += 1;
        if ctx.report(bytes_done, total_bytes, files_done, total_files, &fname, false) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
    }

    ctx.report(total_bytes, total_bytes, total_files, total_files, "", true);
    Ok(())
}

fn extract_tar_all<R: Read>(
    reader: R,
    dest_dir: &Path,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let mut archive = tar::Archive::new(reader);
    let entries = archive
        .entries()
        .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;

    let mut buf = vec![0u8; BUF_SIZE];
    let mut bytes_done: i64 = 0;
    let mut files_done: i32 = 0;

    for item in entries {
        if ctx.wait_if_paused() {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let mut entry = item.map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
        let raw_path = entry
            .path()
            .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?
            .to_string_lossy()
            .to_string();
        let is_dir = entry.header().entry_type().is_dir();

        let Some(out_path) = safe_join_dest(dest_dir, &raw_path) else {
            continue;
        };

        if is_dir {
            let _ = fs::create_dir_all(&out_path);
            continue;
        }
        if is_reparse_point(&out_path) {
            continue;
        }
        if let Some(parent) = out_path.parent() {
            let _ = fs::create_dir_all(parent);
        }

        let fname = out_path
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("");
        let out_file = File::create(&out_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
        let mut writer = BufWriter::with_capacity(BUF_SIZE, out_file);

        loop {
            if ctx.wait_if_paused() {
                return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
            }
            let n = entry
                .read(&mut buf)
                .map_err(|e| (FA_ERR_IO, e.to_string()))?;
            if n == 0 {
                break;
            }
            writer
                .write_all(&buf[..n])
                .map_err(|e| (FA_ERR_IO, e.to_string()))?;
            bytes_done += n as i64;
            if ctx.report(bytes_done, 0, files_done, 1, fname, false) {
                return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
            }
        }
        writer.flush().map_err(|e| (FA_ERR_IO, e.to_string()))?;
        files_done += 1;
    }

    ctx.report(bytes_done, bytes_done, files_done, files_done.max(1), "", true);
    Ok(())
}

fn extract_single_stream<R: Read>(
    archive_path: &Path,
    mut reader: R,
    dest_dir: &Path,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let out_name = archive_path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("extracted");
    let out_path = dest_dir.join(out_name);
    let out_file = File::create(&out_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    let mut writer = BufWriter::with_capacity(BUF_SIZE, out_file);
    let mut buf = vec![0u8; BUF_SIZE];
    let mut bytes_done: i64 = 0;

    loop {
        if ctx.wait_if_paused() {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let n = reader
            .read(&mut buf)
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;
        if n == 0 {
            break;
        }
        writer
            .write_all(&buf[..n])
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;
        bytes_done += n as i64;
        if ctx.report(bytes_done, 0, 0, 1, out_name, false) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
    }
    writer.flush().map_err(|e| (FA_ERR_IO, e.to_string()))?;
    ctx.report(bytes_done, bytes_done, 1, 1, out_name, true);
    Ok(())
}
