using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FastExplorer.Services
{
    public static partial class FileOperationService
    {
        #region Recycle Bin & Deletion

        public sealed class DeleteFailureInfo
        {
            public required string Path { get; init; }
            public required string FileName { get; init; }
            public bool IsInUse { get; init; }
            public List<string> LockingProcesses { get; init; } = new();
            public string Reason { get; init; } = string.Empty;
        }

        public sealed class DeleteOperationResult
        {
            public bool AllSucceeded => Failures.Count == 0 && !Cancelled;
            public bool Cancelled { get; set; }
            public List<DeleteFailureInfo> Failures { get; } = new();
        }

        public static bool MoveToRecycleBin(IEnumerable<string> paths)
        {
            var res = MoveToRecycleBinAsync(paths).GetAwaiter().GetResult();
            return res.AllSucceeded;
        }

        public static async Task<DeleteOperationResult> MoveToRecycleBinAsync(IEnumerable<string> paths)
        {
            var result = new DeleteOperationResult();
            var validPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
            if (validPaths.Count == 0) return result;

            // WSL / ネットワーク UNC パスは Windows ごみ箱非対応のため、通常権限の直接削除を試行して不要な UAC 昇格を防ぐ
            var uncPaths = validPaths.Where(p => p.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)).ToList();
            var localPaths = validPaths.Where(p => !p.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)).ToList();

            if (uncPaths.Count > 0)
            {
                var uncRes = await DeletePermanentlyAsync(uncPaths);
                if (uncRes.Cancelled) result.Cancelled = true;
                result.Failures.AddRange(uncRes.Failures);
            }

            if (localPaths.Count == 0) return result;

            // 1. まず通常権限の IFileOperation (+ SHFileOperationW フォールバック) による高速ゴミ箱移動を試行
            bool normalAccessDenied = false;
            try
            {
                var (success, _, isAccessDenied) = await ShellFileOperationHelper.RunInStaAsync(() =>
                    ShellFileOperationHelper.TryExecuteDeleteDirect(localPaths, recycle: true, DefaultOwnerHwnd, elevated: false));
                normalAccessDenied = isAccessDenied;
                if (success)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                if (ShellFileOperationHelper.IsAccessDeniedException(ex)) normalAccessDenied = true;
                Debug.WriteLine($"Shell delete error: {ex.Message}");
            }

            // 2. 残存しているアイテムを分類（使用中ロック vs 本当の権限不足 vs その他）
            var remaining = localPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            var deniedPaths = new List<string>();

            foreach (var path in remaining)
            {
                // 2a. 使用中ロックを最優先チェック（管理者権限でも消せないため即座に警告対象へ）
                bool isLocked = FileLockInspector.IsFileLocked(path);
                var lockingProcs = isLocked || Directory.Exists(path)
                    ? FileLockInspector.GetLockingProcesses(path)
                    : new List<string>();

                if (isLocked || lockingProcs.Count > 0)
                {
                    result.Failures.Add(new DeleteFailureInfo
                    {
                        Path = path,
                        FileName = Path.GetFileName(path),
                        IsInUse = true,
                        LockingProcesses = lockingProcs,
                        Reason = "別のプロセスがこのファイルを使用しているため削除できません。"
                    });
                    continue;
                }

                // 2b. 本当に権限不足の場合のみ UAC 昇格対象へ（通常権限で削除可能な項目で誤って UAC を出さない）
                bool hasDeletePerm = ShellFileOperationHelper.HasDeletePermission(path);
                if (normalAccessDenied || !hasDeletePerm)
                {
                    deniedPaths.Add(path);
                }
                else
                {
                    // ごみ箱が利用できないボリューム（USB / ごみ箱無効設定 等）の可能性が高いため、UAC は出さずに失敗を通知
                    result.Failures.Add(new DeleteFailureInfo
                    {
                        Path = path,
                        FileName = Path.GetFileName(path),
                        IsInUse = false,
                        Reason = "ごみ箱へ移動できませんでした（対象ドライブでごみ箱が無効か、削除できない状態です）。"
                    });
                }
            }

            // 3. 権限不足の項目のみ管理者権限でゴミ箱移動
            if (deniedPaths.Count > 0)
            {
                var elevRes = await ShellFileOperationHelper.ExecuteDeleteWithResultAsync(deniedPaths, recycle: true);
                if (elevRes.Cancelled)
                {
                    result.Cancelled = true;
                }
                foreach (var f in elevRes.Failures)
                {
                    result.Failures.Add(new DeleteFailureInfo
                    {
                        Path = f.Path,
                        FileName = Path.GetFileName(f.Path),
                        IsInUse = f.IsInUse,
                        LockingProcesses = f.IsInUse ? FileLockInspector.GetLockingProcesses(f.Path) : new List<string>(),
                        Reason = f.Message
                    });
                }
            }

            return result;
        }

        public static void DeletePermanently(IEnumerable<string> paths)
        {
            _ = DeletePermanentlyAsync(paths).GetAwaiter().GetResult();
        }

        public static async Task<DeleteOperationResult> DeletePermanentlyAsync(IEnumerable<string> paths)
        {
            var result = new DeleteOperationResult();
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0) return result;

            var deniedPaths = new List<string>();
            await Task.Run(() =>
            {
                foreach (var path in pathList)
                {
                    try
                    {
                        DeletePathDirect(path);
                    }
                    catch (Exception ex) when (FileLockInspector.IsSharingOrLockViolation(ex))
                    {
                        result.Failures.Add(new DeleteFailureInfo
                        {
                            Path = path,
                            FileName = Path.GetFileName(path),
                            IsInUse = true,
                            LockingProcesses = FileLockInspector.GetLockingProcesses(path),
                            Reason = ex.Message
                        });
                    }
                    catch (Exception ex) when (ShellFileOperationHelper.IsAccessDeniedException(ex))
                    {
                        // ReadOnly 属性（.git オブジェクト等）が原因の UnauthorizedAccessException を通常権限で解除して再試行
                        ShellFileOperationHelper.TryClearReadOnlyAttributesNormal(path);
                        try
                        {
                            DeletePathDirect(path);
                            continue;
                        }
                        catch (Exception retryEx) when (FileLockInspector.IsSharingOrLockViolation(retryEx))
                        {
                            result.Failures.Add(new DeleteFailureInfo
                            {
                                Path = path,
                                FileName = Path.GetFileName(path),
                                IsInUse = true,
                                LockingProcesses = FileLockInspector.GetLockingProcesses(path),
                                Reason = retryEx.Message
                            });
                            continue;
                        }
                        catch (Exception retryEx) when (ShellFileOperationHelper.IsAccessDeniedException(retryEx))
                        {
                            if (FileLockInspector.IsFileLocked(path))
                            {
                                result.Failures.Add(new DeleteFailureInfo
                                {
                                    Path = path,
                                    FileName = Path.GetFileName(path),
                                    IsInUse = true,
                                    LockingProcesses = FileLockInspector.GetLockingProcesses(path),
                                    Reason = "別のプロセスがこのファイルを使用しているため削除できません。"
                                });
                            }
                            else
                            {
                                deniedPaths.Add(path);
                            }
                        }
                        catch (Exception retryEx)
                        {
                            var locking = FileLockInspector.GetLockingProcesses(path);
                            result.Failures.Add(new DeleteFailureInfo
                            {
                                Path = path,
                                FileName = Path.GetFileName(path),
                                IsInUse = locking.Count > 0 || FileLockInspector.IsFileLocked(path),
                                LockingProcesses = locking,
                                Reason = retryEx.Message
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        var locking = FileLockInspector.GetLockingProcesses(path);
                        result.Failures.Add(new DeleteFailureInfo
                        {
                            Path = path,
                            FileName = Path.GetFileName(path),
                            IsInUse = locking.Count > 0 || FileLockInspector.IsFileLocked(path),
                            LockingProcesses = locking,
                            Reason = ex.Message
                        });
                    }
                }
            });

            if (deniedPaths.Count > 0)
            {
                var elevRes = await ShellFileOperationHelper.ExecuteDeleteWithResultAsync(deniedPaths, recycle: false);
                if (elevRes.Cancelled)
                {
                    result.Cancelled = true;
                }
                foreach (var f in elevRes.Failures)
                {
                    result.Failures.Add(new DeleteFailureInfo
                    {
                        Path = f.Path,
                        FileName = Path.GetFileName(f.Path),
                        IsInUse = f.IsInUse,
                        LockingProcesses = f.IsInUse ? FileLockInspector.GetLockingProcesses(f.Path) : new List<string>(),
                        Reason = f.Message
                    });
                }
            }

            return result;
        }

        private static void DeletePathDirect(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        #endregion
    }
}
