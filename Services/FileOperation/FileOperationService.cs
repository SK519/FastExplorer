using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Views.Dialogs;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FastExplorer.Services
{
    public static partial class FileOperationService
    {
        private static bool _isCutOperation = false;
        private static readonly HashSet<string> _inAppClipboardPaths = new(StringComparer.OrdinalIgnoreCase);

        public static event Action? ClipboardStateChanged;
        public static nint DefaultOwnerHwnd { get; set; }

        public static bool IsCutOperation => _isCutOperation;
        public static IReadOnlyCollection<string> InAppClipboardPaths => _inAppClipboardPaths;

        public static bool IsPathCut(string path)
        {
            if (!_isCutOperation || string.IsNullOrEmpty(path)) return false;
            return _inAppClipboardPaths.Contains(path);
        }

        public static void CancelCut()
        {
            if (_isCutOperation)
            {
                _isCutOperation = false;
                _inAppClipboardPaths.Clear();
                ClipboardStateChanged?.Invoke();
            }
        }

        public static Func<string, string, Task<(ConflictResolution Resolution, bool ApplyToAll)>>? ConflictResolver { get; set; }

        #region Clipboard (Copy / Cut / Paste)

        public static void CopyFiles(IEnumerable<string> paths)
        {
            SetClipboardFiles(paths, isCut: false);
        }

        public static void CutFiles(IEnumerable<string> paths)
        {
            SetClipboardFiles(paths, isCut: true);
        }

        private static void SetClipboardFiles(IEnumerable<string> paths, bool isCut)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (pathList.Count == 0) return;

            _isCutOperation = isCut;
            _inAppClipboardPaths.Clear();
            _inAppClipboardPaths.UnionWith(pathList);
            ClipboardStateChanged?.Invoke();

            _ = Task.Run(async () =>
            {
                try
                {
                    var package = new DataPackage();
                    package.RequestedOperation = isCut ? DataPackageOperation.Move : DataPackageOperation.Copy;
                    package.SetText(string.Join(Environment.NewLine, pathList));

                    // WinUI 3 DataPackage に StorageItems を非同期設定
                    var storageItems = new List<IStorageItem>(pathList.Count);
                    foreach (var path in pathList)
                    {
                        try
                        {
                            if (File.Exists(path))
                            {
                                var f = await StorageFile.GetFileFromPathAsync(path);
                                storageItems.Add(f);
                            }
                            else if (Directory.Exists(path))
                            {
                                var f = await StorageFolder.GetFolderFromPathAsync(path);
                                storageItems.Add(f);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Clipboard] StorageItem resolve error for {path}: {ex.Message}");
                        }
                    }

                    if (storageItems.Count > 0)
                    {
                        package.SetStorageItems(storageItems);
                    }

                    Clipboard.SetContent(package);
                    Clipboard.Flush();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Clipboard] SetClipboardFiles async error: {ex.Message}");
                }
            });
        }

        public static bool CanPaste()
        {
            if (_inAppClipboardPaths.Count > 0) return true;

            try
            {
                if (Win32Interop.IsClipboardFormatAvailable(Win32Interop.CF_HDROP)) return true;

                var view = Clipboard.GetContent();
                return view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.Text);
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> PasteFilesAsync(
            string destinationDirectory,
            IProgress<FileTransferProgress>? progress = null,
            FileTransferController? controller = null)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                return false;

            List<string> sourcePaths = [];
            bool isCut = _isCutOperation;

            // 1. DataPackage (StorageItems) から取得
            try
            {
                var view = Clipboard.GetContent();
                if (view != null && view.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await view.GetStorageItemsAsync();
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrEmpty(item.Path) && (File.Exists(item.Path) || Directory.Exists(item.Path)))
                        {
                            sourcePaths.Add(item.Path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Paste] DataPackage StorageItems error: {ex.Message}");
            }

            // 2. アプリ内クリップボード確認 (高速フォールバック)
            if (sourcePaths.Count == 0 && _inAppClipboardPaths.Count > 0)
            {
                sourcePaths.AddRange(_inAppClipboardPaths.Where(p => File.Exists(p) || Directory.Exists(p)));
            }

            // 3. システムクリップボード (Win32 CF_HDROP) から取得 (外部エクスプローラーからのコピー対応)
            if (sourcePaths.Count == 0)
            {
                try
                {
                    if (Win32Interop.OpenClipboard(nint.Zero))
                    {
                        try
                        {
                            nint hDrop = Win32Interop.GetClipboardData(Win32Interop.CF_HDROP);
                            if (hDrop != nint.Zero)
                            {
                                uint count = Win32Interop.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                                for (uint i = 0; i < count; i++)
                                {
                                    uint length = Win32Interop.DragQueryFileW(hDrop, i, null, 0);
                                    var sb = new System.Text.StringBuilder((int)length + 1);
                                    Win32Interop.DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                                    string path = sb.ToString();
                                    if (File.Exists(path) || Directory.Exists(path))
                                    {
                                        sourcePaths.Add(path);
                                    }
                                }
                            }

                            uint dropEffectFormat = Win32Interop.RegisterClipboardFormatW("Preferred DropEffect");
                            if (dropEffectFormat != 0)
                            {
                                nint hEffect = Win32Interop.GetClipboardData(dropEffectFormat);
                                if (hEffect != nint.Zero)
                                {
                                    nint pEffect = Win32Interop.GlobalLock(hEffect);
                                    if (pEffect != nint.Zero)
                                    {
                                        int effect = Marshal.ReadInt32(pEffect);
                                        isCut = (effect == 2);
                                        Win32Interop.GlobalUnlock(hEffect);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Win32Interop.CloseClipboard();
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            // 4. WinUI テキストクリップボード確認 (フォールバック)
            if (sourcePaths.Count == 0)
            {
                try
                {
                    var view = Clipboard.GetContent();
                    if (view != null && view.Contains(StandardDataFormats.Text))
                    {
                        string text = await view.GetTextAsync();
                        var lines = text.Split([Environment.NewLine, "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            string trimmed = line.Trim().Trim('"');
                            if (File.Exists(trimmed) || Directory.Exists(trimmed))
                            {
                                sourcePaths.Add(trimmed);
                            }
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            sourcePaths = sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (sourcePaths.Count == 0) return false;

            bool success = await ExecuteCopyOrMoveAsync(sourcePaths, destinationDirectory, isCut, progress, controller);

            if (isCut && success)
            {
                _inAppClipboardPaths.Clear();
                _isCutOperation = false;
                ClipboardStateChanged?.Invoke();
            }

            return success;
        }

        public static string GetUniqueDestinationPath(string dir, string name)
        {
            string dest = Path.Combine(dir, name);
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);

            int counter = 2;
            while (true)
            {
                string newName = $"{nameWithoutExt} ({counter}){ext}";
                dest = Path.Combine(dir, newName);
                if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
                counter++;
            }
        }

        #endregion

        #region Recycle Bin & Deletion

        public static bool MoveToRecycleBin(IEnumerable<string> paths)
        {
            var validPaths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
            if (validPaths.Count == 0) return true;

            // 1. Windows Explorer 標準の IFileOperation COM インターフェイス（完全なゴミ箱移動・Undo・UAC昇格対応）
            try
            {
                bool shellSuccess = ShellFileOperationHelper.ExecuteDeleteAsync(validPaths, recycle: true, DefaultOwnerHwnd).GetAwaiter().GetResult();
                if (shellSuccess)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shell delete error: {ex.Message}");
            }

            // 2. フォールバック: Microsoft.VisualBasic.FileIO.FileSystem (SendToRecycleBin)
            bool allSuccess = true;
            foreach (var path in validPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else if (Directory.Exists(path))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FileSystem fallback error for {path}: {ex.Message}");
                    allSuccess = false;
                }
            }
            return allSuccess;
        }

        public static void DeletePermanently(IEnumerable<string> paths)
        {
            var pathList = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (pathList.Count == 0) return;

            // 通常削除を試行、管理者権限が必要な場合はシェル削除にフォールバック
            var failedPaths = new List<string>();
            foreach (var path in pathList)
            {
                try
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
                catch (UnauthorizedAccessException)
                {
                    failedPaths.Add(path);
                }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 5)
                {
                    failedPaths.Add(path);
                }
                catch
                {
                    // ignored
                }
            }

            if (failedPaths.Count > 0)
            {
                try
                {
                    ShellFileOperationHelper.ExecuteDeleteAsync(failedPaths, recycle: false, DefaultOwnerHwnd).GetAwaiter().GetResult();
                }
                catch { }
            }
        }

        #endregion

        #region New Items Creation

        public static async Task<string?> CreateNewFolderAsync(string parentDirectory)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
                return null;

            string targetName = Path.GetFileName(GetUniqueDestinationPath(parentDirectory, "新しいフォルダー"));
            string targetPath = Path.Combine(parentDirectory, targetName);

            if (!ShellFileOperationHelper.HasWritePermission(parentDirectory))
            {
                bool ok = await ShellFileOperationHelper.ExecuteNewItemAsync(parentDirectory, targetName, isDirectory: true, DefaultOwnerHwnd);
                return ok ? targetPath : null;
            }

            try
            {
                Directory.CreateDirectory(targetPath);
                return targetPath;
            }
            catch (UnauthorizedAccessException)
            {
                bool ok = await ShellFileOperationHelper.ExecuteNewItemAsync(parentDirectory, targetName, isDirectory: true, DefaultOwnerHwnd);
                return ok ? targetPath : null;
            }
            catch
            {
                return null;
            }
        }

        public static string? CreateNewFolder(string parentDirectory)
        {
            return CreateNewFolderAsync(parentDirectory).GetAwaiter().GetResult();
        }

        public static async Task<string?> CreateNewTextFileAsync(string parentDirectory)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
                return null;

            string targetName = Path.GetFileName(GetUniqueDestinationPath(parentDirectory, "新しいテキスト ドキュメント.txt"));
            string targetPath = Path.Combine(parentDirectory, targetName);

            if (!ShellFileOperationHelper.HasWritePermission(parentDirectory))
            {
                bool ok = await ShellFileOperationHelper.ExecuteNewItemAsync(parentDirectory, targetName, isDirectory: false, DefaultOwnerHwnd);
                return ok ? targetPath : null;
            }

            try
            {
                File.WriteAllText(targetPath, string.Empty);
                return targetPath;
            }
            catch (UnauthorizedAccessException)
            {
                bool ok = await ShellFileOperationHelper.ExecuteNewItemAsync(parentDirectory, targetName, isDirectory: false, DefaultOwnerHwnd);
                return ok ? targetPath : null;
            }
            catch
            {
                return null;
            }
        }

        public static string? CreateNewTextFile(string parentDirectory)
        {
            return CreateNewTextFileAsync(parentDirectory).GetAwaiter().GetResult();
        }

        #endregion

        #region Archive Compression & Extraction
        public static Task<string?> CompressToZipAsync(IEnumerable<string> paths, string destinationDirectory, ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal)
        {
            return ArchiveService.CompressToZipAsync(paths, destinationDirectory, level);
        }

        public static Task<string?> CompressTo7zAsync(IEnumerable<string> paths, string destinationDirectory, ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal)
        {
            return ArchiveService.CompressTo7zAsync(paths, destinationDirectory, level);
        }

        public static Task<bool> ExtractArchiveAsync(string archiveFilePath, string destinationDirectory, bool createSubFolder = true)
        {
            return ArchiveService.ExtractAsync(archiveFilePath, destinationDirectory, createSubFolder);
        }

        public static Task<bool> ExtractZipAsync(string zipFilePath, string destinationDirectory)
        {
            return ArchiveService.ExtractAsync(zipFilePath, destinationDirectory, createSubFolder: true);
        }
        #endregion

        #region Open With

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENASINFO
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcszFile;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pcszClass;
            public uint oaifInFlags;
        }

        [DllImport("shell32.dll", EntryPoint = "SHOpenWithDialog", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHOpenWithDialog(nint hwnd, ref OPENASINFO poaInfo);

        public static void OpenWithDialog(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                var info = new OPENASINFO
                {
                    pcszFile = filePath,
                    pcszClass = null,
                    oaifInFlags = 0x00000004 | 0x00000001 // OAIF_EXEC | OAIF_ALLOW_REGISTRATION
                };

                int hr = SHOpenWithDialog(nint.Zero, ref info);
                if (hr == 0) return;
            }
            catch
            {
                // SHOpenWithDialog not available or error, fall through to fallback
            }

            try
            {
                string safePath = filePath.EndsWith('\\') ? $"\"{filePath}\\\"" : $"\"{filePath}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,OpenAs_RunDLL {safePath}",
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignored
            }
        }

        #endregion
    }
}
