using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Core;
using FastExplorer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FastExplorer
{
    public partial class FilePropertiesInfo
    {
        private void LoadDriveProperties(string drivePath)
        {
            try
            {
                string root = Path.GetPathRoot(drivePath) ?? drivePath;
                if (!root.EndsWith('\\')) root += "\\";

                var drive = new DriveInfo(root);
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "ローカル ディスク" : drive.VolumeLabel;
                Name = $"{label} ({root.TrimEnd('\\')})";
                _originalName = Name;
                ItemType = drive.DriveType switch
                {
                    DriveType.Fixed => "ローカル固定ディスク",
                    DriveType.Removable => "リムーバブル ディスク",
                    DriveType.Network => "ネットワーク ドライブ",
                    DriveType.CDRom => "CD/DVD ドライブ",
                    DriveType.Ram => "RAM ディスク",
                    _ => "ディスク ドライブ"
                };

                Location = root;
                FileSystem = drive.DriveFormat;
                TotalSpace = drive.TotalSize;
                FreeSpace = drive.TotalFreeSpace;
                UsedSpace = TotalSpace - FreeSpace;
                UsedPercentage = TotalSpace > 0 ? ((double)UsedSpace / TotalSpace) * 100.0 : 0;
            }
            catch (Exception ex)
            {
                Name = drivePath;
                ItemType = "ドライブ";
                Location = drivePath;
                CalculationStatus = ex.Message;
            }
        }

        private void LoadFileProperties(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                Name = fi.Name;
                _originalName = fi.Name;
                Location = fi.DirectoryName ?? string.Empty;

                ItemType = GetShellTypeName(filePath, isDirectory: false);
                string programName = GetAssociatedProgramName(fi.Extension);
                OpensWith = string.IsNullOrEmpty(programName) ? "不明なアプリケーション" : programName;

                Size = fi.Length;
                SizeOnDisk = CalculateSizeOnDisk(fi.Length);

                DateCreated = fi.CreationTime;
                DateModified = fi.LastWriteTime;
                DateAccessed = fi.LastAccessTime;

                IsReadOnly = fi.IsReadOnly;
                IsHidden = (fi.Attributes & FileAttributes.Hidden) != 0;
                _originalIsReadOnly = IsReadOnly;
                _originalIsHidden = IsHidden;

                LoadDigitalSignatures(filePath);
            }
            catch (Exception ex)
            {
                Name = Path.GetFileName(filePath);
                Location = Path.GetDirectoryName(filePath) ?? string.Empty;
                CalculationStatus = ex.Message;
            }
        }

        private void LoadDigitalSignatures(string filePath)
        {
            try
            {
                DigitalSignatures.Clear();
                string ext = Path.GetExtension(filePath);
                if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".sys", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".cat", StringComparison.OrdinalIgnoreCase))
                {
#pragma warning disable SYSLIB0057
                    var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                    var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);

                    string signer = cert2.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) ?? cert2.Subject;
                    string algo = cert2.SignatureAlgorithm.FriendlyName ?? cert2.SignatureAlgorithm.Value ?? "SHA256";

                    DigitalSignatures.Add(new DigitalSignatureItem
                    {
                        SignerName = signer,
                        DigestAlgorithm = algo,
                        Timestamp = cert2.NotBefore.ToString("yyyy'年'M'月'd'日' HH:mm:ss")
                    });
                }
            }
            catch
            {
                // 署名がないファイルは例外となり無視（DigitalSignatures は空のまま = HasDigitalSignatures は false）
            }
        }

        private void LoadFolderProperties(string folderPath)
        {
            try
            {
                var di = new DirectoryInfo(folderPath);
                Name = di.Name;
                _originalName = di.Name;
                Location = di.Parent?.FullName ?? string.Empty;

                ItemType = GetShellTypeName(folderPath, isDirectory: true);

                DateCreated = di.CreationTime;
                DateModified = di.LastWriteTime;
                DateAccessed = di.LastAccessTime;

                IsReadOnly = (di.Attributes & FileAttributes.ReadOnly) != 0;
                IsHidden = (di.Attributes & FileAttributes.Hidden) != 0;
                _originalIsReadOnly = IsReadOnly;
                _originalIsHidden = IsHidden;

                StartFolderSizeCalculation([folderPath]);
            }
            catch (Exception ex)
            {
                Name = Path.GetFileName(folderPath);
                Location = Path.GetDirectoryName(folderPath) ?? string.Empty;
                CalculationStatus = ex.Message;
            }
        }

        private void LoadMultipleProperties(IReadOnlyList<string> paths)
        {
            Name = $"選択された {paths.Count} 個のアイテム";
            _originalName = Name;
            ItemType = "複数アイテム";

            string firstDir = Path.GetDirectoryName(paths[0]) ?? string.Empty;
            bool allSameDir = paths.All(p => string.Equals(Path.GetDirectoryName(p), firstDir, StringComparison.OrdinalIgnoreCase));
            Location = allSameDir ? firstDir : "複数の場所";

            bool? readOnlyState = null;
            bool? hiddenState = null;
            bool first = true;

            foreach (var p in paths)
            {
                try
                {
                    FileAttributes attr = File.GetAttributes(p);
                    bool ro = (attr & FileAttributes.ReadOnly) != 0;
                    bool hd = (attr & FileAttributes.Hidden) != 0;

                    if (first)
                    {
                        readOnlyState = ro;
                        hiddenState = hd;
                        first = false;
                    }
                    else
                    {
                        if (readOnlyState.HasValue && readOnlyState.Value != ro) readOnlyState = null;
                        if (hiddenState.HasValue && hiddenState.Value != hd) hiddenState = null;
                    }
                }
                catch { }
            }

            IsReadOnly = readOnlyState;
            IsHidden = hiddenState;
            _originalIsHidden = IsHidden;
            _originalIsReadOnly = IsReadOnly;

            StartFolderSizeCalculation(paths);
        }

        public void StartFolderSizeCalculation(IReadOnlyList<string> targetPaths)
        {
            _sizeCalculationCts?.Cancel();
            _sizeCalculationCts = new CancellationTokenSource();
            var token = _sizeCalculationCts.Token;

            IsCalculatingSize = true;
            CalculationStatus = "計算中...";

            Task.Run(() =>
            {
                long totalSize = 0;
                long totalFiles = 0;
                long totalFolders = 0;
                var lastUiUpdate = Stopwatch.StartNew();

                try
                {
                    var queue = new Queue<string>();
                    foreach (var p in targetPaths)
                    {
                        if (Directory.Exists(p))
                        {
                            queue.Enqueue(p);
                        }
                        else if (File.Exists(p))
                        {
                            try
                            {
                                var fi = new FileInfo(p);
                                totalSize += fi.Length;
                                totalFiles++;
                            }
                            catch { }
                        }
                    }

                    while (queue.Count > 0)
                    {
                        if (token.IsCancellationRequested) return;

                        string currentDir = queue.Dequeue();
                        totalFolders++;

                        try
                        {
                            var dirInfo = new DirectoryInfo(currentDir);
                            foreach (var file in dirInfo.EnumerateFiles())
                            {
                                if (token.IsCancellationRequested) return;
                                totalSize += file.Length;
                                totalFiles++;
                            }

                            foreach (var subDir in dirInfo.EnumerateDirectories())
                            {
                                if (token.IsCancellationRequested) return;
                                if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                                queue.Enqueue(subDir.FullName);
                            }
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (Exception) { }

                        if (lastUiUpdate.ElapsedMilliseconds > 120)
                        {
                            lastUiUpdate.Restart();
                            long curSize = totalSize;
                            long curFiles = totalFiles;
                            long curFolders = totalFolders;
                            _dispatcherQueue?.TryEnqueue(() =>
                            {
                                if (token.IsCancellationRequested) return;
                                Size = curSize;
                                SizeOnDisk = CalculateSizeOnDisk(curSize);
                                FileCount = curFiles;
                                FolderCount = curFolders;
                            });
                        }
                    }

                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        Size = totalSize;
                        SizeOnDisk = CalculateSizeOnDisk(totalSize);
                        FileCount = totalFiles;
                        FolderCount = totalFolders;
                        IsCalculatingSize = false;
                        CalculationStatus = string.Empty;
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        IsCalculatingSize = false;
                        CalculationStatus = ex.Message;
                    });
                }
            }, token);
        }

        private async Task LoadIconAsync()
        {
            try
            {
                if (TargetType == PropertyTargetType.Drive)
                {
                    var bmp = IconThumbnailService.GetSoftwareBitmapForPath(FullPath, isDirectory: true, large: true);
                    if (bmp != null)
                    {
                        var source = new SoftwareBitmapSource();
                        await source.SetBitmapAsync(bmp);
                        Icon = source;
                    }
                }
                else if (TargetType == PropertyTargetType.SingleFolder)
                {
                    var bmp = IconThumbnailService.GetSoftwareBitmapForPath(FullPath, isDirectory: true, large: true);
                    if (bmp != null)
                    {
                        var source = new SoftwareBitmapSource();
                        await source.SetBitmapAsync(bmp);
                        Icon = source;
                    }
                }
                else if (TargetType == PropertyTargetType.SingleFile)
                {
                    var bmp = IconThumbnailService.GetSoftwareBitmapForPath(FullPath, isDirectory: false, large: true);
                    if (bmp != null)
                    {
                        var source = new SoftwareBitmapSource();
                        await source.SetBitmapAsync(bmp);
                        Icon = source;
                    }
                }
            }
            catch { }
        }

        private static string GetShellTypeName(string path, bool isDirectory)
        {
            try
            {
                var shinfo = new Win32Interop.SHFILEINFOW();
                uint flags = Win32Interop.SHGFI_TYPENAME;
                uint attr = isDirectory ? Win32Interop.FILE_ATTRIBUTE_DIRECTORY : Win32Interop.FILE_ATTRIBUTE_NORMAL;

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    flags |= Win32Interop.SHGFI_USEFILEATTRIBUTES;
                }

                nint res = Win32Interop.SHGetFileInfoW(
                    path,
                    attr,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    flags);

                if (res != nint.Zero && !string.IsNullOrWhiteSpace(shinfo.szTypeName))
                {
                    return shinfo.szTypeName;
                }
            }
            catch { }

            if (isDirectory) return "ファイル フォルダー";
            string ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? "ファイル" : $"{ext.TrimStart('.').ToUpperInvariant()} ファイル ({ext})";
        }

        private static string GetAssociatedProgramName(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return string.Empty;

            try
            {
                uint length = 0;
                Win32Interop.AssocQueryStringW(
                    Win32Interop.AssocF.Verify,
                    Win32Interop.AssocStr.FriendlyAppName,
                    extension,
                    null,
                    null!,
                    ref length);

                if (length > 0)
                {
                    var sb = new StringBuilder((int)length);
                    int hr = Win32Interop.AssocQueryStringW(
                        Win32Interop.AssocF.Verify,
                        Win32Interop.AssocStr.FriendlyAppName,
                        extension,
                        null,
                        sb,
                        ref length);

                    if (hr == 0 && sb.Length > 0)
                    {
                        return sb.ToString();
                    }
                }
            }
            catch { }

            return string.Empty;
        }
    }
}
