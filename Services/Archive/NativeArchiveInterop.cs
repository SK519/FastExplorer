using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    internal sealed class NativeArchiveEntryDto
    {
        [JsonPropertyName("k")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("s")]
        public long Size { get; set; }

        [JsonPropertyName("d")]
        public bool IsDirectory { get; set; }

        [JsonPropertyName("m")]
        public long ModifiedUnixSec { get; set; }

        public DateTime? LastModifiedTime =>
            ModifiedUnixSec > 0
                ? DateTimeOffset.FromUnixTimeSeconds(ModifiedUnixSec).LocalDateTime
                : null;
    }

    internal sealed class NativeSearchHitDto
    {
        [JsonPropertyName("n")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("p")]
        public string FullPath { get; set; } = string.Empty;

        [JsonPropertyName("d")]
        public bool IsDirectory { get; set; }

        [JsonPropertyName("s")]
        public long Size { get; set; }

        [JsonPropertyName("m")]
        public long ModifiedUnixSec { get; set; }

        [JsonPropertyName("a")]
        public uint Attributes { get; set; }

        public DateTime LastModifiedTime =>
            ModifiedUnixSec > 0
                ? DateTimeOffset.FromUnixTimeSeconds(ModifiedUnixSec).LocalDateTime
                : DateTime.MinValue;
    }

    [JsonSerializable(typeof(List<NativeArchiveEntryDto>))]
    [JsonSerializable(typeof(List<NativeSearchHitDto>))]
    [JsonSerializable(typeof(List<string>))]
    internal partial class NativeArchiveJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// High-performance Rust native archive engine (fast_archive.dll) P/Invoke bridge.
    /// </summary>
    internal static unsafe partial class NativeArchiveInterop
    {
        private const string DllName = "fast_archive.dll";

        public const int FA_OK = 0;
        public const int FA_ERR_INVALID_ARG = -1;
        public const int FA_ERR_IO = -2;
        public const int FA_ERR_FORMAT = -3;
        public const int FA_ERR_CANCELLED = -4;
        public const int FA_ERR_SIZE_LIMIT = -5;
        public const int FA_ERR_NOT_FOUND = -6;
        public const int FA_ERR_PANIC = -99;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProgressCallbackDelegate(
            long bytesProcessed,
            long totalBytes,
            int filesProcessed,
            int totalFiles,
            nint currentFileUtf8,
            nint userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FolderSizeCallbackDelegate(
            ulong totalBytes,
            ulong totalFiles,
            ulong totalFolders,
            nint userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SearchBatchCallbackDelegate(
            nint batchJsonUtf8,
            nint userData);

        [LibraryImport(DllName)]
        private static partial uint fa_version();

        [LibraryImport(DllName)]
        private static partial void fa_free_string(nint ptr);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_list_entries(
            string archivePath,
            out nint outJson,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_extract_all(
            string archivePath,
            string destDir,
            nint progressCb,
            nint userData,
            int* cancelFlag,
            int* pauseFlag,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_extract_entry(
            string archivePath,
            string entryKey,
            string destFile,
            ulong maxBytes,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_compress_zip(
            string pathsJson,
            string destZip,
            int level,
            nint progressCb,
            nint userData,
            int* cancelFlag,
            int* pauseFlag,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_compress_7z(
            string pathsJson,
            string dest7z,
            int level,
            nint progressCb,
            nint userData,
            int* cancelFlag,
            int* pauseFlag,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_compress_tar(
            string pathsJson,
            string destTar,
            int gzipLevel,
            nint progressCb,
            nint userData,
            int* cancelFlag,
            int* pauseFlag,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_calc_folder_size(
            string pathsJson,
            nint progressCb,
            nint userData,
            int* cancelFlag,
            out ulong outTotalBytes,
            out ulong outTotalFiles,
            out ulong outTotalFolders,
            out nint outError);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int fa_search_subfolders(
            string rootPath,
            string keyword,
            int showHidden,
            int skipRootDirect,
            nint batchCb,
            nint userData,
            int* cancelFlag,
            out nint outError);

        private static int _availabilityState; // 0 = unknown, 1 = available, -1 = unavailable

        public static bool IsAvailable
        {
            get
            {
                int state = Volatile.Read(ref _availabilityState);
                if (state != 0) return state == 1;

                try
                {
                    uint ver = fa_version();
                    bool ok = ver >= 1;
                    Volatile.Write(ref _availabilityState, ok ? 1 : -1);
                    Debug.WriteLine($"[NativeArchiveInterop] fast_archive.dll loaded (v{ver})");
                    return ok;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NativeArchiveInterop] fast_archive.dll unavailable: {ex.Message}");
                    Volatile.Write(ref _availabilityState, -1);
                    return false;
                }
            }
        }

        // Cache of recently listed archives to make virtual folder browsing instant (0ms)
        private sealed record CachedArchiveListing(long Length, long LastWriteTicksUtc, List<NativeArchiveEntryDto> Entries);
        private static readonly ConcurrentDictionary<string, CachedArchiveListing> ListingCache = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxCachedArchives = 8;

        private static string? ConsumeNativeString(nint ptr)
        {
            if (ptr == nint.Zero) return null;
            try
            {
                return Marshal.PtrToStringUTF8(ptr);
            }
            finally
            {
                fa_free_string(ptr);
            }
        }

        public static List<NativeArchiveEntryDto>? ListEntries(string archivePath)
        {
            if (!IsAvailable || !File.Exists(archivePath)) return null;

            try
            {
                var fi = new FileInfo(archivePath);
                long len = fi.Length;
                long ticks = fi.LastWriteTimeUtc.Ticks;

                if (ListingCache.TryGetValue(archivePath, out var cached) &&
                    cached.Length == len &&
                    cached.LastWriteTicksUtc == ticks)
                {
                    return cached.Entries;
                }

                int code = fa_list_entries(archivePath, out nint outJson, out nint outErr);
                string? json = ConsumeNativeString(outJson);
                string? err = ConsumeNativeString(outErr);

                if (code != FA_OK || string.IsNullOrEmpty(json))
                {
                    Debug.WriteLine($"[NativeArchiveInterop.ListEntries] Failed ({code}): {err}");
                    return null;
                }

                var entries = JsonSerializer.Deserialize(json, NativeArchiveJsonContext.Default.ListNativeArchiveEntryDto);
                if (entries != null)
                {
                    if (ListingCache.Count >= MaxCachedArchives)
                    {
                        ListingCache.Clear();
                    }
                    ListingCache[archivePath] = new CachedArchiveListing(len, ticks, entries);
                }
                return entries;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeArchiveInterop.ListEntries] Exception: {ex.Message}");
                return null;
            }
        }

        public static int ExtractAll(
            string archivePath,
            string destDir,
            string customTitle,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            out string? error)
        {
            error = null;
            if (!IsAvailable) return FA_ERR_NOT_FOUND;

            string sourceDir = Path.GetDirectoryName(archivePath) ?? "";
            return RunWithProgress(
                customTitle,
                sourceDir,
                destDir,
                progress,
                controller,
                (cbPtr, cancelPtr, pausePtr, out errPtr) =>
                    fa_extract_all(archivePath, destDir, cbPtr, nint.Zero, cancelPtr, pausePtr, out errPtr),
                out error);
        }

        public static int ExtractEntry(
            string archivePath,
            string entryKey,
            string destFile,
            ulong maxBytes,
            out string? error)
        {
            error = null;
            if (!IsAvailable) return FA_ERR_NOT_FOUND;

            int code = fa_extract_entry(archivePath, entryKey, destFile, maxBytes, out nint outErr);
            error = ConsumeNativeString(outErr);
            return code;
        }

        public static int CompressZip(
            List<string> paths,
            string destZip,
            ArchiveCompressionLevel level,
            string destDir,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            out string? error)
        {
            error = null;
            if (!IsAvailable) return FA_ERR_NOT_FOUND;

            string pathsJson = JsonSerializer.Serialize(paths, NativeArchiveJsonContext.Default.ListString);
            string srcDir = paths.Count > 0 ? (Path.GetDirectoryName(paths[0]) ?? "") : "";
            return RunWithProgress(
                "ZIPに圧縮中",
                srcDir,
                destDir,
                progress,
                controller,
                (cbPtr, cancelPtr, pausePtr, out errPtr) =>
                    fa_compress_zip(pathsJson, destZip, (int)level, cbPtr, nint.Zero, cancelPtr, pausePtr, out errPtr),
                out error);
        }

        public static int Compress7z(
            List<string> paths,
            string dest7z,
            ArchiveCompressionLevel level,
            string destDir,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            out string? error)
        {
            error = null;
            if (!IsAvailable) return FA_ERR_NOT_FOUND;

            string pathsJson = JsonSerializer.Serialize(paths, NativeArchiveJsonContext.Default.ListString);
            string srcDir = paths.Count > 0 ? (Path.GetDirectoryName(paths[0]) ?? "") : "";
            return RunWithProgress(
                "7-Zipに圧縮中",
                srcDir,
                destDir,
                progress,
                controller,
                (cbPtr, cancelPtr, pausePtr, out errPtr) =>
                    fa_compress_7z(pathsJson, dest7z, (int)level, cbPtr, nint.Zero, cancelPtr, pausePtr, out errPtr),
                out error);
        }

        public static int CompressTar(
            List<string> paths,
            string destTar,
            int gzipLevel,
            string title,
            string destDir,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            out string? error)
        {
            error = null;
            if (!IsAvailable) return FA_ERR_NOT_FOUND;

            string pathsJson = JsonSerializer.Serialize(paths, NativeArchiveJsonContext.Default.ListString);
            string srcDir = paths.Count > 0 ? (Path.GetDirectoryName(paths[0]) ?? "") : "";
            return RunWithProgress(
                title,
                srcDir,
                destDir,
                progress,
                controller,
                (cbPtr, cancelPtr, pausePtr, out errPtr) =>
                    fa_compress_tar(pathsJson, destTar, gzipLevel, cbPtr, nint.Zero, cancelPtr, pausePtr, out errPtr),
                out error);
        }

        private delegate int NativeOp(nint cbPtr, int* cancelPtr, int* pausePtr, out nint outErr);

        private static int RunWithProgress(
            string customTitle,
            string sourceDir,
            string destDir,
            IProgress<FileTransferProgress>? progress,
            FileTransferController? controller,
            NativeOp operation,
            out string? error)
        {
            var sw = Stopwatch.StartNew();
            var progressState = new FileTransferProgress
            {
                CustomTitle = customTitle,
                SourceDirectory = sourceDir,
                DestinationDirectory = destDir,
                IsSizeCalculating = false
            };

            int cancelFlag = controller?.IsCancelled == true ? 1 : 0;
            int pauseFlag = controller?.IsPaused == true ? 1 : 0;

            // Pin flags in unmanaged memory so Rust worker threads can safely read them atomically
            nint flagsMem = Marshal.AllocHGlobal(sizeof(int) * 2);
            int* pCancel = (int*)flagsMem;
            int* pPause = (int*)flagsMem + 1;
            Volatile.Write(ref *pCancel, cancelFlag);
            Volatile.Write(ref *pPause, pauseFlag);

            using var cancelReg = controller?.CancellationToken.Register(() => Volatile.Write(ref *pCancel, 1));

            ProgressCallbackDelegate callback = (bytesProcessed, totalBytes, filesProcessed, totalFiles, fileUtf8, _) =>
            {
                if (controller != null)
                {
                    if (controller.IsCancelled)
                    {
                        Volatile.Write(ref *pCancel, 1);
                        return 1;
                    }
                    if (controller.IsPaused)
                    {
                        try
                        {
                            controller.WaitIfPaused();
                        }
                        catch (OperationCanceledException)
                        {
                            Volatile.Write(ref *pCancel, 1);
                            return 1;
                        }
                    }
                    if (controller.IsCancelled)
                    {
                        Volatile.Write(ref *pCancel, 1);
                        return 1;
                    }
                }

                string fileName = fileUtf8 != nint.Zero ? (Marshal.PtrToStringUTF8(fileUtf8) ?? "") : "";
                progressState.CurrentFileName = fileName;
                progressState.TotalBytes = totalBytes;
                progressState.BytesTransferred = bytesProcessed;
                progressState.TotalFiles = Math.Max(1, totalFiles);
                progressState.FilesTransferred = filesProcessed;

                double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                progressState.BytesPerSecond = bytesProcessed / elapsedSec;
                if (progressState.BytesPerSecond > 0 && totalBytes > bytesProcessed)
                {
                    progressState.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - bytesProcessed) / progressState.BytesPerSecond);
                }
                else
                {
                    progressState.EstimatedTimeRemaining = TimeSpan.Zero;
                }

                progress?.Report(progressState);
                return 0;
            };

            nint cbPtr = Marshal.GetFunctionPointerForDelegate(callback);
            try
            {
                int code = operation(cbPtr, pCancel, pPause, out nint errPtr);
                error = ConsumeNativeString(errPtr);
                GC.KeepAlive(callback);
                return code;
            }
            finally
            {
                Marshal.FreeHGlobal(flagsMem);
            }
        }

        public static bool CalcFolderSize(
            IReadOnlyList<string> paths,
            Action<long, long, long>? onProgress,
            CancellationToken ct,
            out long totalBytes,
            out long totalFiles,
            out long totalFolders)
        {
            totalBytes = 0;
            totalFiles = 0;
            totalFolders = 0;
            if (!IsAvailable || paths.Count == 0) return false;

            var list = paths as List<string> ?? new List<string>(paths);
            string pathsJson = JsonSerializer.Serialize(list, NativeArchiveJsonContext.Default.ListString);

            nint flagMem = Marshal.AllocHGlobal(sizeof(int));
            int* pCancel = (int*)flagMem;
            Volatile.Write(ref *pCancel, ct.IsCancellationRequested ? 1 : 0);

            using var reg = ct.Register(() => Volatile.Write(ref *pCancel, 1));

            FolderSizeCallbackDelegate? cb = null;
            nint cbPtr = nint.Zero;
            if (onProgress != null)
            {
                cb = (b, f, d, _) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        Volatile.Write(ref *pCancel, 1);
                        return 1;
                    }
                    onProgress((long)b, (long)f, (long)d);
                    return 0;
                };
                cbPtr = Marshal.GetFunctionPointerForDelegate(cb);
            }

            try
            {
                int code = fa_calc_folder_size(
                    pathsJson,
                    cbPtr,
                    nint.Zero,
                    pCancel,
                    out ulong outB,
                    out ulong outF,
                    out ulong outD,
                    out nint errPtr);
                _ = ConsumeNativeString(errPtr);
                GC.KeepAlive(cb);

                if (code == FA_OK)
                {
                    totalBytes = (long)outB;
                    totalFiles = (long)outF;
                    totalFolders = (long)outD;
                    return true;
                }
                return code == FA_ERR_CANCELLED;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeArchiveInterop.CalcFolderSize] Exception: {ex.Message}");
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(flagMem);
            }
        }

        public static bool SearchSubfolders(
            string rootPath,
            string keyword,
            bool showHidden,
            bool skipRootDirect,
            Action<List<NativeSearchHitDto>> onBatch,
            CancellationToken ct)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(keyword))
                return false;

            nint flagMem = Marshal.AllocHGlobal(sizeof(int));
            int* pCancel = (int*)flagMem;
            Volatile.Write(ref *pCancel, ct.IsCancellationRequested ? 1 : 0);

            using var reg = ct.Register(() => Volatile.Write(ref *pCancel, 1));

            SearchBatchCallbackDelegate cb = (jsonPtr, _) =>
            {
                if (ct.IsCancellationRequested)
                {
                    Volatile.Write(ref *pCancel, 1);
                    return 1;
                }
                if (jsonPtr != nint.Zero)
                {
                    string? json = Marshal.PtrToStringUTF8(jsonPtr);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var batch = JsonSerializer.Deserialize(json, NativeArchiveJsonContext.Default.ListNativeSearchHitDto);
                        if (batch != null && batch.Count > 0)
                        {
                            onBatch(batch);
                        }
                    }
                }
                return ct.IsCancellationRequested ? 1 : 0;
            };

            nint cbPtr = Marshal.GetFunctionPointerForDelegate(cb);
            try
            {
                int code = fa_search_subfolders(
                    rootPath,
                    keyword,
                    showHidden ? 1 : 0,
                    skipRootDirect ? 1 : 0,
                    cbPtr,
                    nint.Zero,
                    pCancel,
                    out nint errPtr);
                string? err = ConsumeNativeString(errPtr);
                GC.KeepAlive(cb);

                if (code != FA_OK && code != FA_ERR_CANCELLED)
                {
                    Debug.WriteLine($"[NativeArchiveInterop.SearchSubfolders] Failed ({code}): {err}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeArchiveInterop.SearchSubfolders] Exception: {ex.Message}");
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(flagMem);
            }
        }
    }
}
