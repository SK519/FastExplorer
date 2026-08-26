using System;
using System.Collections.Generic;
using FastExplorer.Core;

namespace FastExplorer.Helpers
{
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            if (string.Equals(x, y, StringComparison.Ordinal)) return 0;
            return Win32Interop.StrCmpLogicalW(x, y);
        }
    }
}
