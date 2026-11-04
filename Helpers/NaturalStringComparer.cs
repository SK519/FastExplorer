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

            // ファストパス: 先頭文字が異なる ASCII 英字の場合は P/Invoke を回避して即座に判定
            if (x.Length > 0 && y.Length > 0)
            {
                char c1 = x[0];
                char c2 = y[0];
                if (IsAsciiLetter(c1) && IsAsciiLetter(c2))
                {
                    char u1 = char.ToUpperInvariant(c1);
                    char u2 = char.ToUpperInvariant(c2);
                    if (u1 != u2)
                    {
                        return u1 - u2;
                    }
                }
            }

            return Win32Interop.StrCmpLogicalW(x, y);
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }
}
