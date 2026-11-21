use crate::common::{
    detect_archive_kind, sanitize_entry_key, ArchiveEntryDto, ArchiveKind,
};
use std::fs::File;
use std::io::Read;
use std::path::Path;

pub fn list_archive_entries(archive_path: &Path) -> Result<Vec<ArchiveEntryDto>, String> {
    match detect_archive_kind(archive_path) {
        ArchiveKind::Zip => list_zip(archive_path),
        ArchiveKind::SevenZip => list_7z(archive_path),
        ArchiveKind::Rar => list_rar(archive_path),
        ArchiveKind::Tar => {
            let f = File::open(archive_path).map_err(|e| e.to_string())?;
            list_tar_reader(f)
        }
        ArchiveKind::TarGz => {
            let f = File::open(archive_path).map_err(|e| e.to_string())?;
            let dec = flate2::read::GzDecoder::new(f);
            list_tar_reader(dec)
        }
        ArchiveKind::TarBz2 => {
            let f = File::open(archive_path).map_err(|e| e.to_string())?;
            let dec = bzip2::read::BzDecoder::new(f);
            list_tar_reader(dec)
        }
        ArchiveKind::TarXz => {
            let f = File::open(archive_path).map_err(|e| e.to_string())?;
            let dec = lzma_rust2::XzReader::new(f, true);
            list_tar_reader(dec)
        }
        ArchiveKind::TarZst => {
            let f = File::open(archive_path).map_err(|e| e.to_string())?;
            let dec = zstd::stream::read::Decoder::new(f).map_err(|e| e.to_string())?;
            list_tar_reader(dec)
        }
        ArchiveKind::Gz | ArchiveKind::Bz2 | ArchiveKind::Xz | ArchiveKind::Zst => {
            list_single_stream(archive_path)
        }
        ArchiveKind::Unknown => Err("Unsupported archive format".to_string()),
    }
}

fn list_zip(path: &Path) -> Result<Vec<ArchiveEntryDto>, String> {
    let f = File::open(path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
    let mut entries = Vec::with_capacity(archive.len());

    for i in 0..archive.len() {
        let entry = archive.by_index_raw(i).map_err(|e| e.to_string())?;
        let raw_name = entry.name().to_string();
        let is_dir = entry.is_dir() || raw_name.ends_with('/') || raw_name.ends_with('\\');
        let Some(k) = sanitize_entry_key(&raw_name) else {
            continue;
        };
        let m = entry
            .last_modified()
            .and_then(|dt| {
                time::Date::from_calendar_date(
                    dt.year() as i32,
                    time::Month::try_from(dt.month()).ok()?,
                    dt.day(),
                )
                .ok()
                .and_then(|d| {
                    d.with_hms(dt.hour(), dt.minute(), dt.second())
                        .ok()
                        .map(|p| p.assume_utc().unix_timestamp())
                })
            })
            .unwrap_or(0);

        entries.push(ArchiveEntryDto {
            k,
            s: if is_dir { 0 } else { entry.size() },
            d: is_dir,
            m,
        });
    }
    Ok(entries)
}

fn list_7z(path: &Path) -> Result<Vec<ArchiveEntryDto>, String> {
    let mut f = File::open(path).map_err(|e| e.to_string())?;
    let archive = sevenz_rust2::Archive::read(&mut f, &[]).map_err(|e| e.to_string())?;

    let mut entries = Vec::with_capacity(archive.files.len());
    for entry in &archive.files {
        let Some(k) = sanitize_entry_key(&entry.name) else {
            continue;
        };
        let is_dir = entry.is_directory;
        let m = if entry.has_last_modified_date {
            // nt_time FileTime -> unix seconds
            let ft = entry.last_modified_date.to_raw();
            if ft > 116_444_736_000_000_000 {
                ((ft - 116_444_736_000_000_000) / 10_000_000) as i64
            } else {
                0
            }
        } else {
            0
        };
        entries.push(ArchiveEntryDto {
            k,
            s: if is_dir { 0 } else { entry.size },
            d: is_dir,
            m,
        });
    }
    Ok(entries)
}

fn list_rar(path: &Path) -> Result<Vec<ArchiveEntryDto>, String> {
    let mut archive = unrar::Archive::new(path)
        .open_for_listing()
        .map_err(|e| format!("{:?}", e))?;

    let mut entries = Vec::new();
    while let Some(header) = archive.read_header().map_err(|e| format!("{:?}", e))? {
        let entry = header.entry();
        let raw_name = entry.filename.to_string_lossy().to_string();
        let is_dir = entry.is_directory();
        if let Some(k) = sanitize_entry_key(&raw_name) {
            entries.push(ArchiveEntryDto {
                k,
                s: if is_dir { 0 } else { entry.unpacked_size },
                d: is_dir,
                m: entry.file_time as i64,
            });
        }
        archive = header.skip().map_err(|e| format!("{:?}", e))?;
    }
    Ok(entries)
}

fn list_tar_reader<R: Read>(reader: R) -> Result<Vec<ArchiveEntryDto>, String> {
    let mut archive = tar::Archive::new(reader);
    let mut entries = Vec::new();
    let iter = archive.entries().map_err(|e| e.to_string())?;
    for item in iter {
        let entry = item.map_err(|e| e.to_string())?;
        let header = entry.header();
        let raw_path = entry
            .path()
            .map_err(|e| e.to_string())?
            .to_string_lossy()
            .to_string();
        let is_dir = header.entry_type().is_dir();
        let Some(k) = sanitize_entry_key(&raw_path) else {
            continue;
        };
        let size = header.size().unwrap_or(0);
        let m = header.mtime().unwrap_or(0) as i64;
        entries.push(ArchiveEntryDto {
            k,
            s: if is_dir { 0 } else { size },
            d: is_dir,
            m,
        });
    }
    Ok(entries)
}

fn list_single_stream(path: &Path) -> Result<Vec<ArchiveEntryDto>, String> {
    let inner_name = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("file")
        .to_string();
    let meta = std::fs::metadata(path).map_err(|e| e.to_string())?;
    let m = meta
        .modified()
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);

    Ok(vec![ArchiveEntryDto {
        k: inner_name,
        s: meta.len(),
        d: false,
        m,
    }])
}
