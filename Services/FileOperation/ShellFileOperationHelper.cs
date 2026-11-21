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
            Win32Interop.IFileOperation? fileOp = null;
            try
            {
                fileOp = (Win32Interop.IFileOperation)new Win32Interop.FileOperationClass();

                // Windows 独自の昇格・エラーポップアップを出さずサイレント実行し、失敗時は FastExplorer 側で判別する
                uint flags = Win32Interop.FOF_NOCONFIRMATION |
                             Win32Interop.FOF_SILENT |
                             Win32Interop.FOF_NOERRORUI;

                if (recycle)
                {
                    flags |= Win32Interop.FOFX_RECYCLEONDELETE | Win32Interop.FOF_ALLOWUNDO;
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

                bool anyStillExists = validPaths.Any(p => File.Exists(p) || Directory.Exists(p));
                return (!aborted && !anyStillExists, aborted, false);
            }
            catch (Exception ex)
            {
                bool isDenied = IsAccessDeniedException(ex);
                Debug.WriteLine($"[ShellFileOp] Normal delete error (isDenied={isDenied}): {ex.Message}");
                return (false, false, isDenied);
            }
            finally
            {
                if (fileOp != null)
                {
                    try { Marshal.FinalReleaseComObject(fileOp); } catch { }
                }
            }
        }
    }
}
