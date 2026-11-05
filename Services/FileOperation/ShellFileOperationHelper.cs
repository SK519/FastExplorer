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
        /// Windows シェル (IFileOperation) を使用してコピーまたは移動を実行（UAC 昇格プロンプト完全サポート）
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

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            bool requiresElevation = forceElevate || !HasWritePermission(destinationDirectory);

            return await RunInStaAsync(() =>
            {
                // 1. IFileOperation によるシェル実行 (FOFX_SHOWELEVATIONPROMPT 付きで Windows ネイティブの昇格ダイアログを表示)
                var (success, aborted, isAccessDenied) = TryExecuteOperation(
                    pathList, destinationDirectory, isMove, ownerHwnd, elevated: requiresElevation);

                // 2. 通常実行でアクセス拒否または失敗した場合、昇格モードで再試行
                if (!success && !aborted && !requiresElevation)
                {
                    (success, aborted, isAccessDenied) = TryExecuteOperation(
                        pathList, destinationDirectory, isMove, ownerHwnd, elevated: true);
                }

                // 3. IFileOperation が失敗または完了しなかった場合の管理者権限プロセス (runas) フォールバック
                if (!success && !aborted)
                {
                    bool destStillEmpty = pathList.All(p =>
                    {
                        string target = Path.Combine(destinationDirectory, Path.GetFileName(p));
                        return !File.Exists(target) && !Directory.Exists(target);
                    });

                    if (destStillEmpty)
                    {
                        Debug.WriteLine("[ShellFileOp] IFileOperation did not complete, falling back to elevated process.");
                        success = TryExecuteElevatedFallback(pathList, destinationDirectory, isMove);
                    }
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
                             Win32Interop.FOFX_MOVEACROSSVOLUMES |
                             Win32Interop.FOFX_SHOWELEVATIONPROMPT;

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

                    // 実際にファイル・フォルダが転送されたか確認
                    bool anyExists = pathList.Any(p =>
                    {
                        string target = Path.Combine(destinationDirectory, Path.GetFileName(p));
                        return File.Exists(target) || Directory.Exists(target);
                    });

                    return (!aborted && anyExists, aborted, false);
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
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
                return false;

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            bool requiresElevation = forceElevate || !HasWritePermission(parentDirectory);

            return await RunInStaAsync(() =>
            {
                var (success, aborted, isAccessDenied) = TryExecuteNewItem(
                    parentDirectory, name, isDirectory, ownerHwnd, elevated: requiresElevation);

                if (!success && !aborted && !requiresElevation)
                {
                    (success, aborted, isAccessDenied) = TryExecuteNewItem(
                        parentDirectory, name, isDirectory, ownerHwnd, elevated: true);
                }

                if (!success && !aborted)
                {
                    string target = Path.Combine(parentDirectory, name);
                    if (!File.Exists(target) && !Directory.Exists(target))
                    {
                        success = TryExecuteNewItemElevatedFallback(parentDirectory, name, isDirectory);
                    }
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
                uint flags = Win32Interop.FOF_ALLOWUNDO |
                             Win32Interop.FOF_NOCONFIRMATION |
                             Win32Interop.FOFX_SHOWELEVATIONPROMPT;

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

                    string target = Path.Combine(parentDirectory, name);
                    bool created = isDirectory ? Directory.Exists(target) : File.Exists(target);
                    return (!aborted && created, aborted, false);
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
            nint ownerHwnd = 0,
            bool forceElevate = false)
        {
            if (string.IsNullOrWhiteSpace(itemPath) || (!File.Exists(itemPath) && !Directory.Exists(itemPath)))
                return false;

            if (ownerHwnd == 0) ownerHwnd = FileOperationService.DefaultOwnerHwnd;

            string? parentDir = Path.GetDirectoryName(itemPath);
            bool requiresElevation = forceElevate || (parentDir != null && !HasWritePermission(parentDir));

            return await RunInStaAsync(() =>
            {
                var (success, aborted, isAccessDenied) = TryExecuteRename(
                    itemPath, newName, ownerHwnd, elevated: requiresElevation);

                if (!success && !aborted && !requiresElevation)
                {
                    (success, aborted, isAccessDenied) = TryExecuteRename(
                        itemPath, newName, ownerHwnd, elevated: true);
                }

                if (!success && !aborted && parentDir != null)
                {
                    string target = Path.Combine(parentDir, newName);
                    if (!File.Exists(target) && !Directory.Exists(target))
                    {
                        success = TryExecuteRenameElevatedFallback(itemPath, newName);
                    }
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
                uint flags = Win32Interop.FOF_ALLOWUNDO |
                             Win32Interop.FOF_NOCONFIRMATION |
                             Win32Interop.FOFX_SHOWELEVATIONPROMPT;

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

                    string? parentDir = Path.GetDirectoryName(itemPath);
                    bool renamed = parentDir != null && (File.Exists(Path.Combine(parentDir, newName)) || Directory.Exists(Path.Combine(parentDir, newName)));
                    return (!aborted && renamed, aborted, false);
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
                    if (!elevatedSuccess)
                    {
                        elevatedSuccess = TryExecuteDeleteElevatedFallback(validPaths, recycle);
                    }
                    return elevatedSuccess;
                }

                var (success, aborted, isAccessDenied) = TryExecuteDelete(validPaths, recycle, ownerHwnd, elevated: false);
                if (!success && !aborted)
                {
                    (success, _, _) = TryExecuteDelete(validPaths, recycle, ownerHwnd, elevated: true);
                    if (!success)
                    {
                        success = TryExecuteDeleteElevatedFallback(validPaths, recycle);
                    }
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
                uint flags = Win32Interop.FOF_NOCONFIRMATION |
                             Win32Interop.FOF_SILENT |
                             Win32Interop.FOFX_SHOWELEVATIONPROMPT;

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

        #region Elevated Process Fallbacks (runas)

        /// <summary>
        /// UAC 昇格した管理者プロセス (runas) を通じてファイルを確実にコピーまたは移動
        /// </summary>
        private static bool TryExecuteElevatedFallback(List<string> pathList, string destinationDirectory, bool isMove)
        {
            try
            {
                string tempScript = Path.Combine(Path.GetTempPath(), $"fe_elev_{Guid.NewGuid():N}.ps1");
                var lines = new List<string>();
                string escapedDest = destinationDirectory.Replace("'", "''");

                foreach (var src in pathList)
                {
                    string escapedSrc = src.Replace("'", "''");
                    if (isMove)
                    {
                        lines.Add($"Move-Item -LiteralPath '{escapedSrc}' -Destination '{escapedDest}' -Force");
                    }
                    else
                    {
                        lines.Add($"Copy-Item -LiteralPath '{escapedSrc}' -Destination '{escapedDest}' -Recurse -Force");
                    }
                }
                lines.Add($"Remove-Item -LiteralPath '{tempScript.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue");

                File.WriteAllLines(tempScript, lines, System.Text.Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellFileOp] Elevated copy fallback failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// UAC 昇格した管理者プロセス (runas) を通じて新規アイテムを作成
        /// </summary>
        private static bool TryExecuteNewItemElevatedFallback(string parentDirectory, string name, bool isDirectory)
        {
            try
            {
                string tempScript = Path.Combine(Path.GetTempPath(), $"fe_elev_new_{Guid.NewGuid():N}.ps1");
                string targetPath = Path.Combine(parentDirectory, name).Replace("'", "''");
                string itemType = isDirectory ? "Directory" : "File";
                var lines = new List<string>
                {
                    $"New-Item -ItemType {itemType} -Path '{targetPath}' -Force",
                    $"Remove-Item -LiteralPath '{tempScript.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue"
                };

                File.WriteAllLines(tempScript, lines, System.Text.Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return isDirectory ? Directory.Exists(Path.Combine(parentDirectory, name)) : File.Exists(Path.Combine(parentDirectory, name));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellFileOp] Elevated new item fallback failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// UAC 昇格した管理者プロセス (runas) を通じてアイテム名を変更
        /// </summary>
        private static bool TryExecuteRenameElevatedFallback(string itemPath, string newName)
        {
            try
            {
                string tempScript = Path.Combine(Path.GetTempPath(), $"fe_elev_ren_{Guid.NewGuid():N}.ps1");
                string escapedSrc = itemPath.Replace("'", "''");
                string escapedName = newName.Replace("'", "''");
                var lines = new List<string>
                {
                    $"Rename-Item -LiteralPath '{escapedSrc}' -NewName '{escapedName}' -Force",
                    $"Remove-Item -LiteralPath '{tempScript.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue"
                };

                File.WriteAllLines(tempScript, lines, System.Text.Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    string? parent = Path.GetDirectoryName(itemPath);
                    return parent != null && (File.Exists(Path.Combine(parent, newName)) || Directory.Exists(Path.Combine(parent, newName)));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellFileOp] Elevated rename fallback failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// UAC 昇格した管理者プロセス (runas) を通じてアイテムを直接削除
        /// </summary>
        private static bool TryExecuteDeleteElevatedFallback(List<string> paths, bool recycle)
        {
            try
            {
                string tempScript = Path.Combine(Path.GetTempPath(), $"fe_elev_del_{Guid.NewGuid():N}.ps1");
                var lines = new List<string>();

                foreach (var path in paths)
                {
                    string escaped = path.Replace("'", "''");
                    lines.Add($"Remove-Item -LiteralPath '{escaped}' -Recurse -Force -ErrorAction SilentlyContinue");
                }
                lines.Add($"Remove-Item -LiteralPath '{tempScript.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue");

                File.WriteAllLines(tempScript, lines, System.Text.Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScript}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return paths.All(p => !File.Exists(p) && !Directory.Exists(p));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellFileOp] Elevated delete fallback failed: {ex.Message}");
            }
            return false;
        }

        #endregion
    }
}
