using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer
{
    public class FileItem : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        private string _glyphIcon = "\uE7C3";
        private string _name = string.Empty;
        private string _fullPath = string.Empty;
        private long _sizeInBytes;
        private string _formattedSize = string.Empty;
        private DateTime _dateModified;
        private string _formattedDateModified = "-";
        private string _fileType = string.Empty;
        private bool _isDirectory;
        private bool _isHidden;
        private bool _isReadOnly;
        private bool _isRenaming;
        private string _renameText = string.Empty;
        private string _originalLocation = string.Empty;
        private DateTime _dateDeleted;
        private string _formattedDateDeleted = "-";
        private bool _isRecycleBinItem;

        public string Name
        {
            get => _name;
            set
            {
                if (SetField(ref _name, value))
                {
                    RenameText = value;
                }
            }
        }

        public string FullPath
        {
            get => _fullPath;
            set => SetField(ref _fullPath, value);
        }

        public string GlyphIcon
        {
            get => _glyphIcon;
            set => SetField(ref _glyphIcon, value);
        }

        public long SizeInBytes
        {
            get => _sizeInBytes;
            set
            {
                if (SetField(ref _sizeInBytes, value))
                {
                    FormattedSize = _isDirectory ? string.Empty : FormatFileSize(value);
                }
            }
        }

        public string FormattedSize
        {
            get => _formattedSize;
            private set => SetField(ref _formattedSize, value);
        }

        public DateTime DateModified
        {
            get => _dateModified;
            set
            {
                if (SetField(ref _dateModified, value))
                {
                    FormattedDateModified = value == DateTime.MinValue ? "-" : value.ToString("yyyy/MM/dd HH:mm");
                }
            }
        }

        public string FormattedDateModified
        {
            get => _formattedDateModified;
            private set => SetField(ref _formattedDateModified, value);
        }

        public string FileType
        {
            get => _fileType;
            set => SetField(ref _fileType, value);
        }

        public string OriginalLocation
        {
            get => _originalLocation;
            set
            {
                if (SetField(ref _originalLocation, value))
                {
                    OnPropertyChanged(nameof(DisplayCol2Text));
                }
            }
        }

        public DateTime DateDeleted
        {
            get => _dateDeleted;
            set
            {
                if (SetField(ref _dateDeleted, value))
                {
                    FormattedDateDeleted = value == DateTime.MinValue ? "-" : value.ToString("yyyy/MM/dd HH:mm");
                    OnPropertyChanged(nameof(DisplayCol3Text));
                }
            }
        }

        public string FormattedDateDeleted
        {
            get => _formattedDateDeleted;
            private set
            {
                if (SetField(ref _formattedDateDeleted, value))
                {
                    OnPropertyChanged(nameof(DisplayCol3Text));
                }
            }
        }

        public bool IsRecycleBinItem
        {
            get => _isRecycleBinItem;
            set
            {
                if (SetField(ref _isRecycleBinItem, value))
                {
                    OnPropertyChanged(nameof(DisplayCol2Text));
                    OnPropertyChanged(nameof(DisplayCol3Text));
                }
            }
        }

        public string DisplayCol2Text => IsRecycleBinItem
            ? (string.IsNullOrEmpty(OriginalLocation) ? FormattedDateModified : OriginalLocation)
            : FormattedDateModified;

        public string DisplayCol3Text => IsRecycleBinItem
            ? (DateDeleted != DateTime.MinValue ? FormattedDateDeleted : FileType)
            : FileType;

        public bool IsDirectory
        {
            get => _isDirectory;
            set
            {
                if (SetField(ref _isDirectory, value))
                {
                    if (value)
                    {
                        FormattedSize = string.Empty;
                        GlyphIcon = "\uE8B7";
                    }
                    else
                    {
                        GlyphIcon = "\uE7C3";
                    }
                }
            }
        }

        private bool _isCut;
        public bool IsCut
        {
            get => _isCut;
            set
            {
                if (SetField(ref _isCut, value))
                {
                    OnPropertyChanged(nameof(Opacity));
                }
            }
        }

        public bool IsHidden
        {
            get => _isHidden;
            set
            {
                if (SetField(ref _isHidden, value))
                {
                    OnPropertyChanged(nameof(Opacity));
                }
            }
        }

        public double Opacity
        {
            get
            {
                if (_isCut) return 0.4;
                if (_isHidden) return 0.55;
                return 1.0;
            }
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => SetField(ref _isReadOnly, value);
        }

        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (SetField(ref _icon, value))
                {
                    OnPropertyChanged(nameof(FontIconVisibility));
                    OnPropertyChanged(nameof(ImageIconVisibility));
                }
            }
        }

        private bool _allowThumbnail = true;
        public bool AllowThumbnail
        {
            get => _allowThumbnail;
            set => SetField(ref _allowThumbnail, value);
        }

        public Visibility FontIconVisibility => Visibility.Collapsed;
        public Visibility ImageIconVisibility => string.IsNullOrEmpty(_emojiIcon) ? Visibility.Visible : Visibility.Collapsed;

        private double _itemIconSize = 48;
        public double ItemIconSize
        {
            get => _itemIconSize;
            set => SetField(ref _itemIconSize, value);
        }

        private double _itemWidth = 96;
        public double ItemWidth
        {
            get => _itemWidth;
            set => SetField(ref _itemWidth, value);
        }

        private double _itemHeight = 104;
        public double ItemHeight
        {
            get => _itemHeight;
            set => SetField(ref _itemHeight, value);
        }

        private double _itemFontSize = 11;
        public double ItemFontSize
        {
            get => _itemFontSize;
            set => SetField(ref _itemFontSize, value);
        }

        private double _rowHeight = 34;
        public double RowHeight
        {
            get => _rowHeight;
            set => SetField(ref _rowHeight, value);
        }

        private double _itemSubFontSize = 12;
        public double ItemSubFontSize
        {
            get => _itemSubFontSize;
            set => SetField(ref _itemSubFontSize, value);
        }

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

        private double _glyphFontSize = 34;
        public double GlyphFontSize
        {
            get => _glyphFontSize;
            set => SetField(ref _glyphFontSize, value);
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (SetField(ref _isRenaming, value))
                {
                    if (value) RenameText = _name;
                    OnPropertyChanged(nameof(DisplayNameVisibility));
                    OnPropertyChanged(nameof(RenameBoxVisibility));
                }
            }
        }

        public string RenameText
        {
            get => _renameText;
            set => SetField(ref _renameText, value);
        }

        public Visibility DisplayNameVisibility => _isRenaming ? Visibility.Collapsed : Visibility.Visible;
        public Visibility RenameBoxVisibility => _isRenaming ? Visibility.Visible : Visibility.Collapsed;

        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (SetField(ref _isPinned, value))
                {
                    OnPropertyChanged(nameof(PinIconVisibility));
                }
            }
        }

        private bool _isSeparator;
        public bool IsSeparator
        {
            get => _isSeparator;
            set
            {
                if (SetField(ref _isSeparator, value))
                {
                    OnPropertyChanged(nameof(SeparatorVisibility));
                    OnPropertyChanged(nameof(ItemContentVisibility));
                }
            }
        }

        private string _subtitle = string.Empty;
        public string Subtitle
        {
            get => _subtitle;
            set => SetField(ref _subtitle, value);
        }

        private bool _isExpandable;
        public bool IsExpandable
        {
            get => _isExpandable;
            set
            {
                if (SetField(ref _isExpandable, value))
                {
                    OnPropertyChanged(nameof(ExpandIconVisibility));
                }
            }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetField(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ExpandIconGlyph));
                }
            }
        }

        public string ExpandIconGlyph => _isExpanded ? "\uE70D" : "\uE76C"; // ChevronDown : ChevronRight
        public Visibility ExpandIconVisibility => _isExpandable ? Visibility.Visible : Visibility.Collapsed;

        private int _indentLevel;
        public int IndentLevel
        {
            get => _indentLevel;
            set
            {
                if (SetField(ref _indentLevel, value))
                {
                    OnPropertyChanged(nameof(IndentMargin));
                }
            }
        }

        public Thickness IndentMargin => _indentLevel > 0 ? new Thickness(_indentLevel * 14, 0, 0, 0) : new Thickness(0);

        public Visibility PinIconVisibility => _isPinned ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SeparatorVisibility => _isSeparator ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ItemContentVisibility => _isSeparator ? Visibility.Collapsed : Visibility.Visible;

        private string _emojiIcon = string.Empty;
        public string EmojiIcon
        {
            get => _emojiIcon;
            set
            {
                if (SetField(ref _emojiIcon, value))
                {
                    OnPropertyChanged(nameof(EmojiIconVisibility));
                    OnPropertyChanged(nameof(ImageIconVisibility));
                }
            }
        }

        public Visibility EmojiIconVisibility => !string.IsNullOrEmpty(_emojiIcon) ? Visibility.Visible : Visibility.Collapsed;

        public static Action? SelectionVisualsCallback;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetField(ref _isSelected, value))
                {
                    SelectionVisualsCallback?.Invoke();
                }
            }
        }

        private static bool _globalShowCheckBoxes;
        public static bool GlobalShowCheckBoxes
        {
            get => _globalShowCheckBoxes;
            set => _globalShowCheckBoxes = value;
        }

        public Visibility CheckBoxVisibility => GlobalShowCheckBoxes ? Visibility.Visible : Visibility.Collapsed;

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

        public object? Tag { get; set; }
        public string? ShortcutPath { get; set; }

        public void RefreshCheckBoxVisibility()
        {
            OnPropertyChanged(nameof(CheckBoxVisibility));
            OnPropertyChanged(nameof(RowWidth));
        }

        public string Extension
        {
            get
            {
                if (_isDirectory) return string.Empty;
                int dotIndex = _name.LastIndexOf('.');
                return dotIndex >= 0 ? _name[dotIndex..].ToLowerInvariant() : string.Empty;
            }
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
