using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer
{
    public partial class FilePropertiesInfo : INotifyPropertyChanged
    {
        private readonly List<string> _paths = [];
        private PropertyTargetType _targetType;
        private ImageSource? _icon;
        private string _glyphIcon = "\uE7C3";
        private string _name = string.Empty;
        private string _originalName = string.Empty;
        private string _itemType = string.Empty;
        private string _opensWith = string.Empty;
        private ImageSource? _opensWithIcon;
        private string _location = string.Empty;
        private string _fullPath = string.Empty;
        private long _size;
        private string _formattedSize = "-";
        private long _sizeOnDisk;
        private string _formattedSizeOnDisk = "-";
        private long _fileCount;
        private long _folderCount;
        private string _formattedContains = string.Empty;
        private bool _isCalculatingSize;
        private string _calculationStatus = string.Empty;

        private DateTime _dateCreated;
        private string _formattedDateCreated = "-";
        private DateTime _dateModified;
        private string _formattedDateModified = "-";
        private DateTime _dateAccessed;
        private string _formattedDateAccessed = "-";

        private bool? _isReadOnly;
        private bool? _isHidden;
        private bool? _originalIsReadOnly;
        private bool? _originalIsHidden;

        // Drive Properties
        private string _fileSystem = string.Empty;
        private long _usedSpace;
        private string _formattedUsedSpace = "-";
        private long _freeSpace;
        private string _formattedFreeSpace = "-";
        private long _totalSpace;
        private string _formattedTotalSpace = "-";
        private double _usedPercentage;

        // Hash Properties
        private string _sha256Hash = string.Empty;
        private string _md5Hash = string.Empty;
        private bool _isCalculatingHash;

        // Security & Details & Signatures
        public ObservableCollection<SecurityPrincipalPermission> SecurityPrincipals { get; } = [];
        public ObservableCollection<FileDetailProperty> DetailsList { get; } = [];
        public ObservableCollection<DigitalSignatureItem> DigitalSignatures { get; } = [];
        public bool HasDigitalSignatures => DigitalSignatures.Count > 0;

        private CancellationTokenSource? _sizeCalculationCts;
        private CancellationTokenSource? _hashCalculationCts;
        private DispatcherQueue? _dispatcherQueue;

        public event PropertyChangedEventHandler? PropertyChanged;
        public static event Action<IReadOnlyList<string>>? FilePropertiesChanged;

        public IReadOnlyList<string> Paths => _paths;
        public PropertyTargetType TargetType
        {
            get => _targetType;
            private set => SetField(ref _targetType, value);
        }

        public ImageSource? Icon
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        public string GlyphIcon
        {
            get => _glyphIcon;
            set => SetField(ref _glyphIcon, value);
        }

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public string OriginalName => _originalName;

        public string ItemType
        {
            get => _itemType;
            set => SetField(ref _itemType, value);
        }

        public string OpensWith
        {
            get => _opensWith;
            set => SetField(ref _opensWith, value);
        }

        public ImageSource? OpensWithIcon
        {
            get => _opensWithIcon;
            set => SetField(ref _opensWithIcon, value);
        }

        public string Location
        {
            get => _location;
            set => SetField(ref _location, value);
        }

        public string FullPath
        {
            get => _fullPath;
            set => SetField(ref _fullPath, value);
        }

        public long Size
        {
            get => _size;
            set
            {
                if (SetField(ref _size, value))
                {
                    FormattedSize = FormatBytesWithExact(value);
                }
            }
        }

        public string FormattedSize
        {
            get => _formattedSize;
            private set => SetField(ref _formattedSize, value);
        }

        public long SizeOnDisk
        {
            get => _sizeOnDisk;
            set
            {
                if (SetField(ref _sizeOnDisk, value))
                {
                    FormattedSizeOnDisk = FormatBytesWithExact(value);
                }
            }
        }

        public string FormattedSizeOnDisk
        {
            get => _formattedSizeOnDisk;
            private set => SetField(ref _formattedSizeOnDisk, value);
        }

        public long FileCount
        {
            get => _fileCount;
            set
            {
                if (SetField(ref _fileCount, value))
                {
                    UpdateFormattedContains();
                }
            }
        }

        public long FolderCount
        {
            get => _folderCount;
            set
            {
                if (SetField(ref _folderCount, value))
                {
                    UpdateFormattedContains();
                }
            }
        }

        public string FormattedContains
        {
            get => _formattedContains;
            private set => SetField(ref _formattedContains, value);
        }

        public bool IsCalculatingSize
        {
            get => _isCalculatingSize;
            set => SetField(ref _isCalculatingSize, value);
        }

        public string CalculationStatus
        {
            get => _calculationStatus;
            set => SetField(ref _calculationStatus, value);
        }

        public DateTime DateCreated
        {
            get => _dateCreated;
            set
            {
                if (SetField(ref _dateCreated, value))
                {
                    FormattedDateCreated = value == DateTime.MinValue ? "-" : value.ToString("yyyy'年'M'月'd'日'、HH:mm:ss");
                }
            }
        }

        public string FormattedDateCreated
        {
            get => _formattedDateCreated;
            private set => SetField(ref _formattedDateCreated, value);
        }

        public DateTime DateModified
        {
            get => _dateModified;
            set
            {
                if (SetField(ref _dateModified, value))
                {
                    FormattedDateModified = value == DateTime.MinValue ? "-" : value.ToString("yyyy'年'M'月'd'日'、HH:mm:ss");
                }
            }
        }

        public string FormattedDateModified
        {
            get => _formattedDateModified;
            private set => SetField(ref _formattedDateModified, value);
        }

        public DateTime DateAccessed
        {
            get => _dateAccessed;
            set
            {
                if (SetField(ref _dateAccessed, value))
                {
                    FormattedDateAccessed = value == DateTime.MinValue ? "-" : value.ToString("yyyy'年'M'月'd'日'、HH:mm:ss");
                }
            }
        }

        public string FormattedDateAccessed
        {
            get => _formattedDateAccessed;
            private set => SetField(ref _formattedDateAccessed, value);
        }

        public bool? IsReadOnly
        {
            get => _isReadOnly;
            set => SetField(ref _isReadOnly, value);
        }

        public bool? IsHidden
        {
            get => _isHidden;
            set => SetField(ref _isHidden, value);
        }

        // Drive Properties
        public string FileSystem
        {
            get => _fileSystem;
            set => SetField(ref _fileSystem, value);
        }

        public long UsedSpace
        {
            get => _usedSpace;
            set
            {
                if (SetField(ref _usedSpace, value))
                {
                    FormattedUsedSpace = FormatBytesWithExact(value);
                }
            }
        }

        public string FormattedUsedSpace
        {
            get => _formattedUsedSpace;
            private set => SetField(ref _formattedUsedSpace, value);
        }

        public long FreeSpace
        {
            get => _freeSpace;
            set
            {
                if (SetField(ref _freeSpace, value))
                {
                    FormattedFreeSpace = FormatBytesWithExact(value);
                }
            }
        }

        public string FormattedFreeSpace
        {
            get => _formattedFreeSpace;
            private set => SetField(ref _formattedFreeSpace, value);
        }

        public long TotalSpace
        {
            get => _totalSpace;
            set
            {
                if (SetField(ref _totalSpace, value))
                {
                    FormattedTotalSpace = FormatBytesWithExact(value);
                }
            }
        }

        public string FormattedTotalSpace
        {
            get => _formattedTotalSpace;
            private set => SetField(ref _formattedTotalSpace, value);
        }

        public double UsedPercentage
        {
            get => _usedPercentage;
            set => SetField(ref _usedPercentage, value);
        }

        // Hash Properties
        public string Sha256Hash
        {
            get => _sha256Hash;
            set => SetField(ref _sha256Hash, value);
        }

        public string Md5Hash
        {
            get => _md5Hash;
            set => SetField(ref _md5Hash, value);
        }

        public bool IsCalculatingHash
        {
            get => _isCalculatingHash;
            set => SetField(ref _isCalculatingHash, value);
        }

        // Visibility Flags for View
        public bool IsSingleFile => TargetType == PropertyTargetType.SingleFile;
        public bool IsSingleFolder => TargetType == PropertyTargetType.SingleFolder;
        public bool IsMultiple => TargetType == PropertyTargetType.MultipleItems;
        public bool IsDrive => TargetType == PropertyTargetType.Drive;
        public bool ShowNameTextBox => TargetType == PropertyTargetType.SingleFile || TargetType == PropertyTargetType.SingleFolder;
        public bool ShowDates => TargetType == PropertyTargetType.SingleFile || TargetType == PropertyTargetType.SingleFolder;
        public bool ShowAttributes => TargetType != PropertyTargetType.Drive;
        public bool ShowContains => TargetType == PropertyTargetType.SingleFolder || TargetType == PropertyTargetType.MultipleItems;
        public bool ShowOpensWith => TargetType == PropertyTargetType.SingleFile;
        public bool ShowSizeOnDisk => TargetType == PropertyTargetType.SingleFile || TargetType == PropertyTargetType.SingleFolder;
        public bool ShowSecurityTab => TargetType != PropertyTargetType.MultipleItems;
        public bool ShowDetailsTab => TargetType == PropertyTargetType.SingleFile;

        private void UpdateFormattedContains()
        {
            FormattedContains = $"{_fileCount:N0} 個のファイル、{_folderCount:N0} 個のフォルダー";
        }

        public static async Task<FilePropertiesInfo> CreateAsync(IReadOnlyList<string> paths, DispatcherQueue dispatcherQueue)
        {
            var info = new FilePropertiesInfo
            {
                _dispatcherQueue = dispatcherQueue
            };
            info._paths.AddRange(paths.Where(p => !string.IsNullOrWhiteSpace(p)));

            if (info._paths.Count == 0)
            {
                info.Name = "不明なアイテム";
                info.ItemType = "プロパティなし";
                return info;
            }

            if (info._paths.Count == 1)
            {
                string path = info._paths[0];
                info.FullPath = path;

                if (IsDrivePath(path))
                {
                    info.TargetType = PropertyTargetType.Drive;
                    info.GlyphIcon = "\uEDA2";
                    info.LoadDriveProperties(path);
                }
                else if (Directory.Exists(path))
                {
                    info.TargetType = PropertyTargetType.SingleFolder;
                    info.GlyphIcon = "\uE8B7";
                    info.LoadFolderProperties(path);
                }
                else
                {
                    info.TargetType = PropertyTargetType.SingleFile;
                    info.GlyphIcon = "\uE7C3";
                    info.LoadFileProperties(path);
                }
            }
            else
            {
                info.TargetType = PropertyTargetType.MultipleItems;
                info.GlyphIcon = "\uE8B7";
                info.LoadMultipleProperties(info._paths);
            }

            // 大アイコンの非同期ロード
            await info.LoadIconAsync();

            // セキュリティ情報のロード
            if (info.ShowSecurityTab && !string.IsNullOrEmpty(info.FullPath))
            {
                info.LoadSecurityInfo(info.FullPath);
            }

            // 詳細メタデータのロード
            if (info.ShowDetailsTab && File.Exists(info.FullPath))
            {
                await info.LoadDetailsAsync(info.FullPath);
            }

            return info;
        }

        private static bool IsDrivePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string trimmed = path.TrimEnd('\\', '/');
            return trimmed.Length == 2 && trimmed[1] == ':';
        }

        public bool ApplyChanges(out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                if (ShowNameTextBox && !string.IsNullOrWhiteSpace(Name) && Name != _originalName)
                {
                    string trimmedName = Name.Trim();
                    string dir = Location;
                    if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(FullPath))
                    {
                        dir = Path.GetDirectoryName(FullPath) ?? string.Empty;
                    }

                    string newPath = Path.Combine(dir, trimmedName);
                    if (TargetType == PropertyTargetType.SingleFolder)
                    {
                        Directory.Move(FullPath, newPath);
                    }
                    else if (TargetType == PropertyTargetType.SingleFile)
                    {
                        File.Move(FullPath, newPath);
                    }

                    FullPath = newPath;
                    _originalName = trimmedName;
                    _paths[0] = newPath;
                }

                bool roChanged = IsReadOnly != _originalIsReadOnly && IsReadOnly.HasValue;
                bool hdChanged = IsHidden != _originalIsHidden && IsHidden.HasValue;

                if (roChanged || hdChanged)
                {
                    foreach (var path in _paths)
                    {
                        if (File.Exists(path) || Directory.Exists(path))
                        {
                            var attr = File.GetAttributes(path);

                            if (roChanged)
                            {
                                if (IsReadOnly!.Value) attr |= FileAttributes.ReadOnly;
                                else attr &= ~FileAttributes.ReadOnly;
                            }

                            if (hdChanged)
                            {
                                if (IsHidden!.Value) attr |= FileAttributes.Hidden;
                                else attr &= ~FileAttributes.Hidden;
                            }

                            File.SetAttributes(path, attr);
                        }
                    }

                    _originalIsReadOnly = IsReadOnly;
                    _originalIsHidden = IsHidden;
                }

                FilePropertiesChanged?.Invoke(_paths);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public void CancelOperations()
        {
            _sizeCalculationCts?.Cancel();
            _hashCalculationCts?.Cancel();
        }

        private static long CalculateSizeOnDisk(long size)
        {
            if (size <= 0) return 0;
            const long clusterSize = 4096;
            return ((size + clusterSize - 1) / clusterSize) * clusterSize;
        }

        public static string FormatBytesWithExact(long bytes)
        {
            if (bytes < 0) return "-";
            string formatted = FormatFileSize(bytes);
            return $"{formatted} ({bytes:N0} バイト)";
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes < 0) return "-";
            string[] sizes = ["B", "KB", "MB", "GB", "TB", "PB"];
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
