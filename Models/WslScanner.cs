using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using FastExplorer.Core;

namespace FastExplorer.Models
{
    /// <summary>
    /// WSL (Windows Subsystem for Linux) パスのファイルスキャンを
    /// wsl.exe プロセス経由で行うスキャナー。
    /// <para>
    /// UNC パス (\\wsl.localhost\*) を直接 Win32 API や .NET Directory API で
    /// スキャンすると数分かかる場合があるため、wsl.exe -e ls を使うことで
    /// 数秒以内に結果を取得できる。
    /// </para>
    /// </summary>
    internal static class WslScanner
    {
        /// <summary>
        /// WSL の UNC パス (\\wsl.localhost\Ubuntu\home 等) をスキャンして FileItem リストを返す。
        /// \\wsl.localhost または \\wsl$ ルートの場合はディストリビューション一覧を返す。
        /// </summary>
        public static List<FileItem> ScanWslPath(string wslUncPath, bool showHidden)
        {
            var items = new List<FileItem>();

            if (string.IsNullOrWhiteSpace(wslUncPath)) return items;

            // \\wsl.localhost\Ubuntu\... → distro="Ubuntu", linuxPath="/"
            if (!TryParseWslUncPath(wslUncPath, out string distroName, out string linuxPath))
            {
                // \\wsl.localhost ルート → ディストリビューション一覧
                items.AddRange(GetDistroList());
                return items;
            }

            // ディストリビューション内のパスをスキャン
            items.AddRange(ScanDistroPath(distroName, linuxPath, wslUncPath.TrimEnd('\\'), showHidden));
            return items;
        }

        /// <summary>
        /// \\wsl.localhost\Ubuntu\home\user → distro="Ubuntu", linuxPath="/home/user"
        /// \\wsl.localhost\Ubuntu → distro="Ubuntu", linuxPath="/"
        /// \\wsl.localhost → false (ルート)
        /// </summary>
        private static bool TryParseWslUncPath(string uncPath, out string distroName, out string linuxPath)
        {
            distroName = string.Empty;
            linuxPath = "/";

            // パスを正規化 (末尾 \ を除去して分割)
            string normalized = uncPath.TrimEnd('\\');

            // "\\wsl.localhost" or "\\wsl$" — parts[0]="" parts[1]="" parts[2]="wsl.localhost"
            string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);

            // parts: ["wsl.localhost"] → ルートのみ → false
            if (parts.Length < 2) return false;

            // parts: ["wsl.localhost", "Ubuntu"] or ["wsl.localhost", "Ubuntu", "home", ...]
            distroName = parts[1];

            if (parts.Length == 2)
            {
                linuxPath = "/";
            }
            else
            {
                // ["wsl.localhost", "Ubuntu", "home", "user"] → "/home/user"
                linuxPath = "/" + string.Join("/", parts[2..]);
            }

            return true;
        }

        /// <summary>
        /// レジストリ + wsl.exe --list からディストリビューション一覧を取得
        /// </summary>
        private static List<FileItem> GetDistroList()
        {
            var items = new List<FileItem>();
            var distros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. レジストリから取得 (高速)
            try
            {
                using var lxssKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
                if (lxssKey != null)
                {
                    foreach (var subKeyName in lxssKey.GetSubKeyNames())
                    {
                        using var subKey = lxssKey.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("DistributionName") is string name && !string.IsNullOrWhiteSpace(name))
                        {
                            distros.Add(name);
                        }
                    }
                }
            }
            catch { }

            // 2. wsl.exe --list --quiet でフォールバック (0.1秒以内)
            if (distros.Count == 0)
            {
                try
                {
                    var psi = new ProcessStartInfo("wsl.exe", "--list --quiet")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.Unicode
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string name = line.Trim().TrimEnd('(').Trim();
                            // "(既定)" などを除去
                            int parenIdx = name.IndexOf('(');
                            if (parenIdx > 0) name = name[..parenIdx].Trim();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                distros.Add(name);
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var distro in distros)
            {
                items.Add(new FileItem
                {
                    Name = distro,
                    FullPath = $@"\\wsl.localhost\{distro}",
                    GlyphIcon = "\uE74C",
                    FileType = "Linux ディストリビューション",
                    IsDirectory = true
                });
            }

            return items;
        }

        /// <summary>
        /// wsl.exe -d {distro} -e ls -la1 --time-style=long-iso {linuxPath} の出力をパースして FileItem リストを返す
        /// </summary>
        private static List<FileItem> ScanDistroPath(string distro, string linuxPath, string uncBase, bool showHidden)
        {
            var items = new List<FileItem>();

            try
            {
                // wsl.exe -d Ubuntu -e ls -la1 --time-style=long-iso /home
                string args = $"-d {distro} -e ls -la1 --time-style=long-iso \"{linuxPath}\"";
                var psi = new ProcessStartInfo("wsl.exe", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false)
                };

                using var proc = Process.Start(psi);
                if (proc == null) return items;

                // タイムアウト付きで読み取り (WSL 起動に時間がかかることがある)
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30000);

                // 出力をパース
                // 例: "drwxr-xr-x  3 root root 4096 2026-08-02 15:12 home"
                // 例: "lrwxrwxrwx  1 root root    7 2026-04-20 17:46 bin -> usr/bin"
                foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.TrimEnd('\r');

                    // "total 2840" や空行をスキップ
                    if (line.StartsWith("total ", StringComparison.Ordinal) || line.Length == 0)
                        continue;

                    // スペースで分割 (8列以上を期待: perms links owner group size date time name...)
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 8) continue;

                    string perms = parts[0];  // drwxr-xr-x
                    // parts[1] = link count
                    // parts[2] = owner
                    // parts[3] = group
                    // parts[4] = size
                    // parts[5] = date (2026-08-02)
                    // parts[6] = time (15:12)
                    // parts[7..] = name (スペースを含む可能性あり)

                    string name = string.Join(" ", parts[7..]);

                    // シンボリックリンク "bin -> usr/bin" → "bin" に切り詰め
                    int arrowIdx = name.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrowIdx >= 0) name = name[..arrowIdx];

                    // . と .. をスキップ
                    if (name == "." || name == "..") continue;

                    // 隠しファイル (. 始まり) の処理
                    bool isHidden = name.StartsWith(".", StringComparison.Ordinal);
                    if (!showHidden && isHidden) continue;

                    bool isDirectory = perms.StartsWith("d", StringComparison.Ordinal)
                                    || perms.StartsWith("l", StringComparison.Ordinal); // symlink → フォルダとして扱う

                    long size = 0;
                    if (!isDirectory && long.TryParse(parts[4], out long parsedSize))
                    {
                        size = parsedSize;
                    }

                    DateTime dateModified = DateTime.MinValue;
                    if (parts.Length > 6 && DateTime.TryParse($"{parts[5]} {parts[6]}", out DateTime dt))
                    {
                        dateModified = dt;
                    }

                    // FullPath: UNC ベース + \ + name
                    string fullPath = uncBase.TrimEnd('\\') + '\\' + name;

                    string fileType;
                    string glyphIcon;
                    if (isDirectory)
                    {
                        fileType = "フォルダー";
                        glyphIcon = "\uE8B7";
                    }
                    else
                    {
                        string ext = Path.GetExtension(name);
                        fileType = NativeFileScanner.GetFileTypeDescription(ext);
                        glyphIcon = NativeFileScanner.GetGlyphIconForExtension(ext);
                    }

                    items.Add(new FileItem
                    {
                        Name = name,
                        FullPath = fullPath,
                        IsDirectory = isDirectory,
                        IsHidden = isHidden,
                        SizeInBytes = size,
                        DateModified = dateModified,
                        FileType = fileType,
                        GlyphIcon = glyphIcon
                    });
                }
            }
            catch { }

            return items;
        }
    }
}
