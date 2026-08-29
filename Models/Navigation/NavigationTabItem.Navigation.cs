using System;
using System.Collections.Generic;
using System.IO;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
        public void GoBack()
        {
            if (_backStack.Count == 0) return;
            string prev = _backStack[^1];
            _backStack.RemoveAt(_backStack.Count - 1);
            if (!string.IsNullOrEmpty(_currentPath))
            {
                _forwardStack.Add(_currentPath);
            }
            NavigateTo(prev, false);
        }

        public void GoForward()
        {
            if (_forwardStack.Count == 0) return;
            string next = _forwardStack[^1];
            _forwardStack.RemoveAt(_forwardStack.Count - 1);
            if (!string.IsNullOrEmpty(_currentPath))
            {
                _backStack.Add(_currentPath);
            }
            NavigateTo(next, false);
        }

        public void GoUp()
        {
            if (CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase)) return;

            if (RecycleBinService.IsRecycleBinPath(CurrentPath))
            {
                NavigateTo("ThisPC");
                return;
            }

            if (Services.ArchiveService.IsArchiveOrSubPath(CurrentPath, out string archiveFile, out string internalSubPath))
            {
                if (string.IsNullOrEmpty(internalSubPath))
                {
                    string? parent = Path.GetDirectoryName(archiveFile);
                    NavigateTo(string.IsNullOrEmpty(parent) ? "ThisPC" : parent);
                }
                else
                {
                    string? parentSub = Path.GetDirectoryName(CurrentPath);
                    NavigateTo(string.IsNullOrEmpty(parentSub) ? archiveFile : parentSub);
                }
                return;
            }

            if (CurrentPath.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
            {
                int lastSlash = CurrentPath.LastIndexOf('\\');
                if (lastSlash > 2)
                {
                    NavigateTo(CurrentPath[..lastSlash]);
                }
                else
                {
                    NavigateTo("ThisPC");
                }
                return;
            }

            try
            {
                var parent = Directory.GetParent(CurrentPath);
                if (parent != null)
                {
                    NavigateTo(parent.FullName);
                }
                else
                {
                    NavigateTo("ThisPC");
                }
            }
            catch
            {
                NavigateTo("ThisPC");
            }
        }

        private void UpdateBreadcrumbs(string path)
        {
            Breadcrumbs.Clear();
            if (string.IsNullOrEmpty(path)) return;

            if (path.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                Breadcrumbs.Add(new BreadcrumbItem { Label = "ホーム", FullPath = "Home", Glyph = "\uE80F" });
                return;
            }

            if (RecycleBinService.IsRecycleBinPath(path))
            {
                Breadcrumbs.Add(new BreadcrumbItem { Label = "PC", FullPath = "ThisPC", Glyph = "\uE770" });
                Breadcrumbs.Add(new BreadcrumbItem { Label = "ごみ箱", FullPath = RecycleBinService.RecycleBinUri, Glyph = "\uE74D" });
                return;
            }

            if (path.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || path.Equals("Network", StringComparison.OrdinalIgnoreCase))
            {
                Breadcrumbs.Add(new BreadcrumbItem { Label = "ネットワーク", FullPath = "shell:NetworkPlacesFolder", Glyph = "\uE968" });
                return;
            }

            Breadcrumbs.Add(new BreadcrumbItem { Label = "PC", FullPath = "ThisPC", Glyph = "\uE770" });

            if (path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // アーカイブまたは内部パスの場合
            if (Services.ArchiveService.IsArchiveOrSubPath(path, out string archiveFile, out string internalSubPath))
            {
                // アーカイブファイルまでの親パスを構築
                try
                {
                    string? dir = Path.GetDirectoryName(archiveFile);
                    var segments = new List<string>();
                    var cur = dir;
                    while (!string.IsNullOrEmpty(cur))
                    {
                        segments.Add(cur);
                        string? parent = Path.GetDirectoryName(cur);
                        if (parent == cur) break;
                        cur = parent;
                    }
                    segments.Reverse();

                    foreach (var s in segments)
                    {
                        string name = Path.GetFileName(s);
                        bool isDrive = string.IsNullOrEmpty(name);
                        Breadcrumbs.Add(new BreadcrumbItem
                        {
                            Label = isDrive ? s.TrimEnd('\\') : name,
                            FullPath = s,
                            Glyph = isDrive ? "\uEDA2" : "\uE8B7"
                        });
                    }

                    // アーカイブファイル本体
                    Breadcrumbs.Add(new BreadcrumbItem
                    {
                        Label = Path.GetFileName(archiveFile),
                        FullPath = archiveFile,
                        Glyph = "\uF126"
                    });

                    // 内部サブパス
                    if (!string.IsNullOrEmpty(internalSubPath))
                    {
                        string[] parts = internalSubPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                        string acc = archiveFile;
                        foreach (var part in parts)
                        {
                            acc = Path.Combine(acc, part);
                            Breadcrumbs.Add(new BreadcrumbItem
                            {
                                Label = part,
                                FullPath = acc,
                                Glyph = "\uE8B7"
                            });
                        }
                    }
                }
                catch
                {
                    Breadcrumbs.Add(new BreadcrumbItem { Label = path, FullPath = path, Glyph = "\uF126" });
                }
                return;
            }

            // WSL / UNC パスの場合
            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string accumulated = @"\\";
                for (int i = 0; i < parts.Length; i++)
                {
                    accumulated = (i == 0) ? @"\\" + parts[0] : accumulated + "\\" + parts[i];
                    string label = parts[i];
                    string glyph = "\uE8B7";

                    if (i == 0 && (label.Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase) || label.Equals("wsl$", StringComparison.OrdinalIgnoreCase)))
                    {
                        label = "Linux (WSL)";
                        glyph = "\uE74C";
                    }
                    else if (i == 1 && parts[0].StartsWith("wsl", StringComparison.OrdinalIgnoreCase))
                    {
                        glyph = "\uE74C";
                    }

                    Breadcrumbs.Add(new BreadcrumbItem
                    {
                        Label = label,
                        FullPath = accumulated,
                        Glyph = glyph
                    });
                }
                return;
            }

            try
            {
                var dir = new DirectoryInfo(path);
                var segments = new List<DirectoryInfo>();
                var current = dir;
                while (current != null)
                {
                    segments.Add(current);
                    current = current.Parent;
                }
                segments.Reverse();

                foreach (var seg in segments)
                {
                    string label = seg.Name;
                    bool isDrive = seg.Parent == null;
                    if (isDrive)
                    {
                        label = seg.FullName.TrimEnd('\\');
                    }
                    Breadcrumbs.Add(new BreadcrumbItem
                    {
                        Label = label,
                        FullPath = seg.FullName,
                        Glyph = isDrive ? "\uEDA2" : "\uE8B7"
                    });
                }
            }
            catch
            {
                Breadcrumbs.Add(new BreadcrumbItem { Label = path, FullPath = path, Glyph = "\uE8B7" });
            }
        }

        private void UpdateNavigationState()
        {
            CanGoBack = _backStack.Count > 0;
            CanGoForward = _forwardStack.Count > 0;
            CanGoUp = !CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) && !CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase);
        }
    }
}
