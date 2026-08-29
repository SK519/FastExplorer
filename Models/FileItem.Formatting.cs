using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FastExplorer
{
    public partial class FileItem
    {
        public void ApplyDetailsScale(ViewScaleLevel scale)
        {
            switch (scale)
            {
                case ViewScaleLevel.Compact:
                    RowHeight = 28;
                    ItemIconSize = 16;
                    GlyphFontSize = 12;
                    ItemFontSize = 11.5;
                    ItemSubFontSize = 11;
                    break;
                case ViewScaleLevel.Large:
                    RowHeight = 42;
                    ItemIconSize = 24;
                    GlyphFontSize = 17;
                    ItemFontSize = 14;
                    ItemSubFontSize = 13;
                    break;
                case ViewScaleLevel.ExtraLarge:
                    RowHeight = 52;
                    ItemIconSize = 32;
                    GlyphFontSize = 22;
                    ItemFontSize = 16;
                    ItemSubFontSize = 14;
                    break;
                default: // Normal
                    RowHeight = 34;
                    ItemIconSize = 20;
                    GlyphFontSize = 14;
                    ItemFontSize = 13;
                    ItemSubFontSize = 12;
                    break;
            }
        }

        public double ColNameWidth => FastExplorer.Core.ColumnLayout.NameWidth;
        public double ColDateWidth => FastExplorer.Core.ColumnLayout.DateWidth;
        public double ColTypeWidth => FastExplorer.Core.ColumnLayout.TypeWidth;
        public double ColSizeWidth => FastExplorer.Core.ColumnLayout.SizeWidth;
        public double RowWidth => ColNameWidth + ColDateWidth + ColTypeWidth + ColSizeWidth + (GlobalShowCheckBoxes ? 80 : 54);

        public void RefreshColumnWidths()
        {
            OnPropertyChanged(nameof(ColNameWidth));
            OnPropertyChanged(nameof(ColDateWidth));
            OnPropertyChanged(nameof(ColTypeWidth));
            OnPropertyChanged(nameof(ColSizeWidth));
            OnPropertyChanged(nameof(RowWidth));
        }

        public void RefreshCheckBoxVisibility()
        {
            OnPropertyChanged(nameof(CheckBoxVisibility));
            OnPropertyChanged(nameof(RowWidth));
        }

        private static readonly string[] FileSizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

        public static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < FileSizeUnits.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {FileSizeUnits[order]}";
        }

        public static string FormatDiskSpace(long bytes)
        {
            if (bytes <= 0) return "0 GB";
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1000.0)
            {
                double tb = gb / 1024.0;
                return $"{tb:0.##} TB";
            }
            else if (gb >= 1.0)
            {
                return $"{Math.Round(gb)} GB";
            }
            else
            {
                double mb = bytes / (1024.0 * 1024.0);
                return $"{Math.Round(mb)} MB";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> _eventArgsCache = new();

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (PropertyChanged != null && propertyName != null)
            {
                var args = _eventArgsCache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name));
                PropertyChanged(this, args);
            }
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
