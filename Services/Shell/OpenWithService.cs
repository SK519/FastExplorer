using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;
using Windows.Graphics.Imaging;

namespace FastExplorer.Services
{
    public sealed class OpenWithAppInfo
    {
        public string DisplayName { get; init; } = "";
        public string AppPath { get; init; } = "";
        public string? IconPath { get; init; }
        public int IconIndex { get; init; }
        public bool IsRecommended { get; init; }
        public SoftwareBitmap? IconBitmap { get; set; }
        public nint AssocHandlerPtr { get; init; }
    }

    public static class OpenWithService
    {
        private static readonly ConcurrentDictionary<string, List<OpenWithAppInfo>> _extensionAppsCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ExcludedExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "fastexplorer.exe", "fastexplorer", "openwith.exe", "openwith", "rundll32.exe", "explorer.exe", "dllhost.exe", "svchost.exe", "msedgewebview2.exe", "msedgewebview2"
        };

        public static void ClearCache()
        {
            _extensionAppsCache.Clear();
        }

        public static IReadOnlyList<OpenWithAppInfo> GetOpenWithApps(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<OpenWithAppInfo>();

            string extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension)) extension = ".";

            if (_extensionAppsCache.TryGetValue(extension, out var cached))
            {
                return cached;
            }

            var appList = QueryOpenWithAppsInternal(extension);
            _extensionAppsCache[extension] = appList;
            return appList;
        }

        private static List<OpenWithAppInfo> QueryOpenWithAppsInternal(string extension)
        {
            var results = new List<OpenWithAppInfo>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 推奨ハンドラー列挙 (ASSOC_FILTER_RECOMMENDED) - Windows 11 Explorer と完全に同一の正規関連付けアプリ群
            try
            {
                int hr = Win32Interop.SHAssocEnumHandlers(extension, Win32Interop.ASSOC_FILTER.ASSOC_FILTER_RECOMMENDED, out nint pEnumHandlers);
                if (hr == 0 && pEnumHandlers != nint.Zero)
                {
                    try
                    {
                        EnumerateHandlers(pEnumHandlers, results, seenKeys, seenNames);
                    }
                    finally
                    {
                        Win32Interop.NativeCom.Release(pEnumHandlers);
                    }
                }
            }
            catch { }

            // 2. 万が一推奨ハンドラーが0件の場合のみ、デフォルト関連付け ProgID / UserChoice をフォールバック取得
            if (results.Count == 0 && !string.IsNullOrEmpty(extension) && extension != ".")
            {
                try
                {
                    EnumerateFallbackRegistryOpenWith(extension, results, seenKeys, seenNames);
                }
                catch { }
            }

            return results;
        }

        private static void EnumerateHandlers(
            nint pEnumHandlers,
            List<OpenWithAppInfo> results,
            HashSet<string> seenKeys,
            HashSet<string> seenNames)
        {
            while (Win32Interop.NativeCom.EnumAssocHandlers_Next(pEnumHandlers, 1, out nint pHandler, out uint fetched) == 0 && fetched > 0 && pHandler != nint.Zero)
            {
                string uiName = "";
                string appPath = "";
                string iconPath = "";
                int iconIndex = 0;
                bool isRecommended = true;

                try
                {
                    Win32Interop.NativeCom.AssocHandler_GetUIName(pHandler, out uiName);
                    Win32Interop.NativeCom.AssocHandler_GetName(pHandler, out appPath);
                    Win32Interop.NativeCom.AssocHandler_GetIconLocation(pHandler, out iconPath, out iconIndex);
                    int recHr = Win32Interop.NativeCom.AssocHandler_IsRecommended(pHandler);
                    if (recHr == 1) isRecommended = false;
                }
                catch { }

                uiName = CleanDisplayName(uiName, appPath);
                if (string.IsNullOrEmpty(uiName) || IsExcludedApp(appPath, uiName))
                {
                    Win32Interop.NativeCom.Release(pHandler);
                    continue;
                }

                string dedupeKey = !string.IsNullOrEmpty(appPath) ? appPath : uiName;
                if (!seenKeys.Add(dedupeKey) || !seenNames.Add(uiName))
                {
                    Win32Interop.NativeCom.Release(pHandler);
                    continue;
                }

                var iconBmp = ExtractAppIcon(iconPath, iconIndex, appPath);

                results.Add(new OpenWithAppInfo
                {
                    DisplayName = uiName,
                    AppPath = appPath,
                    IconPath = iconPath,
                    IconIndex = iconIndex,
                    IsRecommended = isRecommended,
                    IconBitmap = iconBmp,
                    AssocHandlerPtr = pHandler
                });
            }
        }

        private static void EnumerateFallbackRegistryOpenWith(
            string extension,
            List<OpenWithAppInfo> results,
            HashSet<string> seenKeys,
            HashSet<string> seenNames)
        {
            // HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
                var progId = key?.GetValue("ProgId") as string;
                if (!string.IsNullOrEmpty(progId))
                {
                    using var progKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                    var cmd = progKey?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        string exePath = ExtractExePathFromCommand(cmd);
                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            AddAppFromPath(exePath, results, seenKeys, seenNames);
                        }
                    }
                }
            }
            catch { }
        }

        private static void AddAppFromPath(
            string exePath,
            List<OpenWithAppInfo> results,
            HashSet<string> seenKeys,
            HashSet<string> seenNames)
        {
            if (seenKeys.Contains(exePath)) return;

            string displayName = "";
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                {
                    displayName = vi.FileDescription.Trim();
                }
            }
            catch { }

            if (string.IsNullOrEmpty(displayName))
            {
                displayName = Path.GetFileNameWithoutExtension(exePath);
            }

            if (string.IsNullOrEmpty(displayName) || IsExcludedApp(exePath, displayName)) return;
            if (!seenKeys.Add(exePath) || !seenNames.Add(displayName)) return;

            var iconBmp = ExtractAppIcon(null, 0, exePath);

            results.Add(new OpenWithAppInfo
            {
                DisplayName = displayName,
                AppPath = exePath,
                IsRecommended = true,
                IconBitmap = iconBmp
            });
        }

        private static string ExtractExePathFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            string trimmed = command.Trim();
            if (trimmed.StartsWith("\""))
            {
                int endQuote = trimmed.IndexOf('\"', 1);
                if (endQuote > 1) return trimmed.Substring(1, endQuote - 1);
            }
            int spaceIdx = trimmed.IndexOf(' ');
            return spaceIdx > 0 ? trimmed.Substring(0, spaceIdx) : trimmed;
        }

        private static bool IsExcludedApp(string appPath, string displayName)
        {
            if (!string.IsNullOrEmpty(appPath))
            {
                string exeName = Path.GetFileName(appPath).ToLowerInvariant();
                if (ExcludedExeNames.Contains(exeName) || ExcludedExeNames.Contains(appPath.ToLowerInvariant()))
                    return true;
            }

            if (!string.IsNullOrEmpty(displayName))
            {
                string lower = displayName.ToLowerInvariant();
                if (lower.Contains("fastexplorer") || lower == "openwith" || lower == "rundll32" || lower == "msedgewebview2")
                    return true;
            }

            return false;
        }

        private static string CleanDisplayName(string rawName, string appPath)
        {
            if (!string.IsNullOrWhiteSpace(rawName))
            {
                string clean = rawName.Replace("&", "").Trim();
                if (!string.IsNullOrEmpty(clean))
                {
                    return clean;
                }
            }

            if (!string.IsNullOrWhiteSpace(appPath))
            {
                if (File.Exists(appPath))
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(appPath);
                        if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                        {
                            return vi.FileDescription.Trim();
                        }
                    }
                    catch { }
                }

                return Path.GetFileNameWithoutExtension(appPath);
            }

            return "";
        }

        public static SoftwareBitmap? ExtractAppIcon(string? iconPath, int iconIndex, string? appPath)
        {
            try
            {
                // 1. UWP / パッケージアプリの ms-resource / @{PackageFullName?...} URI または 間接参照リソース文字列の解決
                if (!string.IsNullOrEmpty(iconPath) && iconPath.StartsWith("@"))
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int hr = Win32Interop.SHLoadIndirectString(iconPath, sb, (uint)sb.Capacity, nint.Zero);
                    if (hr == 0 && sb.Length > 0)
                    {
                        string resolved = sb.ToString();
                        if (File.Exists(resolved))
                        {
                            string ext = Path.GetExtension(resolved).ToLowerInvariant();
                            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                            {
                                var imgBmp = IconThumbnailService.ExtractSkiaImageThumbnail(resolved, 24);
                                if (imgBmp != null) return imgBmp;
                            }

                            var hIcons = new nint[1];
                            var ids = new uint[1];
                            uint count = Win32Interop.PrivateExtractIcons(resolved, iconIndex, 20, 20, hIcons, ids, 1, 0);
                            if (count > 0 && hIcons[0] != nint.Zero)
                            {
                                try { return IconThumbnailService.ConvertHIconToSoftwareBitmap(hIcons[0]); }
                                finally { Win32Interop.DestroyIcon(hIcons[0]); }
                            }
                        }
                    }
                }

                // 2. 通常のファイルパスまたは DLL / EXE アイコン
                if (!string.IsNullOrEmpty(iconPath))
                {
                    string expanded = Environment.ExpandEnvironmentVariables(iconPath).Trim('"', ' ');
                    int idx = iconIndex;
                    if (expanded.Contains(','))
                    {
                        var parts = expanded.Split(',');
                        expanded = parts[0].Trim('"', ' ');
                        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int parsedIdx))
                        {
                            idx = parsedIdx;
                        }
                    }

                    if (File.Exists(expanded))
                    {
                        string ext = Path.GetExtension(expanded).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                        {
                            var imgBmp = IconThumbnailService.ExtractSkiaImageThumbnail(expanded, 24);
                            if (imgBmp != null) return imgBmp;
                        }

                        var hIcons = new nint[1];
                        var ids = new uint[1];
                        uint count = Win32Interop.PrivateExtractIcons(expanded, idx, 20, 20, hIcons, ids, 1, 0);
                        if (count > 0 && hIcons[0] != nint.Zero)
                        {
                            try { return IconThumbnailService.ConvertHIconToSoftwareBitmap(hIcons[0]); }
                            finally { Win32Interop.DestroyIcon(hIcons[0]); }
                        }
                    }
                }

                // 3. appPath からの直接抽出
                if (!string.IsNullOrEmpty(appPath))
                {
                    string expandedApp = Environment.ExpandEnvironmentVariables(appPath).Trim('"', ' ');
                    if (File.Exists(expandedApp))
                    {
                        var hIcons = new nint[1];
                        var ids = new uint[1];
                        uint count = Win32Interop.PrivateExtractIcons(expandedApp, 0, 20, 20, hIcons, ids, 1, 0);
                        if (count > 0 && hIcons[0] != nint.Zero)
                        {
                            try { return IconThumbnailService.ConvertHIconToSoftwareBitmap(hIcons[0]); }
                            finally { Win32Interop.DestroyIcon(hIcons[0]); }
                        }

                        var shinfo = new Win32Interop.SHFILEINFOW();
                        Win32Interop.SHGetFileInfoW(
                            expandedApp,
                            0,
                            ref shinfo,
                            (uint)Marshal.SizeOf(shinfo),
                            Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_SMALLICON);
                        if (shinfo.hIcon != nint.Zero)
                        {
                            try { return IconThumbnailService.ConvertHIconToSoftwareBitmap(shinfo.hIcon); }
                            finally { Win32Interop.DestroyIcon(shinfo.hIcon); }
                        }
                    }
                }
            }
            catch
            {
                // ignored
            }
            return null;
        }

        public static void LaunchWithApp(OpenWithAppInfo app, string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            // 1. Shell COM ハンドラー経由での起動を試行 (Win32/UWP 双方に完全対応)
            if (app.AssocHandlerPtr != nint.Zero)
            {
                try
                {
                    int hrItem = Win32Interop.SHCreateItemFromParsingName(filePath, nint.Zero, in Win32Interop.IID_IShellItem, out nint pShellItem);
                    if (hrItem == 0 && pShellItem != nint.Zero)
                    {
                        try
                        {
                            int hrDO = Win32Interop.NativeCom.ShellItem_BindToHandler(pShellItem, nint.Zero, in Win32Interop.BHID_SFUIObject, in Win32Interop.IID_IDataObject, out nint pDataObject);
                            if (hrDO == 0 && pDataObject != nint.Zero)
                            {
                                try
                                {
                                    int hrInvoke = Win32Interop.NativeCom.AssocHandler_Invoke(app.AssocHandlerPtr, pDataObject);
                                    if (hrInvoke == 0) return;
                                }
                                finally
                                {
                                    Win32Interop.NativeCom.Release(pDataObject);
                                }
                            }
                        }
                        finally
                        {
                            Win32Interop.NativeCom.Release(pShellItem);
                        }
                    }
                }
                catch { }
            }

            // 2. フォールバック: Process.Start
            if (!string.IsNullOrWhiteSpace(app.AppPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = app.AppPath,
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        public static void LaunchWithApp(OpenWithAppInfo app, IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0) return;
            if (filePaths.Count == 1)
            {
                LaunchWithApp(app, filePaths[0]);
                return;
            }

            if (!string.IsNullOrWhiteSpace(app.AppPath))
            {
                try
                {
                    string args = string.Join(" ", filePaths.Select(p => $"\"{p}\""));
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = app.AppPath,
                        Arguments = args,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        public static void SearchMicrosoftStore(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(ext)) return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = $"ms-windows-store://assoc/?FileExt={ext}",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
