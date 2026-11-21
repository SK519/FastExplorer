using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static class ShellFileOperationHelper
    {
        /// <summary>
        /// 指定された HRESULT がアクセス権限不足・昇格要求を示すかを判定
        /// </summary>
        public static bool IsAccessDeniedHResult(int hresult)
        {
            uint hr = (uint)hresult;
            // E_ACCESSDENIED (0x80070005), COPYENGINE_E_REQUIRES_ELEVATION (0x80270001),
            // COPYENGINE_E_USER_CANCELLED ではなく権限関連コードのみを対象とする
            return hr == 0x80070005 ||
                   ((hr & 0xFFFF0000) == 0x80070000 && (hr & 0xFFFF) == 5) ||
                   hr == 0x80270001 || // COPYENGINE_E_REQUIRES_ELEVATION
                   hr == 0x80270002 || // COPYENGINE_E_CANT_REACH_SOURCE (elevation / access)
                   hr == 0x80270016;   // COPYENGINE_E_ACCESS_DENIED_SRC
        }

        /// <summary>
        /// 指定された例外がアクセス権限不足（ERROR_ACCESS_DENIED / E_ACCESSDENIED）によるものかを正確に判定
        /// </summary>
        public static bool IsAccessDeniedException(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return true;
            if (ex is IOException ioe && (ioe.HResult & 0xFFFF) == 5) return true;
            if (ex is COMException cex)
            {
                return IsAccessDeniedHResult(cex.HResult);
            }
            return false;
        }

        /// <summary>
        /// 指定されたディレクトリに一般権限で書き込み可能かどうかを高速判定
        /// </summary>
        public static bool HasWritePermission(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return false;

            try
            {
                string testPath = Path.Combine(directoryPath, $".fe_perm_check_{Guid.NewGuid():N}.tmp");
                using (var fs = new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 5)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 指定されたパスを削除するための通常権限があるかを判定する（不要な UAC 昇格を防止）
        /// </summary>
        public static bool HasDeletePermission(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return true;

            try
            {
                string? parentDir = Path.GetDirectoryName(targetPath.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    if (!HasWritePermission(parentDir))
                    {
                        return false;
                    }
                }

                // ファイル自身に DELETE アクセス権があるか確認
                if (File.Exists(targetPath))
                {
                    using var hFile = Win32Interop.CreateFileW(
                        targetPath,
                        0x00010000 /* DELETE */,
                        Win32Interop.FILE_SHARE_READ | Win32Interop.FILE_SHARE_WRITE | Win32Interop.FILE_SHARE_DELETE,
                        nint.Zero,
                        Win32Interop.OPEN_EXISTING,
                        0,
                        nint.Zero);
                    if (hFile.IsInvalid)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 5) return false; // ERROR_ACCESS_DENIED
                    }
                }
                else if (Directory.Exists(targetPath))
                {
                    using var hDir = Win32Interop.CreateFileW(
                        targetPath,
                        0x00010000 /* DELETE */,
                        Win32Interop.FILE_SHARE_READ | Win32Interop.FILE_SHARE_WRITE | Win32Interop.FILE_SHARE_DELETE,
                        nint.Zero,
                        Win32Interop.OPEN_EXISTING,
                        0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */,
                        nint.Zero);
                    if (hDir.IsInvalid)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 5) return false; // ERROR_ACCESS_DENIED
                    }
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 通常権限で ReadOnly 属性を解除する（.git オブジェクト等での誤アクセス拒否を防止）
        /// </summary>
        public static void TryClearReadOnlyAttributesNormal(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var attr = File.GetAttributes(path);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                    }
                }
                else if (Directory.Exists(path))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true
                    }))
                    {
                        try
                        {
                            var attr = File.GetAttributes(entry);
                            if ((attr & FileAttributes.ReadOnly) != 0)
                            {
                                File.SetAttributes(entry, attr & ~FileAttributes.ReadOnly);
                            }
                        }
                        catch { }
                    }
                    var dirAttr = File.GetAttributes(path);
                    if ((dirAttr & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, dirAttr & ~FileAttributes.ReadOnly);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// COM 操作を専用のバックグラウンド STA スレッド上で非同期実行（UI スレッドをブロックしない）
        /// </summary>
        public static Task<T> RunInStaAsync<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                Win32Interop.OleInitialize(nint.Zero);
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    Win32Interop.OleUninitialize();
                }
            })
            {
                IsBackground = true,
                Name = "ShellFileOpStaThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        /// <summary>
        /// 管理者権限でのコピーまたは移動を実行（FastExplorer.exe ヘッドレス昇格、PowerShell 不使用）
        /// </summary>
        public static async Task<bool> ExecuteCopyOrMoveAsync(
            IEnumerable<string> sourcePaths,
            string destinationDirectory,
            bool isMove,
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                return false;

            var pathList = sourcePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0) return false;

            var res = await ElevatedOperationRunner.RunElevatedAsync(new ElevatedOpRequest
            {
                Action = isMove ? "Move" : "Copy",
                SourcePaths = pathList,
                DestinationPath = destinationDirectory
            });
            return res.Success;
        }

        /// <summary>
        /// 管理者権限で新規アイテム（フォルダーまたはファイル）を作成
        /// </summary>
        public static async Task<bool> ExecuteNewItemAsync(
            string parentDirectory,
            string name,
            bool isDirectory,
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
                return false;

            var res = await ElevatedOperationRunner.RunElevatedAsync(new ElevatedOpRequest
            {
                Action = isDirectory ? "NewFolder" : "NewFile",
                DestinationPath = parentDirectory,
                NewName = name
            });
            return res.Success;
        }

        /// <summary>
        /// 管理者権限でアイテム名を変更
        /// </summary>
        public static async Task<bool> ExecuteRenameAsync(
            string itemPath,
            string newName,
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            if (string.IsNullOrWhiteSpace(itemPath) || (!File.Exists(itemPath) && !Directory.Exists(itemPath)))
                return false;

            string parentDir = Path.GetDirectoryName(itemPath) ?? "";
            var res = await ElevatedOperationRunner.RunElevatedAsync(new ElevatedOpRequest
            {
                Action = "Rename",
                SourcePaths = [itemPath],
                DestinationPath = parentDir,
                NewName = newName
            });
            return res.Success;
        }

        /// <summary>
        /// 通常権限で直接ゴミ箱移動または削除を試行（UI ダイアログなし・高速）
        /// </summary>
        public static (bool Success, bool Aborted, bool IsAccessDenied) TryExecuteDeleteDirect(
            IEnumerable<string> paths,
            bool recycle,
            nint ownerHwnd = 0,
            bool elevated = false)
        {
            var validPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (validPaths.Count == 0) return (true, false, false);

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;
            return TryExecuteDeleteNormal(validPaths, recycle, ownerHwnd);
        }

        /// <summary>
        /// 管理者権限で削除またはゴミ箱移動を実行（FastExplorer.exe ヘッドレス昇格）
        /// </summary>
        public static async Task<ElevatedOpResponse> ExecuteDeleteWithResultAsync(
            IEnumerable<string> paths,
            bool recycle)
        {
            var validPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (validPaths.Count == 0) return new ElevatedOpResponse { Success = true };

            string parentDir = Path.GetDirectoryName(validPaths[0]) ?? "";
            return await ElevatedOperationRunner.RunElevatedAsync(new ElevatedOpRequest
            {
                Action = recycle ? "DeleteRecycle" : "DeletePermanent",
                SourcePaths = validPaths,
                DestinationPath = parentDir
            });
        }

        public static async Task<bool> ExecuteDeleteAsync(
            IEnumerable<string> paths,
            bool recycle,
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            var res = await ExecuteDeleteWithResultAsync(paths, recycle);
            return res.Success;
        }

        private static (bool Success, bool Aborted, bool IsAccessDenied) TryExecuteDeleteNormal(
            List<string> validPaths,
            bool recycle,
            nint ownerHwnd)
        {
            bool isAccessDenied = false;
            bool aborted = false;

            // 1. NativeCom 直接 vtable 呼び出しによる IFileOperation（Trimming / AOT / Release 安全）
            nint pFileOp = nint.Zero;
            try
            {
                int hrCreate = Win32Interop.CoCreateInstance(
                    in Win32Interop.CLSID_FileOperation,
                    nint.Zero,
                    Win32Interop.CLSCTX_ALL,
                    in Win32Interop.IID_IFileOperation,
                    out pFileOp);

                if (hrCreate == 0 && pFileOp != nint.Zero)
                {
                    // Windows 独自の昇格・エラーポップアップを出さずサイレント実行し、失敗時は FastExplorer 側で判別する
                    uint flags = Win32Interop.FOF_NOCONFIRMATION |
                                 Win32Interop.FOF_SILENT |
                                 Win32Interop.FOF_NOERRORUI;

                    if (recycle)
                    {
                        flags |= Win32Interop.FOFX_RECYCLEONDELETE | Win32Interop.FOF_ALLOWUNDO;
                    }

                    Win32Interop.NativeCom.FileOperation_SetOperationFlags(pFileOp, flags);

                    bool addedAny = false;
                    foreach (var path in validPaths)
                    {
                        int hr = Win32Interop.SHCreateItemFromParsingName(
                            path, 0, in Win32Interop.IID_IShellItem, out nint pItem);
                        if (hr == 0 && pItem != 0)
                        {
                            try
                            {
                                Win32Interop.NativeCom.FileOperation_DeleteItem(pFileOp, pItem, 0);
                                addedAny = true;
                            }
                            finally
                            {
                                Win32Interop.NativeCom.Release(pItem);
                            }
                        }
                    }

                    if (addedAny)
                    {
                        int hrPerform = Win32Interop.NativeCom.FileOperation_PerformOperations(pFileOp);
                        Win32Interop.NativeCom.FileOperation_GetAnyOperationsAborted(pFileOp, out aborted);

                        if (hrPerform < 0 && IsAccessDeniedHResult(hrPerform))
                        {
                            isAccessDenied = true;
                        }

                        bool anyStillExists = validPaths.Any(p => File.Exists(p) || Directory.Exists(p));
                        if (!anyStillExists)
                        {
                            return (true, false, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsAccessDeniedException(ex)) isAccessDenied = true;
                Debug.WriteLine($"[ShellFileOp] Normal IFileOperation delete error (isDenied={isAccessDenied}): {ex.Message}");
            }
            finally
            {
                if (pFileOp != nint.Zero)
                {
                    try { Win32Interop.NativeCom.Release(pFileOp); } catch { }
                }
            }

            // 2. 残存項目がある場合、ReadOnly 属性を通常権限で解除して SHFileOperationW で通常権限フォールバック
            var remaining = validPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (remaining.Count == 0) return (true, false, false);

            foreach (var path in remaining)
            {
                TryClearReadOnlyAttributesNormal(path);
            }

            try
            {
                string multiPath = string.Join("\0", remaining) + "\0\0";
                ushort shFlags = (ushort)(Win32Interop.FOF_NOCONFIRMATION |
                                          Win32Interop.FOF_SILENT |
                                          Win32Interop.FOF_NOERRORUI);
                if (recycle)
                {
                    shFlags |= Win32Interop.FOF_ALLOWUNDO;
                }

                var op = new Win32Interop.SHFILEOPSTRUCTW
                {
                    hwnd = nint.Zero,
                    wFunc = Win32Interop.FO_DELETE,
                    pFrom = multiPath,
                    pTo = null,
                    fFlags = shFlags,
                    fAnyOperationsAborted = false,
                    hNameMappings = nint.Zero,
                    lpszProgressTitle = null
                };
                int shRes = Win32Interop.SHFileOperationW(ref op);
                if (shRes == 5 || shRes == 0x78 /* DE_ACCESSDENIEDSRC */)
                {
                    isAccessDenied = true;
                }
                if (op.fAnyOperationsAborted) aborted = true;

                bool anyStillExists = remaining.Any(p => File.Exists(p) || Directory.Exists(p));
                if (!anyStillExists)
                {
                    return (true, false, false);
                }
            }
            catch (Exception ex)
            {
                if (IsAccessDeniedException(ex)) isAccessDenied = true;
                Debug.WriteLine($"[ShellFileOp] Normal SHFileOperationW fallback error: {ex.Message}");
            }

            return (false, aborted, isAccessDenied);
        }
    }
}
