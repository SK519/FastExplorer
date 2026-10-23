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
        /// 指定された例外がアクセス権限不足（ERROR_ACCESS_DENIED / E_ACCESSDENIED）によるものかを正確に判定
        /// </summary>
        public static bool IsAccessDeniedException(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return true;
            if (ex is IOException ioe && (ioe.HResult & 0xFFFF) == 5) return true;
            if (ex is COMException cex)
            {
                uint hr = (uint)cex.HResult;
                // E_ACCESSDENIED (0x80070005), Win32 ERROR_ACCESS_DENIED (5)
                // COPYENGINE_E_ACCESS_DENIED_SRC (0x80270000), COPYENGINE_E_ACCESS_DENIED_DEST (0x80270001)
                return hr == 0x80070005 || (hr & 0xFFFF) == 5 || hr == 0x80270000 || hr == 0x80270001;
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
                    // 書き込み可能
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 5) // ERROR_ACCESS_DENIED
            {
                return false;
            }
            catch
            {
                // 容量不足などの一時的要因は true 扱いとする
                return true;
            }
        }

        /// <summary>
        /// COM 操作を安全に STA スレッド上で実行
        /// </summary>
        public static async Task<T> RunInStaAsync<T>(Func<T> action)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                return action();
            }

            var tcs = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return await tcs.Task;
        }

        private static Win32Interop.IFileOperation? CreateFileOperation(nint ownerHwnd, bool elevated)
        {
            if (elevated)
            {
                try
                {
                    var bindOpts = new Win32Interop.BIND_OPTS3
                    {
                        cbStruct = (uint)Marshal.SizeOf<Win32Interop.BIND_OPTS3>(),
                        dwClassContext = Win32Interop.CLSCTX_LOCAL_SERVER,
                        hwnd = ownerHwnd
                    };

                    string moniker = $"Elevation:Administrator!new:{Win32Interop.CLSID_FileOperation:B}";
                    int hr = Win32Interop.CoGetObject(moniker, ref bindOpts, in Win32Interop.IID_IFileOperation, out nint ppv);
                    if (hr == 0 && ppv != 0)
                    {
                        var elevatedOp = (Win32Interop.IFileOperation)Marshal.GetObjectForIUnknown(ppv);
                        Marshal.Release(ppv);
                        return elevatedOp;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ShellFileOp] CoGetObject elevation moniker failed: {ex.Message}");
                }
            }

            try
            {
                return (Win32Interop.IFileOperation)new Win32Interop.FileOperationClass();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellFileOp] FileOperationClass creation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Windows シェル (IFileOperation) を使用してコピーまたは移動を実行（権限不足時のみ UAC 昇格をサポート）
        /// </summary>
        public static async Task<bool> ExecuteCopyOrMoveAsync(
            IEnumerable<string> sourcePaths,
            string destinationDirectory,
            bool isMove,
            nint ownerHwnd = 0)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                return false;

            var pathList = sourcePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0) return false;

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            return await RunInStaAsync(() =>
            {
                var (success, aborted, isAccessDenied) = TryExecuteOperation(pathList, destinationDirectory, isMove, ownerHwnd, elevated: false);
                // 明確なアクセス拒否（権限不足）の場合のみ管理者権限昇格を試行
                if (!success && !aborted && isAccessDenied)
                {
                    (success, _, _) = TryExecuteOperation(pathList, destinationDirectory, isMove, ownerHwnd, elevated: true);
                }
                return success;
            });
        }

        private static (bool Success, bool Aborted, bool IsAccessDenied) TryExecuteOperation(
            List<string> pathList,
            string destinationDirectory,
            bool isMove,
            nint ownerHwnd,
            bool elevated)
        {
            var fileOp = CreateFileOperation(ownerHwnd, elevated);
            if (fileOp == null) return (false, false, false);

            try
            {
                uint flags = Win32Interop.FOF_ALLOWUNDO |
                             Win32Interop.FOFX_MOVEACROSSVOLUMES;

                if (elevated)
                {
                    flags |= Win32Interop.FOFX_REQUIREELEVATION;
                }
                fileOp.SetOperationFlags(flags);

                if (ownerHwnd != 0)
                {
                    fileOp.SetOwnerWindow(ownerHwnd);
                }

                int hrDest = Win32Interop.SHCreateItemFromParsingName(
                    destinationDirectory, 0, in Win32Interop.IID_IShellItem, out nint pDestItem);
                if (hrDest != 0 || pDestItem == 0) return (false, false, hrDest == unchecked((int)0x80070005));

                try
                {
                    bool addedAny = false;
                    foreach (var src in pathList)
                    {
                        int hrSrc = Win32Interop.SHCreateItemFromParsingName(
                            src, 0, in Win32Interop.IID_IShellItem, out nint pSrcItem);
                        if (hrSrc == 0 && pSrcItem != 0)
                        {
                            try
                            {
                                if (isMove)
                                {
                                    fileOp.MoveItem(pSrcItem, pDestItem, null!, 0);
                                }
                                else
                                {
                                    fileOp.CopyItem(pSrcItem, pDestItem, null!, 0);
                                }
                                addedAny = true;
                            }
                            finally
                            {
                                Marshal.Release(pSrcItem);
                            }
                        }
                    }

                    if (!addedAny) return (false, false, false);

                    fileOp.PerformOperations();
                    bool aborted = fileOp.GetAnyOperationsAborted();
                    return (!aborted, aborted, false);
                }
                finally
                {
                    Marshal.Release(pDestItem);
                }
            }
            catch (Exception ex)
            {
                bool isDenied = IsAccessDeniedException(ex);
                Debug.WriteLine($"[ShellFileOp] PerformOperations error (elevated={elevated}, isDenied={isDenied}): {ex.Message}");
                return (false, false, isDenied);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(fileOp); } catch { }
            }
        }

        /// <summary>
        /// Windows シェルを使用して新規アイテム（フォルダーまたはファイル）を作成（権限不足時のみ UAC 昇格をサポート）
        /// </summary>
        public static async Task<bool> ExecuteNewItemAsync(
            string parentDirectory,
            string name,
            bool isDirectory,
            nint ownerHwnd = 0)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
                return false;

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            return await RunInStaAsync(() =>
            {
                var (success, aborted, isAccessDenied) = TryExecuteNewItem(parentDirectory, name, isDirectory, ownerHwnd, elevated: false);
                // 明確なアクセス拒否（権限不足）の場合のみ管理者権限昇格を試行
                if (!success && !aborted && isAccessDenied)
                {
                    (success, _, _) = TryExecuteNewItem(parentDirectory, name, isDirectory, ownerHwnd, elevated: true);
                }
                return success;
            });
        }

        private static (bool Success, bool Aborted, bool IsAccessDenied) TryExecuteNewItem(
            string parentDirectory,
            string name,
            bool isDirectory,
            nint ownerHwnd,
            bool elevated)
        {
            var fileOp = CreateFileOperation(ownerHwnd, elevated);
            if (fileOp == null) return (false, false, false);

            try
            {
                uint flags = Win32Interop.FOF_ALLOWUNDO | Win32Interop.FOF_NOCONFIRMATION;
                if (elevated) flags |= Win32Interop.FOFX_REQUIREELEVATION;
                fileOp.SetOperationFlags(flags);

                if (ownerHwnd != 0) fileOp.SetOwnerWindow(ownerHwnd);

                int hr = Win32Interop.SHCreateItemFromParsingName(
                    parentDirectory, 0, in Win32Interop.IID_IShellItem, out nint pParentItem);
                if (hr != 0 || pParentItem == 0) return (false, false, hr == unchecked((int)0x80070005));

                try
                {
                    uint attr = isDirectory ? 0x00000010u /* FILE_ATTRIBUTE_DIRECTORY */ : 0x00000080u /* FILE_ATTRIBUTE_NORMAL */;
                    fileOp.NewItem(pParentItem, attr, name, null!, 0);
                    fileOp.PerformOperations();
                    bool aborted = fileOp.GetAnyOperationsAborted();
                    return (!aborted, aborted, false);
                }
                finally
                {
                    Marshal.Release(pParentItem);
                }
            }
            catch (Exception ex)
            {
                bool isDenied = IsAccessDeniedException(ex);
                Debug.WriteLine($"[ShellFileOp] NewItem error (elevated={elevated}, isDenied={isDenied}): {ex.Message}");
                return (false, false, isDenied);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(fileOp); } catch { }
            }
        }

        /// <summary>
        /// Windows シェルを使用して名前変更を実行（権限不足時のみ UAC 昇格をサポート）
        /// </summary>
        public static async Task<bool> ExecuteRenameAsync(
            string itemPath,
            string newName,
            nint ownerHwnd = 0)
        {
            if (string.IsNullOrWhiteSpace(itemPath) || (!File.Exists(itemPath) && !Directory.Exists(itemPath)))
                return false;

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            return await RunInStaAsync(() =>
            {
                var (success, aborted, isAccessDenied) = TryExecuteRename(itemPath, newName, ownerHwnd, elevated: false);
                // 明確なアクセス拒否（権限不足）の場合のみ管理者権限昇格を試行
                if (!success && !aborted && isAccessDenied)
                {
                    (success, _, _) = TryExecuteRename(itemPath, newName, ownerHwnd, elevated: true);
                }
                return success;
            });
        }

        private static (bool Success, bool Aborted, bool IsAccessDenied) TryExecuteRename(
            string itemPath,
            string newName,
            nint ownerHwnd,
            bool elevated)
        {
            var fileOp = CreateFileOperation(ownerHwnd, elevated);
            if (fileOp == null) return (false, false, false);

            try
            {
                uint flags = Win32Interop.FOF_ALLOWUNDO | Win32Interop.FOF_NOCONFIRMATION;
                if (elevated) flags |= Win32Interop.FOFX_REQUIREELEVATION;
                fileOp.SetOperationFlags(flags);

                if (ownerHwnd != 0) fileOp.SetOwnerWindow(ownerHwnd);

                int hr = Win32Interop.SHCreateItemFromParsingName(
                    itemPath, 0, in Win32Interop.IID_IShellItem, out nint pItem);
                if (hr != 0 || pItem == 0) return (false, false, hr == unchecked((int)0x80070005));

                try
                {
                    fileOp.RenameItem(pItem, newName, 0);
                    fileOp.PerformOperations();
                    bool aborted = fileOp.GetAnyOperationsAborted();
                    return (!aborted, aborted, false);
                }
                finally
                {
                    Marshal.Release(pItem);
                }
            }
            catch (Exception ex)
            {
                bool isDenied = IsAccessDeniedException(ex);
                Debug.WriteLine($"[ShellFileOp] Rename error (elevated={elevated}, isDenied={isDenied}): {ex.Message}");
                return (false, false, isDenied);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(fileOp); } catch { }
            }
        }

        /// <summary>
        /// 通常権限で直接削除またはゴミ箱移動を試行（STA スレッド不要の直接呼び出し可能メソッド）
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
            return TryExecuteDelete(validPaths, recycle, ownerHwnd, elevated);
        }

        /// <summary>
        /// Windows シェルを使用して削除またはゴミ箱移動を実行（権限不足時のみ UAC 昇格をサポート）
        /// </summary>
        public static async Task<bool> ExecuteDeleteAsync(
            IEnumerable<string> paths,
            bool recycle,
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            var validPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (validPaths.Count == 0) return true;

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            return await RunInStaAsync(() =>
            {
                if (forceElevate)
                {
                    var (elevatedSuccess, _, _) = TryExecuteDelete(validPaths, recycle, ownerHwnd, elevated: true);
                    return elevatedSuccess;
                }

                var (success, aborted, isAccessDenied) = TryExecuteDelete(validPaths, recycle, ownerHwnd, elevated: false);
                // 明確なアクセス拒否（権限不足）の場合のみ管理者権限昇格を試行
                if (!success && !aborted && isAccessDenied)
                {
                    (success, _, _) = TryExecuteDelete(validPaths, recycle, ownerHwnd, elevated: true);
                }
                return success;
            });
        }

        private static (bool Success, bool Aborted, bool IsAccessDenied) TryExecuteDelete(
            List<string> validPaths,
            bool recycle,
            nint ownerHwnd,
            bool elevated)
        {
            var fileOp = CreateFileOperation(ownerHwnd, elevated);
            if (fileOp == null) return (false, false, false);

            try
            {
                uint flags = Win32Interop.FOF_NOCONFIRMATION | Win32Interop.FOF_SILENT;
                if (recycle)
                {
                    flags |= Win32Interop.FOFX_RECYCLEONDELETE | Win32Interop.FOF_ALLOWUNDO;
                }
                if (elevated)
                {
                    flags |= Win32Interop.FOFX_REQUIREELEVATION;
                }

                fileOp.SetOperationFlags(flags);

                if (ownerHwnd != 0) fileOp.SetOwnerWindow(ownerHwnd);

                bool addedAny = false;
                foreach (var path in validPaths)
                {
                    int hr = Win32Interop.SHCreateItemFromParsingName(
                        path, 0, in Win32Interop.IID_IShellItem, out nint pItem);
                    if (hr == 0 && pItem != 0)
                    {
                        try
                        {
                            fileOp.DeleteItem(pItem, 0);
                            addedAny = true;
                        }
                        finally
                        {
                            Marshal.Release(pItem);
                        }
                    }
                }

                if (!addedAny) return (false, false, false);

                fileOp.PerformOperations();
                bool aborted = fileOp.GetAnyOperationsAborted();

                // 削除後に項目がまだ残っている場合は成否を確認
                bool anyStillExists = validPaths.Any(p => File.Exists(p) || Directory.Exists(p));
                return (!aborted && !anyStillExists, aborted, false);
            }
            catch (Exception ex)
            {
                bool isDenied = IsAccessDeniedException(ex);
                Debug.WriteLine($"[ShellFileOp] Delete error (elevated={elevated}, isDenied={isDenied}): {ex.Message}");
                return (false, false, isDenied);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(fileOp); } catch { }
            }
        }
    }
}
