using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace FastExplorer.Services
{
    internal static unsafe partial class NativeArchiveInterop
    {
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
