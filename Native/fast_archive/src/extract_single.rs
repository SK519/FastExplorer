use crate::common::{
    detect_archive_kind, sanitize_entry_key, ArchiveKind, FA_ERR_FORMAT, FA_ERR_IO,
    FA_ERR_NOT_FOUND, FA_ERR_SIZE_LIMIT,
};
use std::fs::{self, File};
use std::io::{BufWriter, Read, Write};
use std::path::Path;
use std::sync::atomic::Ordering;

/// Extract a single entry to `dest_file` with a strict size limit (`max_bytes`).
pub fn extract_single_entry(
    archive_path: &Path,
    entry_key: &str,
    dest_file: &Path,
    max_bytes: u64,
) -> Result<(), (i32, String)> {
    let target = sanitize_entry_key(entry_key)
        .ok_or_else(|| (FA_ERR_FORMAT, "Invalid entry key".to_string()))?;
    let target_lower = target.to_ascii_lowercase();

    if let Some(parent) = dest_file.parent() {
        fs::create_dir_all(parent).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    }

    match detect_archive_kind(archive_path) {
        ArchiveKind::Zip => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let mut archive =
                zip::ZipArchive::new(f).map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
            for i in 0..archive.len() {
                let mut entry = archive
                    .by_index(i)
                    .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
                if let Some(k) = sanitize_entry_key(entry.name()) {
                    if k.to_ascii_lowercase() == target_lower {
                        if entry.size() > max_bytes {
                            return Err((FA_ERR_SIZE_LIMIT, "Entry exceeds size limit".to_string()));
                        }
                        return copy_limited(&mut entry, dest_file, max_bytes);
                    }
                }
            }
            Err((FA_ERR_NOT_FOUND, "Entry not found".to_string()))
        }
        ArchiveKind::SevenZip => {
            let found = std::sync::atomic::AtomicBool::new(false);
            let limit_hit = std::sync::atomic::AtomicBool::new(false);
            let res = sevenz_rust2::decompress_file_with_extract_fn(
                archive_path,
                dest_file.parent().unwrap_or_else(|| Path::new(".")),
                |entry, reader, _| {
                    if let Some(k) = sanitize_entry_key(&entry.name) {
                        if k.to_ascii_lowercase() == target_lower {
                            if entry.size > max_bytes {
                                limit_hit.store(true, Ordering::Relaxed);
                                return Err(sevenz_rust2::Error::other("Size limit exceeded"));
                            }
                            match copy_limited(reader, dest_file, max_bytes) {
                                Ok(()) => {
                                    found.store(true, Ordering::Relaxed);
                                    return Err(sevenz_rust2::Error::other("FOUND_DONE"));
                                }
                                Err((code, msg)) => {
                                    if code == FA_ERR_SIZE_LIMIT {
                                        limit_hit.store(true, Ordering::Relaxed);
                                    }
                                    return Err(sevenz_rust2::Error::other(msg));
                                }
                            }
                        }
                    }
                    Ok(true)
                },
            );
            if found.load(Ordering::Relaxed) {
                return Ok(());
            }
            if limit_hit.load(Ordering::Relaxed) {
                return Err((FA_ERR_SIZE_LIMIT, "Entry exceeds size limit".to_string()));
            }
            if let Err(e) = res {
                return Err((FA_ERR_FORMAT, e.to_string()));
            }
            Err((FA_ERR_NOT_FOUND, "Entry not found".to_string()))
        }
        ArchiveKind::Rar => {
            let mut archive = unrar::Archive::new(archive_path)
                .open_for_processing()
                .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?;
            while let Some(header) = archive
                .read_header()
                .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?
            {
                let raw_name = header.entry().filename.to_string_lossy().to_string();
                let size = header.entry().unpacked_size;
                if let Some(k) = sanitize_entry_key(&raw_name) {
                    if k.to_ascii_lowercase() == target_lower {
                        if size > max_bytes {
                            return Err((FA_ERR_SIZE_LIMIT, "Entry exceeds size limit".to_string()));
                        }
                        header
                            .extract_to(dest_file)
                            .map_err(|e| (FA_ERR_IO, format!("{:?}", e)))?;
                        return Ok(());
                    }
                }
                archive = header
                    .skip()
                    .map_err(|e| (FA_ERR_FORMAT, format!("{:?}", e)))?;
            }
            Err((FA_ERR_NOT_FOUND, "Entry not found".to_string()))
        }
        ArchiveKind::Tar => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_single(f, &target_lower, dest_file, max_bytes)
        }
        ArchiveKind::TarGz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_single(
                flate2::read::GzDecoder::new(f),
                &target_lower,
                dest_file,
                max_bytes,
            )
        }
        ArchiveKind::TarBz2 => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_single(
                bzip2::read::BzDecoder::new(f),
                &target_lower,
                dest_file,
                max_bytes,
            )
        }
        ArchiveKind::TarXz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            extract_tar_single(
                lzma_rust2::XzReader::new(f, true),
                &target_lower,
                dest_file,
                max_bytes,
            )
        }
        ArchiveKind::TarZst => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let dec = zstd::stream::read::Decoder::new(f)
                .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
            extract_tar_single(dec, &target_lower, dest_file, max_bytes)
        }
        ArchiveKind::Gz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            copy_limited(&mut flate2::read::GzDecoder::new(f), dest_file, max_bytes)
        }
        ArchiveKind::Bz2 => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            copy_limited(&mut bzip2::read::BzDecoder::new(f), dest_file, max_bytes)
        }
        ArchiveKind::Xz => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            copy_limited(
                &mut lzma_rust2::XzReader::new(f, true),
                dest_file,
                max_bytes,
            )
        }
        ArchiveKind::Zst => {
            let f = File::open(archive_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let mut dec = zstd::stream::read::Decoder::new(f)
                .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
            copy_limited(&mut dec, dest_file, max_bytes)
        }
        ArchiveKind::Unknown => Err((FA_ERR_FORMAT, "Unsupported archive format".to_string())),
    }
}

fn extract_tar_single<R: Read>(
    reader: R,
    target_lower: &str,
    dest_file: &Path,
    max_bytes: u64,
) -> Result<(), (i32, String)> {
    let mut archive = tar::Archive::new(reader);
    let entries = archive
        .entries()
        .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
    for item in entries {
        let mut entry = item.map_err(|e| (FA_ERR_FORMAT, e.to_string()))?;
        let raw_path = entry
            .path()
            .map_err(|e| (FA_ERR_FORMAT, e.to_string()))?
            .to_string_lossy()
            .to_string();
        if let Some(k) = sanitize_entry_key(&raw_path) {
            if k.to_ascii_lowercase() == target_lower {
                if entry.header().size().unwrap_or(0) > max_bytes {
                    return Err((FA_ERR_SIZE_LIMIT, "Entry exceeds size limit".to_string()));
                }
                return copy_limited(&mut entry, dest_file, max_bytes);
            }
        }
    }
    Err((FA_ERR_NOT_FOUND, "Entry not found".to_string()))
}

fn copy_limited<R: Read + ?Sized>(
    reader: &mut R,
    dest_file: &Path,
    max_bytes: u64,
) -> Result<(), (i32, String)> {
    let out = File::create(dest_file).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    let mut writer = BufWriter::with_capacity(64 * 1024, out);
    let mut buf = vec![0u8; 64 * 1024];
    let mut total: u64 = 0;
    loop {
        let n = reader
            .read(&mut buf)
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;
        if n == 0 {
            break;
        }
        total += n as u64;
        if total > max_bytes {
            drop(writer);
            let _ = fs::remove_file(dest_file);
            return Err((FA_ERR_SIZE_LIMIT, "Entry exceeds size limit".to_string()));
        }
        writer
            .write_all(&buf[..n])
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;
    }
    writer.flush().map_err(|e| (FA_ERR_IO, e.to_string()))?;
    Ok(())
}
