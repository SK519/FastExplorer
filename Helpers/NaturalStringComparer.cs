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
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return Win32Interop.StrCmpLogicalW(x, y);
        }
    }
}
