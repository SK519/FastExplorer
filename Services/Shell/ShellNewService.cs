using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FastExplorer.Services
{
    public class ShellNewTemplate
    {
        public string Extension { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? TemplateFileName { get; set; }
        public byte[]? Data { get; set; }
        public string? Command { get; set; }
        public bool IsNullFile { get; set; }
        public string Glyph { get; set; } = "\uE7C3";
    }

    public static class ShellNewService
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, nint ppvReserved);

        private static List<ShellNewTemplate>? _cachedTemplates;

        private static readonly HashSet<string> _ignoredExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk",
            ".library-ms",
            ".contact",
            ".zip",
            ".zfsendtotarget",
            ".jnt",
            ".desklink",
            ".mapimail",
            ".mydocs",
            ".sendmail"
        };

        public static List<ShellNewTemplate> GetShellNewTemplates()
        {
            if (_cachedTemplates != null) return _cachedTemplates;

            var templates = new List<ShellNewTemplate>();
            var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Explorer の ShellNew キャッシュを最優先で走査
            try
            {
                using var expKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew");
                if (expKey?.GetValue("Classes") is string[] classes)
                {
                    foreach (var cls in classes)
                    {
                        if (string.IsNullOrWhiteSpace(cls) || !cls.StartsWith('.')) continue;
                        string ext = cls.ToLowerInvariant();
                        if (_ignoredExtensions.Contains(ext) || seenExtensions.Contains(ext)) continue;

                        var template = TryGetTemplateForExtension(ext);
                        if (template != null)
                        {
                            seenExtensions.Add(ext);
                            templates.Add(template);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellNew] Error scanning explorer cache: {ex.Message}");
            }

            // 2. HKCR および HKCU\Software\Classes を走査
            var roots = new List<RegistryKey>();
            try
            {
                roots.Add(Registry.ClassesRoot);
                var hkcuClasses = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
                if (hkcuClasses != null) roots.Add(hkcuClasses);

                foreach (var root in roots)
                {
                    try
                    {
                        var subKeyNames = root.GetSubKeyNames();
                        foreach (var name in subKeyNames)
                        {
                            if (!name.StartsWith('.')) continue;
                            string ext = name.ToLowerInvariant();
                            if (_ignoredExtensions.Contains(ext) || seenExtensions.Contains(ext)) continue;

                            var template = TryGetTemplateForExtension(ext);
                            if (template != null)
                            {
                                seenExtensions.Add(ext);
                                templates.Add(template);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ShellNew] Error scanning registry root: {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var r in roots)
                {
                    if (r != Registry.ClassesRoot) r.Dispose();
                }
            }

            _cachedTemplates = templates
                .OrderBy(t => t.Extension == ".txt" ? 0 : 1)
                .ThenBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return _cachedTemplates;
        }

        private static ShellNewTemplate? TryGetTemplateForExtension(string ext)
        {
            var searchRoots = new List<RegistryKey> { Registry.ClassesRoot };
            var hkcuClasses = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (hkcuClasses != null) searchRoots.Add(hkcuClasses);

            try
            {
                foreach (var root in searchRoots)
                {
                    try
                    {
                        using var extKey = root.OpenSubKey(ext);
                        if (extKey == null) continue;

                        // 1. .ext\ShellNew
                        using var directShellNew = extKey.OpenSubKey("ShellNew");
                        if (directShellNew != null)
                        {
                            var t = ExtractTemplate(extKey, directShellNew, ext, root);
                            if (t != null) return t;
                        }

                        // 2. .ext\<ProgIdSubKey>\ShellNew (例: .docx\Word.Document.12\ShellNew)
                        foreach (var subName in extKey.GetSubKeyNames())
                        {
                            if (subName.Equals("ShellNew", StringComparison.OrdinalIgnoreCase)) continue;
                            using var subKey = extKey.OpenSubKey(subName);
                            using var subShellNew = subKey?.OpenSubKey("ShellNew");
                            if (subShellNew != null)
                            {
                                var t = ExtractTemplate(extKey, subShellNew, ext, root, subProgId: subName);
                                if (t != null) return t;
                            }
                        }

                        // 3. ProgID\ShellNew
                        string? progId = extKey.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(progId))
                        {
                            using var progKey = root.OpenSubKey(progId);
                            using var progShellNew = progKey?.OpenSubKey("ShellNew");
                            if (progShellNew != null)
                            {
                                var t = ExtractTemplate(extKey, progShellNew, ext, root, subProgId: progId);
                                if (t != null) return t;
                            }
                        }
                    }
                    catch { }
                }

                return null;
            }
            finally
            {
                hkcuClasses?.Dispose();
            }
        }

        private static ShellNewTemplate? ExtractTemplate(RegistryKey extKey, RegistryKey shellNewKey, string ext, RegistryKey root, string? subProgId = null)
        {
            try
            {
                var template = new ShellNewTemplate { Extension = ext };

                if (shellNewKey.GetValue("NullFile") != null)
                {
                    template.IsNullFile = true;
                }

                if (shellNewKey.GetValue("FileName") is string fileName)
                {
                    template.TemplateFileName = fileName;
                }

                if (shellNewKey.GetValue("Data") is byte[] data)
                {
                    template.Data = data;
                }
                else if (shellNewKey.GetValue("Data") is string strData)
                {
                    template.Data = Encoding.UTF8.GetBytes(strData);
                }

                if (shellNewKey.GetValue("Command") is string cmd)
                {
                    template.Command = cmd;
                }
                else if (shellNewKey.GetValue("command") is string lowerCmd)
                {
                    template.Command = lowerCmd;
                }

                if (!template.IsNullFile && string.IsNullOrEmpty(template.TemplateFileName) && template.Data == null && string.IsNullOrEmpty(template.Command))
                {
                    if (shellNewKey.GetValueNames().Length == 0) return null;
                    template.IsNullFile = true;
                }

                // 表示名の解決
                string? displayName = null;

                // 1. ShellNew\ItemName
                if (shellNewKey.GetValue("ItemName") is string itemName && !string.IsNullOrEmpty(itemName))
                {
                    displayName = ResolveIndirectString(itemName);
                }

                // 2. ProgID\FriendlyTypeName / ProgID Default
                string? progId = subProgId ?? (extKey.GetValue(null) as string);
                if (string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(progId))
                {
                    using var progKey = root.OpenSubKey(progId) ?? Registry.ClassesRoot.OpenSubKey(progId);
                    if (progKey != null)
                    {
                        if (progKey.GetValue("FriendlyTypeName") is string friendlyName && !string.IsNullOrEmpty(friendlyName))
                        {
                            displayName = ResolveIndirectString(friendlyName);
                        }
                        if (string.IsNullOrEmpty(displayName))
                        {
                            displayName = progKey.GetValue(null) as string;
                        }
                    }
                }

                // 3. extKey Default
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = extKey.GetValue(null) as string;
                }

                // 4. フォールバック
                if (string.IsNullOrEmpty(displayName) || displayName.Equals(progId, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = $"{ext.TrimStart('.').ToUpperInvariant()} ドキュメント";
                }

                template.DisplayName = displayName.Trim();
                template.Glyph = GetGlyphForExtension(ext);
                return template;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveIndirectString(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;

            if (source.StartsWith('@'))
            {
                var sb = new StringBuilder(512);
                int hr = SHLoadIndirectString(source, sb, (uint)sb.Capacity, nint.Zero);
                if (hr == 0 && sb.Length > 0)
                {
                    return sb.ToString();
                }
            }

            return source;
        }

        private static string GetGlyphForExtension(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".txt" or ".log" or ".md" => "\uE7C3",
                ".docx" or ".doc" or ".rtf" or ".gdoc" => "\uE8A5",
                ".xlsx" or ".xls" or ".csv" or ".gsheet" => "\uE8A5",
                ".pptx" or ".ppt" or ".gslides" => "\uE8A5",
                ".bmp" or ".png" or ".jpg" or ".jpeg" or ".gif" => "\uEB9F",
                ".zip" or ".7z" or ".rar" => "\uE8B7",
                _ => "\uE7C3"
            };
        }

        public static string? CreateFileFromTemplate(string destinationDirectory, ShellNewTemplate template)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                return null;

            try
            {
                string baseName = $"新規 {template.DisplayName}{template.Extension}";
                string targetPath = GetUniquePath(destinationDirectory, baseName);

                if (!string.IsNullOrEmpty(template.TemplateFileName))
                {
                    string? templateFilePath = FindTemplateFile(template.TemplateFileName);
                    if (templateFilePath != null && File.Exists(templateFilePath))
                    {
                        File.Copy(templateFilePath, targetPath, false);
                        return targetPath;
                    }
                }

                if (template.Data != null && template.Data.Length > 0)
                {
                    File.WriteAllBytes(targetPath, template.Data);
                    return targetPath;
                }

                if (!string.IsNullOrEmpty(template.Command))
                {
                    try
                    {
                        string cmd = template.Command.Replace("%1", targetPath);
                        var psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c \"{cmd}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var proc = Process.Start(psi);
                        proc?.WaitForExit(4000);
                        if (File.Exists(targetPath)) return targetPath;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ShellNew] Command execution error: {ex.Message}");
                    }
                }

                File.WriteAllBytes(targetPath, Array.Empty<byte>());
                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellNew] Error creating file: {ex.Message}");
                return null;
            }
        }

        private static string? FindTemplateFile(string fileName)
        {
            if (File.Exists(fileName)) return fileName;

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysTemplate = Path.Combine(winDir, "ShellNew", fileName);
            if (File.Exists(sysTemplate)) return sysTemplate;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userTemplate = Path.Combine(appData, @"Microsoft\Windows\Templates", fileName);
            if (File.Exists(userTemplate)) return userTemplate;

            return null;
        }

        private static string GetUniquePath(string dir, string name)
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
    }
}
