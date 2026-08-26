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

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
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

            return info;
        }

        public async Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, Action<double>? progressCallback = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(downloadUrl)) return false;

            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "FastExplorer_Setup.exe");

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
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
                                double progress = (double)totalRead / totalBytes * 100.0;
                                progressCallback(progress);
                            }
                        }
                    }
                }

                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/SILENT /NORESTART",
                    UseShellExecute = true
                };

                Process.Start(psi);

                // インストーラーがファイルを上書きできるよう自プロセスを速やかに完全終了
                Environment.Exit(0);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
