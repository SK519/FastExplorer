use crate::common::{
    collect_source_files, ProgressContext, SourceFile, FA_ERR_CANCELLED, FA_ERR_IO, FA_OK,
};
use std::fs::File;
use std::io::{BufWriter, Read, Write};
use std::path::Path;
use std::sync::atomic::{AtomicI32, AtomicI64, Ordering};

const STREAM_BUF_SIZE: usize = 256 * 1024;

/// Compress files/directories to ZIP.
/// `level`: 0=Store, 1=Fast, 2=Normal, 3=Ultra.
pub fn compress_zip(
    input_paths: &[String],
    dest_zip: &Path,
    level: i32,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let sources = collect_source_files(input_paths);
    let total_bytes: i64 = sources.iter().map(|f| f.size as i64).sum();
    let total_files: i32 = sources.len().max(1) as i32;

    let file = File::create(dest_zip).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    let writer = BufWriter::with_capacity(512 * 1024, file);
    let mut zip = zip::ZipWriter::new(writer);

    let (method, comp_level) = match level {
        0 => (zip::CompressionMethod::Stored, None),
        1 => (zip::CompressionMethod::Deflated, Some(1)),
        3 => (zip::CompressionMethod::Deflated, Some(9)),
        _ => (zip::CompressionMethod::Deflated, Some(6)),
    };

    let mut base_options = zip::write::SimpleFileOptions::default().compression_method(method);
    if let Some(lv) = comp_level {
        base_options = base_options.compression_level(Some(lv));
    }

    let bytes_done = AtomicI64::new(0);
    let files_done = AtomicI32::new(0);

    // Stream all files cleanly with large buffers & zlib-rs SIMD acceleration
    let mut buf = vec![0u8; STREAM_BUF_SIZE];
    for src in &sources {
        if ctx.wait_if_paused() {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let fname = src
            .physical_path
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("");
        let opts = base_options.large_file(src.size >= 0xFFFF_FFFF);
        zip.start_file(&src.entry_name, opts)
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;

        let mut f = File::open(&src.physical_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
        loop {
            if ctx.wait_if_paused() {
                return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
            }
            let n = f.read(&mut buf).map_err(|e| (FA_ERR_IO, e.to_string()))?;
            if n == 0 {
                break;
            }
            zip.write_all(&buf[..n])
                .map_err(|e| (FA_ERR_IO, e.to_string()))?;
            let b = bytes_done.fetch_add(n as i64, Ordering::Relaxed) + n as i64;
            let fd = files_done.load(Ordering::Relaxed);
            if ctx.report(b, total_bytes, fd, total_files, fname, false) {
                return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
            }
        }
        let fd = files_done.fetch_add(1, Ordering::Relaxed) + 1;
        let b = bytes_done.load(Ordering::Relaxed);
        ctx.report(b, total_bytes, fd, total_files, fname, false);
    }

    zip.finish().map_err(|e| (FA_ERR_IO, e.to_string()))?;
    ctx.report(total_bytes, total_bytes, total_files, total_files, "", true);
    let _ = FA_OK;
    Ok(())
}

/// Compress files/directories to 7z (LZMA2).
/// `level`: 0=Store, 1=Fast, 2=Normal, 3=Ultra.
pub fn compress_7z(
    input_paths: &[String],
    dest_7z: &Path,
    level: i32,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let sources = collect_source_files(input_paths);
    let total_bytes: i64 = sources.iter().map(|f| f.size as i64).sum();
    let total_files: i32 = sources.len().max(1) as i32;

    let mut sz = sevenz_rust2::SevenZWriter::create(dest_7z)
        .map_err(|e| (FA_ERR_IO, e.to_string()))?;

    match level {
        0 => {
            sz.set_content_methods(vec![sevenz_rust2::SevenZMethodConfiguration::new(
                sevenz_rust2::SevenZMethod::COPY,
            )]);
        }
        _ => {
            let lzma2_level = match level {
                1 => 1,
                3 => 9,
                _ => 5,
            };
            let options = sevenz_rust2::lzma::LZMA2Options::with_preset(lzma2_level);
            sz.set_content_methods(vec![options.into()]);
        }
    }

    let mut bytes_done: i64 = 0;
    let mut files_done: i32 = 0;

    for src in &sources {
        if ctx.wait_if_paused() {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let fname = src
            .physical_path
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("");
        if ctx.report(bytes_done, total_bytes, files_done, total_files, fname, false) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }

        let entry =
            sevenz_rust2::SevenZArchiveEntry::from_path(&src.physical_path, src.entry_name.clone());
        let file = File::open(&src.physical_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
        sz.push_archive_entry(entry, Some(file))
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;

        bytes_done += src.size as i64;
        files_done += 1;
        if ctx.report(bytes_done, total_bytes, files_done, total_files, fname, false) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
    }

    sz.finish().map_err(|e| (FA_ERR_IO, e.to_string()))?;
    ctx.report(total_bytes, total_bytes, total_files, total_files, "", true);
    Ok(())
}

/// Compress files/directories to TAR (`gzip_level < 0`) or TAR.GZ (`gzip_level >= 0`).
pub fn compress_tar(
    input_paths: &[String],
    dest_path: &Path,
    gzip_level: i32,
    ctx: &ProgressContext,
) -> Result<(), (i32, String)> {
    let sources = collect_source_files(input_paths);
    let total_bytes: i64 = sources.iter().map(|f| f.size as i64).sum();
    let total_files: i32 = sources.len().max(1) as i32;

    let file = File::create(dest_path).map_err(|e| (FA_ERR_IO, e.to_string()))?;
    let buf_writer = BufWriter::with_capacity(512 * 1024, file);

    if gzip_level < 0 {
        write_tar_entries(buf_writer, &sources, total_bytes, total_files, ctx)?;
    } else {
        let comp = match gzip_level {
            0 => flate2::Compression::none(),
            1 => flate2::Compression::fast(),
            3 => flate2::Compression::best(),
            _ => flate2::Compression::default(),
        };
        let gz = flate2::write::GzEncoder::new(buf_writer, comp);
        let gz = write_tar_entries(gz, &sources, total_bytes, total_files, ctx)?;
        gz.finish().map_err(|e| (FA_ERR_IO, e.to_string()))?;
    }

    ctx.report(total_bytes, total_bytes, total_files, total_files, "", true);
    Ok(())
}

fn write_tar_entries<W: Write>(
    writer: W,
    sources: &[SourceFile],
    total_bytes: i64,
    total_files: i32,
    ctx: &ProgressContext,
) -> Result<W, (i32, String)> {
    let mut builder = tar::Builder::new(writer);
    let mut bytes_done: i64 = 0;
    let mut files_done: i32 = 0;

    for src in sources {
        if ctx.wait_if_paused() {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }
        let fname = src
            .physical_path
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("");
        if ctx.report(bytes_done, total_bytes, files_done, total_files, fname, false) {
            return Err((FA_ERR_CANCELLED, "Cancelled".to_string()));
        }

        builder
            .append_path_with_name(&src.physical_path, &src.entry_name)
            .map_err(|e| (FA_ERR_IO, e.to_string()))?;

        bytes_done += src.size as i64;
        files_done += 1;
    }

    builder
        .into_inner()
        .map_err(|e| (FA_ERR_IO, e.to_string()))
}
