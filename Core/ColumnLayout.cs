using System;

namespace FastExplorer.Core
{
    public static class ColumnLayout
    {
        public static double NameWidth { get; set; } = 280;
        public static double DateWidth { get; set; } = 170;
        public static double TypeWidth { get; set; } = 140;
        public static double SizeWidth { get; set; } = 100;

        public static event Action? LayoutChanged;

        public static void NotifyChanged()
        {
            LayoutChanged?.Invoke();
        }
    }
}
