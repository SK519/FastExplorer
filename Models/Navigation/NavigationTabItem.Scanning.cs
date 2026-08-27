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
                var newScanned = ScanCurrentPath();

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

                // 2. 合計ファイルサイズの再計算 & Items コレクションの差分同期 (スクロール位置・選択状態を完全維持)
                RecalculateTotalBytes();
                SyncFilteredItems();
            }
            catch
            {
                LoadItems();
            }
        }

        private List<FileItem> ScanCurrentPath()
        {
            var scanned = new List<FileItem>(1024);

            try
            {
                if (CurrentPath.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    scanned.AddRange(QuickAccessService.GetHomeItems());
                }
                else if (CurrentPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    scanned.AddRange(NativeFileScanner.GetDrives());
                }
                else if (RecycleBinService.IsRecycleBinPath(CurrentPath))
                {
                    scanned.AddRange(RecycleBinService.GetRecycleBinItems());
                }
                else if (CurrentPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
                {
                    scanned.AddRange(NativeFileScanner.GetNetworkPlaces());
                }
                else if (ArchiveService.IsArchiveOrSubPath(CurrentPath, out string archiveFile, out string internalSubPath))
                {
                    scanned.AddRange(ArchiveService.GetArchiveFolderItems(archiveFile, internalSubPath));
                }
                else
                {
                    bool showHidden = ConfigService.Current.Ui.ShowHiddenFiles;
                    scanned.AddRange(NativeFileScanner.ScanDirectory(CurrentPath, showHidden));

                    // WSL ルート走査時のフォールバック (レジストリからディストリビューション取得)
                    if (scanned.Count == 0 && (CurrentPath.Equals(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals(@"\\wsl$", StringComparison.OrdinalIgnoreCase) || CurrentPath.Equals("Linux", StringComparison.OrdinalIgnoreCase)))
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
                                            scanned.Add(new FileItem
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
                // ignored
            }

            return scanned;
        }

        private void SyncFilteredItems()
        {
            var targetList = GetFilteredAndSortedItems();
            ApplySizesToItems(targetList);
            SynchronizeItemsCollection(targetList);
            UpdateStatusText();
        }

        private List<FileItem> GetFilteredAndSortedItems()
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

            return targetList;
        }

        private void ApplySizesToItems(List<FileItem> targetList)
        {
            bool isGrid = ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons;
            foreach (var item in targetList)
            {
                ApplySizeToItem(item, CustomSize, isGrid, ViewMode);
                item.ApplyDetailsScale(_viewScale);
            }
        }

        private void SynchronizeItemsCollection(List<FileItem> targetList)
        {
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

                // アイテムの現在インデックスを高速検索するための辞書 (O(n) 化)
                var currentIndices = new Dictionary<string, int>(Items.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Items.Count; i++)
                {
                    currentIndices[Items[i].FullPath] = i;
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

                        if (currentIndices.TryGetValue(targetItem.FullPath, out int existingIndex) && existingIndex > targetIndex)
                        {
                            Items.Move(existingIndex, targetIndex);
                            for (int k = targetIndex; k <= existingIndex; k++)
                            {
                                currentIndices[Items[k].FullPath] = k;
                            }
                        }
                        else
                        {
                            Items.Insert(targetIndex, targetItem);
                            for (int k = targetIndex; k < Items.Count; k++)
                            {
                                currentIndices[Items[k].FullPath] = k;
                            }
                        }
                    }
                    else
                    {
                        Items.Add(targetItem);
                        currentIndices[targetItem.FullPath] = Items.Count - 1;
                    }
                }
            }
        }

        private int _loadGeneration;

        private void LoadItems()
        {
            int currentGen = System.Threading.Interlocked.Increment(ref _loadGeneration);
            IsLoading = true;
            _allItems.Clear();
            Items.Clear();

            string targetPath = CurrentPath;
            string? selectTargetName = PendingSelectedItemName;
            PendingSelectedItemName = null;

            System.Threading.Tasks.Task.Run(() =>
            {
                var scanned = new List<FileItem>(1024);
                try
                {
                    scanned = ScanCurrentPath();
                }
                catch
                {
                    // ignored
                }

                if (currentGen != _loadGeneration) return;

                // ユーザーによる個別保存設定がない場合、フォルダー内のコンテンツ構成から最適な表示モードを自動検出
                string normPath = FastExplorer.Helpers.PathHelper.NormalizeFolderPath(targetPath);
                FolderViewMode? detectedMode = null;
                if (!ConfigService.Current.FolderViewSettings.ContainsKey(normPath) &&
                    !ConfigService.Current.FolderViewSettings.ContainsKey(targetPath))
                {
                    detectedMode = FastExplorer.Helpers.FolderTypeHelper.DetectViewModeFromContent(scanned);
                }

                // アイコンの事前設定
                bool allowThumb = IconThumbnailService.IsImageOrientedMode(_viewMode);
                foreach (var item in scanned)
                {
                    item.IsCut = FileOperationService.IsPathCut(item.FullPath);
                    item.AllowThumbnail = allowThumb;
                    IconThumbnailService.Instance.ApplyImmediateDefaultIcon(item);
                }

                DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    if (currentGen != _loadGeneration) return;

                    _allItems.AddRange(scanned);

                    if (detectedMode.HasValue)
                    {
                        _viewMode = detectedMode.Value;
                        if (_viewMode == FolderViewMode.LargeIcons)
                        {
                            _customSize = 80;
                        }
                    }

                    RecalculateTotalBytes();
                    ApplyFilter();
                    IsLoading = false;

                    // キューへの投入はUI描画後に非同期実行
                    foreach (var item in _allItems)
                    {
                        IconThumbnailService.Instance.Enqueue(item);
                    }

                    if (!string.IsNullOrEmpty(selectTargetName))
                    {
                        var targetItem = Items.FirstOrDefault(i => i.Name.Equals(selectTargetName, StringComparison.OrdinalIgnoreCase));
                        if (targetItem != null)
                        {
                            targetItem.IsSelected = true;
                            ItemSelectionRequested?.Invoke(this, selectTargetName);
                        }
                    }

                    // ネットワーク項目の非同期リアルタイム受信 (タイムアウト待ちを回避して機器検知と同時に即座に画面へ反映)
                    if (targetPath.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || targetPath.Equals("Network", StringComparison.OrdinalIgnoreCase))
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            NativeFileScanner.ScanNetworkPlacesLive(newItem =>
                            {
                                if (currentGen != _loadGeneration) return;
                                DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                                {
                                    if (currentGen != _loadGeneration) return;
                                    newItem.IsCut = FileOperationService.IsPathCut(newItem.FullPath);
                                    newItem.AllowThumbnail = allowThumb;
                                    IconThumbnailService.Instance.ApplyImmediateDefaultIcon(newItem);

                                    var existing = _allItems.FirstOrDefault(x => x.Name.Equals(newItem.Name, StringComparison.OrdinalIgnoreCase));
                                    if (existing != null)
                                    {
                                        int idx = _allItems.IndexOf(existing);
                                        _allItems[idx] = newItem;
                                    }
                                    else
                                    {
                                        _allItems.Add(newItem);
                                    }
                                    SyncFilteredItems();
                                    IconThumbnailService.Instance.Enqueue(newItem);
                                });
                            });
                        });
                    }
                });
            });
        }
    }
}
