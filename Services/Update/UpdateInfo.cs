using System;

namespace FastExplorer.Services.Update
{
    public class UpdateInfo
    {
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string TagName { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
