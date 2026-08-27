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
                path.Equals("RecycleBin", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("Network", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("::", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            {
                return path.TrimEnd('\\');
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
