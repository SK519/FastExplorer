using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FastExplorer.Services
{
    public sealed class ElevatedOpRequest
    {
        public string Action { get; set; } = string.Empty; // Copy, Move, NewFolder, NewFile, Rename, DeleteRecycle, DeletePermanent
        public List<string> SourcePaths { get; set; } = new();
        public string DestinationPath { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
        public string ResultFile { get; set; } = string.Empty;
    }

    public sealed class ElevatedOpFailure
    {
        public string Path { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsInUse { get; set; }
    }

    public sealed class ElevatedOpResponse
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public List<ElevatedOpFailure> Failures { get; set; } = new();
    }

    [JsonSerializable(typeof(ElevatedOpRequest))]
    [JsonSerializable(typeof(ElevatedOpResponse))]
    internal partial class ElevatedOpJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// PowerShell を一切起動せず、FastExplorer.exe 自身をヘッドレス管理者ワーカー (--elevated-op) として昇格実行するランナー。
    /// WinExe サブシステムのためコンソールウィンドウのチラつきが完全にゼロ。
    /// </summary>
    public static class ElevatedOperationRunner
    {
        public const string CommandLineSwitch = "--elevated-op";

        /// <summary>
        /// 管理者権限で FastExplorer.exe --elevated-op を起動し、完了を待機して結果を返す。
        /// </summary>
        public static async Task<ElevatedOpResponse> RunElevatedAsync(ElevatedOpRequest request)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FastExplorer", "ElevatedOps");
            Directory.CreateDirectory(tempDir);

            string id = Guid.NewGuid().ToString("N");
            string reqFile = Path.Combine(tempDir, $"req_{id}.json");
            string resFile = Path.Combine(tempDir, $"res_{id}.json");
            request.ResultFile = resFile;

            try
            {
                string json = JsonSerializer.Serialize(request, ElevatedOpJsonContext.Default.ElevatedOpRequest);
                await File.WriteAllTextAsync(reqFile, json, System.Text.Encoding.UTF8);

                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    return new ElevatedOpResponse { Success = false };
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{CommandLineSwitch} \"{reqFile}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return new ElevatedOpResponse { Success = false };
                }

                await proc.WaitForExitAsync();

                if (File.Exists(resFile))
                {
                    string resJson = await File.ReadAllTextAsync(resFile, System.Text.Encoding.UTF8);
                    var parsed = JsonSerializer.Deserialize(resJson, ElevatedOpJsonContext.Default.ElevatedOpResponse);
                    if (parsed != null)
                    {
                        return parsed;
                    }
                }

                return new ElevatedOpResponse { Success = proc.ExitCode == 0 };
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (UAC キャンセル)
            {
                return new ElevatedOpResponse { Success = false, Cancelled = true };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ElevatedOperationRunner] RunElevatedAsync error: {ex.Message}");
                return new ElevatedOpResponse { Success = false };
            }
            finally
            {
                try { if (File.Exists(reqFile)) File.Delete(reqFile); } catch { }
                try { if (File.Exists(resFile)) File.Delete(resFile); } catch { }
            }
        }

        /// <summary>
        /// Program.Main からヘッドレス実行される管理者ワーカー本体。UI を一切起動せず処理完了後に終了コードを返す。
        /// </summary>
        public static int ExecuteHeadlessFromFile(string requestFilePath)
        {
            var response = new ElevatedOpResponse { Success = true };
            ElevatedOpRequest? request = null;

            try
            {
                if (!File.Exists(requestFilePath)) return 1;
                string json = File.ReadAllText(requestFilePath, System.Text.Encoding.UTF8);
                request = JsonSerializer.Deserialize(json, ElevatedOpJsonContext.Default.ElevatedOpRequest);
                if (request == null) return 1;

                switch (request.Action)
                {
                    case "Copy":
                        ExecuteCopyOrMove(request.SourcePaths, request.DestinationPath, isMove: false, response);
                        break;
                    case "Move":
                        ExecuteCopyOrMove(request.SourcePaths, request.DestinationPath, isMove: true, response);
                        break;
                    case "NewFolder":
                        ExecuteNewItem(request.DestinationPath, request.NewName, isDir: true, response);
                        break;
                    case "NewFile":
                        ExecuteNewItem(request.DestinationPath, request.NewName, isDir: false, response);
                        break;
                    case "Rename":
                        ExecuteRename(request.SourcePaths.FirstOrDefault() ?? "", request.NewName, response);
                        break;
                    case "DeleteRecycle":
                        ExecuteDelete(request.SourcePaths, recycle: true, response);
                        break;
                    case "DeletePermanent":
                        ExecuteDelete(request.SourcePaths, recycle: false, response);
                        break;
                    default:
                        response.Success = false;
                        break;
                }

                response.Success = response.Failures.Count == 0 && response.Success;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Failures.Add(new ElevatedOpFailure
                {
                    Path = request?.DestinationPath ?? "",
                    Message = ex.Message,
                    IsInUse = FileLockInspector.IsSharingOrLockViolation(ex)
                });
            }
            finally
            {
                if (request != null && !string.IsNullOrEmpty(request.ResultFile))
                {
                    try
                    {
                        string resJson = JsonSerializer.Serialize(response, ElevatedOpJsonContext.Default.ElevatedOpResponse);
                        File.WriteAllText(request.ResultFile, resJson, System.Text.Encoding.UTF8);
                    }
                    catch { }
                }
            }

            return response.Success ? 0 : 1;
        }

        private static void ExecuteCopyOrMove(List<string> sources, string destDir, bool isMove, ElevatedOpResponse response)
        {
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            foreach (var src in sources)
            {
                try
                {
                    string name = Path.GetFileName(src.TrimEnd('\\', '/'));
                    string destPath = Path.Combine(destDir, name);

                    if (File.Exists(src))
                    {
                        if (isMove)
                        {
                            File.Move(src, destPath, overwrite: true);
                        }
                        else
                        {
                            File.Copy(src, destPath, overwrite: true);
                        }
                    }
                    else if (Directory.Exists(src))
                    {
                        if (isMove && string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(destPath), StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(destPath))
                            {
                                CopyDirectoryOverwrite(src, destPath);
                                Directory.Delete(src, recursive: true);
                            }
                            else
                            {
                                Directory.Move(src, destPath);
                            }
                        }
                        else
                        {
                            CopyDirectoryOverwrite(src, destPath);
                            if (isMove)
                            {
                                Directory.Delete(src, recursive: true);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    response.Failures.Add(new ElevatedOpFailure
                    {
                        Path = src,
                        Message = ex.Message,
                        IsInUse = FileLockInspector.IsSharingOrLockViolation(ex)
                    });
                }
            }
        }

        private static void CopyDirectoryOverwrite(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }
            foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
            {
                string destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectoryOverwrite(subDir, destSub);
            }
        }

        private static void ExecuteNewItem(string parentDir, string name, bool isDir, ElevatedOpResponse response)
        {
            string target = Path.Combine(parentDir, name);
            try
            {
                if (isDir)
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    using var _ = File.Create(target);
                }
            }
            catch (Exception ex)
            {
                response.Failures.Add(new ElevatedOpFailure
                {
                    Path = target,
                    Message = ex.Message,
                    IsInUse = FileLockInspector.IsSharingOrLockViolation(ex)
                });
            }
        }

        private static void ExecuteRename(string srcPath, string newName, ElevatedOpResponse response)
        {
            try
            {
                string parent = Path.GetDirectoryName(srcPath) ?? "";
                string dest = Path.Combine(parent, newName);
                if (Directory.Exists(srcPath))
                {
                    Directory.Move(srcPath, dest);
                }
                else if (File.Exists(srcPath))
                {
                    File.Move(srcPath, dest);
                }
            }
            catch (Exception ex)
            {
                response.Failures.Add(new ElevatedOpFailure
                {
                    Path = srcPath,
                    Message = ex.Message,
                    IsInUse = FileLockInspector.IsSharingOrLockViolation(ex)
                });
            }
        }

        private static void ExecuteDelete(List<string> paths, bool recycle, ElevatedOpResponse response)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path) && !Directory.Exists(path))
                    {
                        continue;
                    }

                    if (recycle)
                    {
                        // すでに管理者トークンで実行中なので、IFileOperation で確実にゴミ箱へ移動する
                        bool recycled = TryRecycleWithIFileOperation(path);

                        if (!recycled && (File.Exists(path) || Directory.Exists(path)))
                        {
                            ClearReadOnlyAttributes(path);
                            recycled = TryRecycleWithIFileOperation(path);
                        }

                        if (!recycled && (File.Exists(path) || Directory.Exists(path)))
                        {
                            // 最終フォールバック: SHFileOperationW (明示的ダブルヌル終端)
                            TryRecycleWithSHFileOperation(path);
                        }

                        if (File.Exists(path) || Directory.Exists(path))
                        {
                            bool inUse = FileLockInspector.IsFileLocked(path) || FileLockInspector.GetLockingProcesses(path).Count > 0;
                            response.Failures.Add(new ElevatedOpFailure
                            {
                                Path = path,
                                Message = inUse
                                    ? "別のプロセスがこのファイルを使用しているため削除できません。"
                                    : "管理者権限でもゴミ箱へ移動できませんでした。",
                                IsInUse = inUse
                            });
                        }
                    }
                    else
                    {
                        ClearReadOnlyAttributes(path);
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        else if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    response.Failures.Add(new ElevatedOpFailure
                    {
                        Path = path,
                        Message = ex.Message,
                        IsInUse = FileLockInspector.IsSharingOrLockViolation(ex) || FileLockInspector.IsFileLocked(path)
                    });
                }
            }
        }

        private static bool TryRecycleWithIFileOperation(string path)
        {
            nint pFileOp = nint.Zero;
            try
            {
                int hrCreate = Core.Win32Interop.CoCreateInstance(
                    in Core.Win32Interop.CLSID_FileOperation,
                    nint.Zero,
                    Core.Win32Interop.CLSCTX_ALL,
                    in Core.Win32Interop.IID_IFileOperation,
                    out pFileOp);
                if (hrCreate != 0 || pFileOp == nint.Zero) return false;

                uint flags = Core.Win32Interop.FOF_NOCONFIRMATION |
                             Core.Win32Interop.FOF_SILENT |
                             Core.Win32Interop.FOF_NOERRORUI |
                             Core.Win32Interop.FOFX_RECYCLEONDELETE |
                             Core.Win32Interop.FOF_ALLOWUNDO;
                Core.Win32Interop.NativeCom.FileOperation_SetOperationFlags(pFileOp, flags);

                int hr = Core.Win32Interop.SHCreateItemFromParsingName(
                    path, 0, in Core.Win32Interop.IID_IShellItem, out nint pItem);
                if (hr != 0 || pItem == 0) return false;

                try
                {
                    Core.Win32Interop.NativeCom.FileOperation_DeleteItem(pFileOp, pItem, 0);
                    Core.Win32Interop.NativeCom.FileOperation_PerformOperations(pFileOp);
                }
                finally
                {
                    Core.Win32Interop.NativeCom.Release(pItem);
                }

                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch
            {
                return !File.Exists(path) && !Directory.Exists(path);
            }
            finally
            {
                if (pFileOp != nint.Zero)
                {
                    try { Core.Win32Interop.NativeCom.Release(pFileOp); } catch { }
                }
            }
        }

        private static bool TryRecycleWithSHFileOperation(string path)
        {
            try
            {
                var op = new Core.Win32Interop.SHFILEOPSTRUCTW
                {
                    hwnd = nint.Zero,
                    wFunc = Core.Win32Interop.FO_DELETE,
                    pFrom = path + "\0\0",
                    pTo = null,
                    fFlags = (ushort)(Core.Win32Interop.FOF_ALLOWUNDO |
                                      Core.Win32Interop.FOF_NOCONFIRMATION |
                                      Core.Win32Interop.FOF_SILENT |
                                      Core.Win32Interop.FOF_NOERRORUI),
                    fAnyOperationsAborted = false,
                    hNameMappings = nint.Zero,
                    lpszProgressTitle = null
                };
                Core.Win32Interop.SHFileOperationW(ref op);
                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch
            {
                return !File.Exists(path) && !Directory.Exists(path);
            }
        }

        private static void ClearReadOnlyAttributes(string path)
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
    }
}
