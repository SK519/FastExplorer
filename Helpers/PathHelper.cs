using System;
using System.IO;

namespace FastExplorer.Helpers
{
    public static class PathHelper
    {
        public static string NormalizeFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            if (path.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("RecycleBin", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                string root = Path.GetPathRoot(fullPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return root.TrimEnd('\\', '/') + "\\";
                }
                return fullPath.TrimEnd('\\', '/');
            }
            catch
            {
                return path.TrimEnd('\\', '/');
            }
        }
    }
}
