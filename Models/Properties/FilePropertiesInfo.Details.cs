using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace FastExplorer
{
    public partial class FilePropertiesInfo
    {
        private void LoadSecurityInfo(string path)
        {
            try
            {
                SecurityPrincipals.Clear();

                FileSystemSecurity? security = null;
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    security = fi.GetAccessControl();
                }
                else if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    security = di.GetAccessControl();
                }

                if (security != null)
                {
                    var rules = security.GetAccessRules(true, true, typeof(NTAccount));
                    var dict = new Dictionary<string, SecurityPrincipalPermission>();

                    foreach (FileSystemAccessRule rule in rules)
                    {
                        string accountName = rule.IdentityReference.Value;
                        if (!dict.TryGetValue(accountName, out var perm))
                        {
                            bool isGroup = accountName.Contains("Administrators") || accountName.Contains("Users") || accountName.Contains("GROUP");
                            perm = new SecurityPrincipalPermission
                            {
                                Name = accountName,
                                DisplayName = accountName,
                                Glyph = isGroup ? "\uE716" : "\uE77B",
                                IsGroup = isGroup
                            };
                            dict[accountName] = perm;
                        }

                        if (rule.AccessControlType == AccessControlType.Allow)
                        {
                            var rights = rule.FileSystemRights;
                            if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl) perm.FullControl = true;
                            if ((rights & FileSystemRights.Modify) != 0 || perm.FullControl) perm.Modify = true;
                            if ((rights & FileSystemRights.ReadAndExecute) != 0 || perm.FullControl) perm.ReadAndExecute = true;
                            if ((rights & FileSystemRights.Read) != 0 || perm.FullControl) perm.Read = true;
                            if ((rights & FileSystemRights.Write) != 0 || perm.FullControl) perm.Write = true;
                        }
                    }

                    foreach (var perm in dict.Values)
                    {
                        SecurityPrincipals.Add(perm);
                    }
                }
            }
            catch
            {
                // フォールバック: 現在のユーザーとSYSTEM、Administratorsを追加
                try
                {
                    string currentUserName = Environment.UserName;
                    SecurityPrincipals.Add(new SecurityPrincipalPermission
                    {
                        Name = "SYSTEM",
                        DisplayName = "SYSTEM",
                        Glyph = "\uE716",
                        FullControl = true, Modify = true, ReadAndExecute = true, Read = true, Write = true
                    });
                    SecurityPrincipals.Add(new SecurityPrincipalPermission
                    {
                        Name = currentUserName,
                        DisplayName = currentUserName,
                        Glyph = "\uE77B",
                        FullControl = true, Modify = true, ReadAndExecute = true, Read = true, Write = true
                    });
                    SecurityPrincipals.Add(new SecurityPrincipalPermission
                    {
                        Name = "Administrators",
                        DisplayName = "Administrators",
                        Glyph = "\uE716",
                        FullControl = true, Modify = true, ReadAndExecute = true, Read = true, Write = true
                    });
                }
                catch { }
            }
        }

        private async Task LoadDetailsAsync(string filePath)
        {
            try
            {
                DetailsList.Clear();
                var fi = new FileInfo(filePath);
                string ext = fi.Extension.ToLowerInvariant();

                // 1. 説明 / 基本情報
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "説明", Value = string.Empty });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "名前", Value = fi.Name });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "項目の種類", Value = ItemType });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "フォルダー パス", Value = fi.DirectoryName ?? string.Empty });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "サイズ", Value = FormatFileSize(fi.Length) });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "作成日時", Value = FormattedDateCreated });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "更新日時", Value = FormattedDateModified });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "アクセス日時", Value = FormattedDateAccessed });
                DetailsList.Add(new FileDetailProperty { Category = "説明", Name = "属性", Value = fi.Attributes.ToString() });

                // 2. 実行ファイル (.exe, .dll, .sys)
                if (ext is ".exe" or ".dll" or ".sys" or ".ocx")
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(filePath);
                        DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "バージョン情報", Value = string.Empty });
                        if (!string.IsNullOrEmpty(vi.FileDescription)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "ファイルの説明", Value = vi.FileDescription });
                        if (!string.IsNullOrEmpty(vi.FileVersion)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "ファイル バージョン", Value = vi.FileVersion });
                        if (!string.IsNullOrEmpty(vi.ProductName)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "製品名", Value = vi.ProductName });
                        if (!string.IsNullOrEmpty(vi.ProductVersion)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "製品バージョン", Value = vi.ProductVersion });
                        if (!string.IsNullOrEmpty(vi.CompanyName)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "著作権者 / 会社名", Value = vi.CompanyName });
                        if (!string.IsNullOrEmpty(vi.LegalCopyright)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "著作権", Value = vi.LegalCopyright });
                        if (!string.IsNullOrEmpty(vi.OriginalFilename)) DetailsList.Add(new FileDetailProperty { Category = "バージョン情報", Name = "元のファイル名", Value = vi.OriginalFilename });
                    }
                    catch { }
                }

                // 3. 画像ファイル (.png, .jpg, .jpeg, .bmp, .gif, .webp, .ico)
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico" or ".tif" or ".tiff")
                {
                    try
                    {
                        await Task.Run(async () =>
                        {
                            using var stream = File.OpenRead(filePath);
                            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                            uint w = decoder.PixelWidth;
                            uint h = decoder.PixelHeight;
                            double dpiX = decoder.DpiX;
                            double dpiY = decoder.DpiY;

                            _dispatcherQueue?.TryEnqueue(() =>
                            {
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "イメージ", Value = string.Empty });
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "大きさ", Value = $"{w} x {h}" });
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "幅", Value = $"{w} ピクセル" });
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "高さ", Value = $"{h} ピクセル" });
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "水平方向の解像度", Value = $"{dpiX:0} dpi" });
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "垂直方向の解像度", Value = $"{dpiY:0} dpi" });
                                DetailsList.Add(new FileDetailProperty { Category = "イメージ", Name = "ビットの深さ", Value = decoder.BitmapPixelFormat.ToString() });
                            });
                        });
                    }
                    catch { }
                }

                // 4. コンピューター / 所有者情報
                DetailsList.Add(new FileDetailProperty { Category = "ファイル", Name = "ファイル", Value = string.Empty });
                DetailsList.Add(new FileDetailProperty { Category = "ファイル", Name = "コンピューター", Value = Environment.MachineName });
                try
                {
                    var security = fi.GetAccessControl();
                    var owner = security.GetOwner(typeof(NTAccount))?.Value;
                    if (!string.IsNullOrEmpty(owner))
                    {
                        DetailsList.Add(new FileDetailProperty { Category = "ファイル", Name = "所有者", Value = owner });
                    }
                }
                catch { }
            }
            catch { }
        }

        public async Task CalculateHashesAsync(CancellationToken ct = default)
        {
            if (TargetType != PropertyTargetType.SingleFile || !File.Exists(FullPath)) return;

            _hashCalculationCts?.Cancel();
            _hashCalculationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _hashCalculationCts.Token;

            IsCalculatingHash = true;
            Sha256Hash = "計算中...";
            Md5Hash = "計算中...";

            try
            {
                string filePath = FullPath;
                var (sha256, md5) = await Task.Run(() =>
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
                    
                    using var sha256Alg = SHA256.Create();
                    byte[] sha256Bytes = sha256Alg.ComputeHash(stream);
                    string sha256Str = Convert.ToHexString(sha256Bytes).ToLowerInvariant();

                    stream.Position = 0;
                    using var md5Alg = MD5.Create();
                    byte[] md5Bytes = md5Alg.ComputeHash(stream);
                    string md5Str = Convert.ToHexString(md5Bytes).ToLowerInvariant();

                    return (sha256Str, md5Str);
                }, token);

                if (!token.IsCancellationRequested)
                {
                    Sha256Hash = sha256;
                    Md5Hash = md5;
                }
            }
            catch (Exception ex)
            {
                Sha256Hash = $"エラー: {ex.Message}";
                Md5Hash = $"エラー: {ex.Message}";
            }
            finally
            {
                IsCalculatingHash = false;
            }
        }
    }
}
