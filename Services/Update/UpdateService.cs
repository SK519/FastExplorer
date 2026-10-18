using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FastExplorer.Services.Update
{
    public class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

    public class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAsset[]? Assets { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(GitHubReleaseResponse))]
    [JsonSerializable(typeof(GitHubReleaseAsset[]))]
    public partial class UpdateJsonContext : JsonSerializerContext
    {
    }

    public class UpdateService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public static DateTime? LastCheckedTime { get; set; }
        public static UpdateInfo? LastUpdateInfo { get; set; }
        public static event Action<UpdateInfo>? UpdateStatusChanged;

        public static string GetCurrentVersionString()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string owner, string repo, CancellationToken cancellationToken = default)
        {
            var info = new UpdateInfo
            {
                CurrentVersion = GetCurrentVersionString()
            };

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                info.ErrorMessage = "リポジトリ情報が設定されていません。";
                return info;
            }

            // オーナー名・リポジトリ名の安全なバリデーション (英数字、ハイフン、アンダースコア、ドットのみ許可)
            if (!System.Text.RegularExpressions.Regex.IsMatch(owner, @"^[a-zA-Z0-9_\-\.]+$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[a-zA-Z0-9_\-\.]+$"))
            {
                info.ErrorMessage = "リポジトリの指定形式が不正です。";
                return info;
            }

            try
            {
                string safeOwner = Uri.EscapeDataString(owner);
                string safeRepo = Uri.EscapeDataString(repo);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{safeOwner}/{safeRepo}/releases/latest");
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FastExplorer-App", info.CurrentVersion));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    info.ErrorMessage = $"リリースの取得に失敗しました (HTTP {(int)response.StatusCode})";
                    return info;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubReleaseResponse);

                if (release == null || string.IsNullOrEmpty(release.TagName))
                {
                    info.ErrorMessage = "リリース情報の解析に失敗しました。";
                    return info;
                }

                info.TagName = release.TagName;
                var cleanTag = release.TagName.TrimStart('v', 'V');
                info.LatestVersion = cleanTag;
                info.ReleaseNotes = release.Body ?? string.Empty;
                info.PublishedAt = release.PublishedAt;

                var installerAsset = release.Assets?.FirstOrDefault(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                info.DownloadUrl = installerAsset?.BrowserDownloadUrl;

                if (Version.TryParse(info.CurrentVersion, out var currentVer) &&
                    Version.TryParse(cleanTag, out var latestVer))
                {
                    info.IsUpdateAvailable = latestVer > currentVer;
                }
                else
                {
                    info.IsUpdateAvailable = string.Compare(cleanTag, info.CurrentVersion, StringComparison.OrdinalIgnoreCase) > 0;
                }
            }
            catch (Exception ex)
            {
                info.ErrorMessage = $"アップデート確認エラー: {ex.Message}";
            }

            LastCheckedTime = DateTime.Now;
            LastUpdateInfo = info;
            try
            {
                UpdateStatusChanged?.Invoke(info);
            }
            catch { }

            return info;
        }

        public static string? DownloadedInstallerPath { get; set; }

        public async Task<string?> DownloadUpdateAsync(string downloadUrl, Action<double>? progressCallback = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(downloadUrl)) return null;

            // ダウンロードURLの安全性検証 (HTTPS かつ GitHub 公式ドメインのみ許可)
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine($"[Update] Security alert: Blocked untrusted download URL: {downloadUrl}");
                return null;
            }

            try
            {
                string updateDir = Path.Combine(Path.GetTempPath(), "FastExplorer", "Updates");
                Directory.CreateDirectory(updateDir);
                string tempPath = Path.Combine(updateDir, $"FastExplorer_Setup_{Guid.NewGuid():N}.exe");

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                            totalRead += bytesRead;

                            if (totalBytes > 0 && progressCallback != null)
                            {
                                double progress = Math.Min(100.0, (double)totalRead / totalBytes * 100.0);
                                progressCallback(progress);
                            }
                        }
                    }
                }

                progressCallback?.Invoke(100.0);
                DownloadedInstallerPath = tempPath;

                // ダウンロード完了を全画面・コントロールに通知
                try
                {
                    UpdateStatusChanged?.Invoke(LastUpdateInfo ?? new UpdateInfo { IsUpdateAvailable = true, DownloadUrl = downloadUrl });
                }
                catch { }

                return tempPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] DownloadUpdateAsync error: {ex.Message}");
                return null;
            }
        }

        public static void LaunchInstallerAndExit(string? installerPath = null)
        {
            var path = installerPath ?? DownloadedInstallerPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            // インストーラの完全性・署名検証
            var verifyResult = FastExplorer.Core.AuthenticodeVerifier.VerifyFile(path, out var statusDesc);
            if (verifyResult == FastExplorer.Core.SignatureVerificationResult.Tampered ||
                verifyResult == FastExplorer.Core.SignatureVerificationResult.ExplicitlyDistrusted)
            {
                Debug.WriteLine($"[Update] Security alert: Installer signature verification failed ({statusDesc})! Launch aborted.");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "/SILENT /NORESTART",
                    UseShellExecute = true
                };

                Process.Start(psi);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] LaunchInstaller error: {ex.Message}");
            }
        }

        public static void OpenDownloadedInstallerFolder(string? installerPath = null)
        {
            var path = installerPath ?? DownloadedInstallerPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] OpenDownloadedInstallerFolder error: {ex.Message}");
            }
        }

        public async Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, Action<double>? progressCallback = null, CancellationToken cancellationToken = default)
        {
            var downloaded = await DownloadUpdateAsync(downloadUrl, progressCallback, cancellationToken);
            if (!string.IsNullOrEmpty(downloaded))
            {
                LaunchInstallerAndExit(downloaded);
                return true;
            }
            return false;
        }
    }
}
