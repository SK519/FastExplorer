using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
        public void Refresh()
        {
            RefreshIncremental();
        }

        public void RefreshIncremental()
        {
            try
            {
                var newScanned = new List<FileItem>(1024);

                if (CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    newScanned.AddRange(QuickAccessService.GetHomeItems());
                }
                else if (CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    newScanned.AddRange(NativeFileScanner.GetDrives());
                }
                else if (RecycleBinService.IsRecycleBinPath(CurrentPath))
                {
                    newScanned.AddRange(RecycleBinService.GetRecycleBinItems());
                }
                else if (CurrentPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
                {
                    newScanned.AddRange(NativeFileScanner.GetNetworkPlaces());
                }
                else if (ArchiveService.IsArchiveOrSubPath(CurrentPath, out string archiveFile, out string internalSubPath))
                {
                    newScanned.AddRange(ArchiveService.GetArchiveFolderItems(archiveFile, internalSubPath));
                }
                else
                {
                    bool showHidden = ConfigService.Current.Ui.ShowHiddenFiles;
                    newScanned.AddRange(NativeFileScanner.ScanDirectory(CurrentPath, showHidden));
                }

                // 1. _allItems のインプレース差分同期
                var newLookup = new Dictionary<string, FileItem>(newScanned.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var item in newScanned)
                {
                    newLookup[item.FullPath] = item;
                }

                // 削除されたアイテムを _allItems から除去
                for (int i = _allItems.Count - 1; i >= 0; i--)
                {
                    var existing = _allItems[i];
                    if (!newLookup.ContainsKey(existing.FullPath))
                    {
                        _allItems.RemoveAt(i);
                    }
                }

                var existingLookup = new Dictionary<string, FileItem>(_allItems.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var item in _allItems)
                {
                    existingLookup[item.FullPath] = item;
                }

                // 新規アイテムの追加 & 既存アイテムのプロパティ更新
                bool allowThumb = IconThumbnailService.IsImageOrientedMode(_viewMode);
                foreach (var newItem in newScanned)
                {
                    newItem.IsCut = FileOperationService.IsPathCut(newItem.FullPath);
                    if (existingLookup.TryGetValue(newItem.FullPath, out var existingItem))
                    {
                        existingItem.IsCut = newItem.IsCut;
                        if (existingItem.DateModified != newItem.DateModified)
                        {
                            existingItem.DateModified = newItem.DateModified;
                        }
                        if (existingItem.SizeInBytes != newItem.SizeInBytes)
                        {
                            existingItem.SizeInBytes = newItem.SizeInBytes;
                        }
                        if (existingItem.FileType != newItem.FileType)
                        {
                            existingItem.FileType = newItem.FileType;
                        }
                    }
                    else
                    {
                        newItem.AllowThumbnail = allowThumb;
                        IconThumbnailService.Instance.ApplyImmediateDefaultIcon(newItem);
                        _allItems.Add(newItem);
                        IconThumbnailService.Instance.Enqueue(newItem);
                    }
                }

                // 2. Items コレクションの差分同期 (スクロール位置・選択状態を完全維持)
                SyncFilteredItems();
            }
            catch
            {
                LoadItems();
            }
        }

        private void SyncFilteredItems()
        {
            List<FileItem> targetList;

            if (!string.IsNullOrWhiteSpace(_filterText))
            {
                targetList = new List<FileItem>(_allItems.Count);
                foreach (var item in _allItems)
                {
                    if (item.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                    {
                        targetList.Add(item);
                    }
                }
            }
            else
            {
                targetList = new List<FileItem>(_allItems);
            }

            // フォルダを先頭に、その中で高速直接ソート (Windows Explorer 準拠の自然順ソート)
            var sortCol = CurrentSortColumn;
            bool asc = IsSortAscending;

            targetList.Sort((a, b) =>
            {
                // 1. フォルダーを常にファイルより前に配置 (Filter-first)
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }

                int cmp = 0;
                switch (sortCol)
                {
                    case SortColumn.Name:
                        cmp = FastExplorer.Helpers.NaturalStringComparer.Instance.Compare(a.Name, b.Name);
                        break;
                    case SortColumn.DateModified:
                        cmp = a.DateModified.CompareTo(b.DateModified);
                        break;
                    case SortColumn.FileType:
                        cmp = string.Compare(a.FileType, b.FileType, StringComparison.OrdinalIgnoreCase);
                        break;
                    case SortColumn.Size:
                        cmp = a.SizeInBytes.CompareTo(b.SizeInBytes);
                        break;
                }

                if (!asc) cmp = -cmp;

                // 2. 値が同一の場合は名前の自然順でタイブレーク
                if (cmp == 0 && sortCol != SortColumn.Name)
                {
                    cmp = FastExplorer.Helpers.NaturalStringComparer.Instance.Compare(a.Name, b.Name);
                }

                return cmp;
            });

            bool isGrid = ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons;
            foreach (var item in targetList)
            {
                ApplySizeToItem(item, CustomSize, isGrid, ViewMode);
                item.ApplyDetailsScale(_viewScale);
            }

            // Items コレクションの同期 (初回読み込み時は一括、差分時は最小の変更でスクロール位置・選択状態を維持)
            if (Items.Count == 0)
            {
                foreach (var item in targetList)
                {
                    Items.Add(item);
                }
            }
            else
            {
                var targetPathSet = new HashSet<string>(targetList.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var item in targetList)
                {
                    targetPathSet.Add(item.FullPath);
                }

                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (!targetPathSet.Contains(Items[i].FullPath))
                    {
                        Items.RemoveAt(i);
                    }
                }

                for (int targetIndex = 0; targetIndex < targetList.Count; targetIndex++)
                {
                    var targetItem = targetList[targetIndex];
                    if (targetIndex < Items.Count)
                    {
                        if (string.Equals(Items[targetIndex].FullPath, targetItem.FullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int existingIndex = -1;
                        for (int j = targetIndex + 1; j < Items.Count; j++)
                        {
                            if (string.Equals(Items[j].FullPath, targetItem.FullPath, StringComparison.OrdinalIgnoreCase))
                            {
                                existingIndex = j;
                                break;
                            }
                        }

                        if (existingIndex >= 0)
                        {
                            Items.Move(existingIndex, targetIndex);
                        }
                        else
                        {
                            Items.Insert(targetIndex, targetItem);
                        }
                    }
                    else
                    {
                        Items.Add(targetItem);
                    }
                }
            }

            UpdateStatusText();
        }

        private void LoadItems()
        {
            IsLoading = true;
            _allItems.Clear();
            Items.Clear();

            try
            {
                if (CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    var homeItems = QuickAccessService.GetHomeItems();
                    _allItems.AddRange(homeItems);
                }
                else if (CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    var drives = NativeFileScanner.GetDrives();
                    _allItems.AddRange(drives);
                }
                else if (RecycleBinService.IsRecycleBinPath(CurrentPath))
                {
                    var recycleItems = RecycleBinService.GetRecycleBinItems();
                    _allItems.AddRange(recycleItems);
                }
                else if (CurrentPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
                {
                    var netItems = NativeFileScanner.GetNetworkPlaces();
                    _allItems.AddRange(netItems);
                }
                else if (ArchiveService.IsArchiveOrSubPath(CurrentPath, out string archiveFile, out string internalSubPath))
                {
                    var archiveItems = ArchiveService.GetArchiveFolderItems(archiveFile, internalSubPath);
                    _allItems.AddRange(archiveItems);
                }
                else
                {
                    bool showHidden = ConfigService.Current.Ui.ShowHiddenFiles;
                    var scanned = NativeFileScanner.ScanDirectory(CurrentPath, showHidden);
                    _allItems.AddRange(scanned);

                    // WSL ルート走査時のフォールバック (レジストリからディストリビューション取得)
                    if (_allItems.Count == 0 && (CurrentPath.Equals(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals(@"\\wsl$", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals("Linux", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            using var lxssKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
                            if (lxssKey != null)
                            {
                                foreach (var subKeyName in lxssKey.GetSubKeyNames())
                                {
                                    using var distroKey = lxssKey.OpenSubKey(subKeyName);
                                    if (distroKey != null)
                                    {
                                        string? distroName = distroKey.GetValue("DistributionName") as string;
                                        if (!string.IsNullOrWhiteSpace(distroName))
                                        {
                                            _allItems.Add(new FileItem
                                            {
                                                Name = distroName,
                                                FullPath = $@"\\wsl.localhost\{distroName}",
                                                GlyphIcon = "\uE74C",
                                                FileType = "Linux ディストリビューション",
                                                IsDirectory = true
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                // エラー時は何もしない
            }

            // ユーザーによる個別保存設定がない場合、フォルダー内のコンテンツ構成（画像ファイル割合など）から最適な表示モードを自動検出
            string normPath = FastExplorer.Helpers.PathHelper.NormalizeFolderPath(CurrentPath);
            if (!ConfigService.Current.FolderViewSettings.ContainsKey(normPath) &&
                !ConfigService.Current.FolderViewSettings.ContainsKey(CurrentPath))
            {
                var detectedMode = FastExplorer.Helpers.FolderTypeHelper.DetectViewModeFromContent(_allItems);
                if (detectedMode.HasValue)
                {
                    _viewMode = detectedMode.Value;
                    if (_viewMode == FolderViewMode.LargeIcons)
                    {
                        _customSize = 80;
                    }
                }
            }

            // 非同期アイコン取得キューに投入 & 初期アイコンの事前即時適用 (青いフォントアイコンのチラつき防止)
            bool allowThumb = IconThumbnailService.IsImageOrientedMode(_viewMode);
            foreach (var item in _allItems)
            {
                item.IsCut = FileOperationService.IsPathCut(item.FullPath);
                item.AllowThumbnail = allowThumb;
                IconThumbnailService.Instance.ApplyImmediateDefaultIcon(item);
                IconThumbnailService.Instance.Enqueue(item);
            }

            ApplyFilter();
            IsLoading = false;
        }
    }
}
