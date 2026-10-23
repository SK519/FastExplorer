using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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

        public static async Task<string?> CreateFileFromTemplateAsync(string destinationDirectory, ShellNewTemplate template, nint ownerHwnd = 0)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
                return null;

            string baseName = $"新規 {template.DisplayName}{template.Extension}";
            string targetPath = GetUniquePath(destinationDirectory, baseName);
            string targetName = Path.GetFileName(targetPath);

            // 書き込み権限がない場合は管理者権限（UAC確認画面付き）で作成
            if (!ShellFileOperationHelper.HasWritePermission(destinationDirectory))
            {
                return await CreateElevatedFileFromTemplateAsync(destinationDirectory, targetPath, targetName, template, ownerHwnd);
            }

            try
            {
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
                    if (ExecuteShellNewCommand(template.Command, targetPath))
                    {
                        return targetPath;
                    }
                }

                File.WriteAllBytes(targetPath, Array.Empty<byte>());
                return targetPath;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is IOException ioe && (ioe.HResult & 0xFFFF) == 5))
            {
                Debug.WriteLine($"[ShellNew] Access denied, delegating to elevated shell operation: {ex.Message}");
                return await CreateElevatedFileFromTemplateAsync(destinationDirectory, targetPath, targetName, template, ownerHwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellNew] Error creating file: {ex.Message}");
                return null;
            }
        }

        private static async Task<string?> CreateElevatedFileFromTemplateAsync(
            string destinationDirectory,
            string targetPath,
            string targetName,
            ShellNewTemplate template,
            nint ownerHwnd)
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FastExplorer_NewItem_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string tempFile = Path.Combine(tempDir, targetName);

                if (!string.IsNullOrEmpty(template.TemplateFileName))
                {
                    string? templateFilePath = FindTemplateFile(template.TemplateFileName);
                    if (templateFilePath != null && File.Exists(templateFilePath))
                    {
                        File.Copy(templateFilePath, tempFile, true);
                    }
                    else
                    {
                        File.WriteAllBytes(tempFile, Array.Empty<byte>());
                    }
                }
                else if (template.Data != null && template.Data.Length > 0)
                {
                    File.WriteAllBytes(tempFile, template.Data);
                }
                else
                {
                    File.WriteAllBytes(tempFile, Array.Empty<byte>());
                }

                bool ok = await ShellFileOperationHelper.ExecuteCopyOrMoveAsync(new[] { tempFile }, destinationDirectory, isMove: true, ownerHwnd);
                try { Directory.Delete(tempDir, true); } catch { }
                return ok ? targetPath : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellNew] Elevated creation error: {ex.Message}");
                return null;
            }
        }

        public static string? CreateFileFromTemplate(string destinationDirectory, ShellNewTemplate template)
        {
            return CreateFileFromTemplateAsync(destinationDirectory, template).GetAwaiter().GetResult();
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

        private static bool ExecuteShellNewCommand(string command, string targetPath)
        {
            try
            {
                string trimmed = command.Trim();
                if (string.IsNullOrEmpty(trimmed)) return false;

                string exe;
                string argsTemplate;

                if (trimmed.StartsWith('"'))
                {
                    int closeQuote = trimmed.IndexOf('"', 1);
                    if (closeQuote > 1)
                    {
                        exe = trimmed.Substring(1, closeQuote - 1);
                        argsTemplate = trimmed.Substring(closeQuote + 1).TrimStart();
                    }
                    else
                    {
                        exe = trimmed.Trim('"');
                        argsTemplate = string.Empty;
                    }
                }
                else
                {
                    int spaceIdx = trimmed.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        exe = trimmed.Substring(0, spaceIdx);
                        argsTemplate = trimmed.Substring(spaceIdx + 1).TrimStart();
                    }
                    else
                    {
                        exe = trimmed;
                        argsTemplate = string.Empty;
                    }
                }

                string safeTarget = targetPath.Replace("\"", "");
                string args;
                if (argsTemplate.Contains("\"%1\""))
                {
                    args = argsTemplate.Replace("\"%1\"", $"\"{safeTarget}\"");
                }
                else if (argsTemplate.Contains("%1"))
                {
                    args = argsTemplate.Replace("%1", $"\"{safeTarget}\"");
                }
                else
                {
                    args = string.IsNullOrWhiteSpace(argsTemplate) ? $"\"{safeTarget}\"" : $"{argsTemplate} \"{safeTarget}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = true
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(4000);
                return File.Exists(targetPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellNew] Command execution error: {ex.Message}");
                return false;
            }
        }
    }
}
