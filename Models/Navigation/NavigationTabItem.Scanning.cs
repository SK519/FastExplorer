using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
        private int _refreshGeneration;

        public void Refresh()
        {
            RefreshIncremental();
        }

        public void RefreshIncremental()
        {
            // 初回読み込み中の場合は重複スキャンをスキップ
            if (IsLoading) return;

            int capturedLoadGen = System.Threading.Volatile.Read(ref _loadGeneration);
            int refreshGen = System.Threading.Interlocked.Increment(ref _refreshGeneration);
            string targetPath = CurrentPath;

            // ディスク/WSL/ネットワーク走査は必ずバックグラウンドスレッドで実行し、UIスレッドのフリーズを完全排除
            System.Threading.Tasks.Task.Run(() =>
            {
                List<FileItem> newScanned;
                try
                {
                    newScanned = ScanPath(targetPath)
                        .DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch
                {
                    return;
                }

                if (capturedLoadGen != System.Threading.Volatile.Read(ref _loadGeneration) ||
                    refreshGen != System.Threading.Volatile.Read(ref _refreshGeneration))
                {
                    return;
                }

                var dq = DispatcherQueue;
                if (dq == null) return;

                dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    if (capturedLoadGen != System.Threading.Volatile.Read(ref _loadGeneration) ||
                        refreshGen != System.Threading.Volatile.Read(ref _refreshGeneration))
                    {
                        return;
                    }

                    ApplyIncrementalDiffOnUiThread(newScanned);
                });
            });
        }

        private void ApplyIncrementalDiffOnUiThread(List<FileItem> newScanned)
        {
            try
            {
                // 既存の _allItems 内に重複があれば事前に除去
                var seenExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = _allItems.Count - 1; i >= 0; i--)
                {
                    if (!seenExisting.Add(_allItems[i].FullPath))
                    {
                        _allItems.RemoveAt(i);
                    }
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
                        if (!string.Equals(existingItem.Name, newItem.Name, StringComparison.Ordinal))
                        {
                            existingItem.Name = newItem.Name;
                        }
                        if (existingItem.DateModified != newItem.DateModified)
                        {
                            existingItem.DateModified = newItem.DateModified;
                            if (allowThumb) IconThumbnailService.Instance.Enqueue(existingItem);
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
                        existingLookup[newItem.FullPath] = newItem;
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

        private List<FileItem> ScanCurrentPath() => ScanPath(CurrentPath);

        private static List<FileItem> ScanPath(string path)
        {
            var scanned = new List<FileItem>(256);

            try
            {
                if (string.IsNullOrEmpty(path) || path.Equals("Home", StringComparison.OrdinalIgnoreCase))
                {
                    // ホーム画面は専用の HomeViewControl で描画されるため、通常リスト向けの重い COM 走査をスキップ
                    return scanned;
                }
                else if (path.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
                {
                    scanned.AddRange(NativeFileScanner.GetDrives());
                }
                else if (RecycleBinService.IsRecycleBinPath(path))
                {
                    scanned.AddRange(RecycleBinService.GetRecycleBinItems());
                }
                else if (path.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase) || path.Equals("Network", StringComparison.OrdinalIgnoreCase))
                {
                    scanned.AddRange(NativeFileScanner.GetNetworkPlaces());
                }
                else if (ArchiveService.IsArchiveOrSubPath(path, out string archiveFile, out string internalSubPath))
                {
                    scanned.AddRange(ArchiveService.GetArchiveFolderItems(archiveFile, internalSubPath));
                }
                else
                {
                    bool showHidden = ConfigService.Current.Ui.ShowHiddenFiles;

                    if (path.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
                    {
                        scanned.AddRange(WslScanner.ScanWslPath(path, showHidden));
                    }
                    else
                    {
                        scanned.AddRange(NativeFileScanner.ScanDirectory(path, showHidden));
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
            var targetList = !string.IsNullOrWhiteSpace(_filterText)
                ? _allItems.Where(item => item.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<FileItem>(_allItems);

            SortItemList(targetList);
            return targetList;
        }

        public void SortItemList(List<FileItem> targetList)
        {
            var sortCol = CurrentSortColumn;
            bool asc = IsSortAscending;

            targetList.Sort((a, b) =>
            {
                // 1. フォルダーを常にファイルより前に配置 (Filter-first)
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }

                // ドライブ同士の場合はドライブレター（C:\, D:\, G:\ ...）のアルファベット順（ABC順）でソート
                if (a.IsDrive && b.IsDrive)
                {
                    int driveCmp = string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
                    return asc ? driveCmp : -driveCmp;
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
            // Items コレクションの同期 (初回読み込み時や件数差が大きい時は一括)
            if (Items.Count == 0 || Math.Abs(Items.Count - targetList.Count) > 30 || targetList.Count > 500)
            {
                Items.ReplaceAll(targetList);
                return;
            }

            // 並び順が大きく異なっている場合（例: ソート変更）も一括置換 (O(N^2) と大量の CollectionChanged を回避)
            int mismatchCount = 0;
            int checkLen = Math.Min(Items.Count, targetList.Count);
            for (int i = 0; i < checkLen; i++)
            {
                if (!string.Equals(Items[i].FullPath, targetList[i].FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchCount++;
                    if (mismatchCount > 10) break;
                }
            }

            if (mismatchCount > 10)
            {
                Items.ReplaceAll(targetList);
                return;
            }

            // 少数件の追加・削除・軽微な位置変更のみ最小差分で更新 (スクロール位置・選択状態を維持)
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
                        currentIndices[targetItem.FullPath] = targetIndex;
                        for (int k = targetIndex + 1; k <= existingIndex; k++)
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

        private int _loadGeneration;

        private void LoadItems()
        {
            int currentGen = System.Threading.Interlocked.Increment(ref _loadGeneration);
            CancelSubfolderSearch();
            IconThumbnailService.Instance.ClearQueue();
            IconThumbnailService.Instance.TrimThumbnailCache(80);
            IsLoading = true;
            foreach (var oldItem in _allItems)
            {
                if (oldItem.AllowThumbnail && !oldItem.IsDirectory)
                {
                    oldItem.Icon = null;
                }
            }
            _allItems.Clear();
            Items.Clear();

            string targetPath = CurrentPath;
            string? selectTargetName = PendingSelectedItemName;
            PendingSelectedItemName = null;

            System.Threading.Tasks.Task.Run(() =>
            {
                var scanned = new List<FileItem>(256);
                try
                {
                    scanned = ScanPath(targetPath);
                }
                catch
                {
                    // ignored
                }

                if (currentGen != _loadGeneration) return;

                // ユーザーによる個別保存設定がない場合、フォルダー内のコンテンツ構成から最適な表示モードを自動検出 (ThisPC/PC は除外)
                string normPath = FastExplorer.Helpers.PathHelper.NormalizeFolderPath(targetPath);
                FolderViewMode? detectedMode = null;
                if (!targetPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) &&
                    !ConfigService.Current.FolderViewSettings.ContainsKey(normPath) &&
                    !ConfigService.Current.FolderViewSettings.ContainsKey(targetPath))
                {
                    detectedMode = FastExplorer.Helpers.FolderTypeHelper.DetectViewModeFromContent(scanned);
                }

                // アイコンの事前設定
                FolderViewMode activeMode = detectedMode ?? _viewMode;
                bool allowThumb = IconThumbnailService.IsImageOrientedMode(activeMode);
                foreach (var item in scanned)
                {
                    item.IsCut = FileOperationService.IsPathCut(item.FullPath);
                    item.AllowThumbnail = allowThumb;
                    IconThumbnailService.Instance.ApplyImmediateDefaultIcon(item);
                }

                DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    if (currentGen != _loadGeneration) return;

                    _allItems.Clear();
                    _allItems.AddRange(scanned);

                    if (detectedMode.HasValue)
                    {
                        ViewMode = detectedMode.Value;
                        if (_viewMode == FolderViewMode.LargeIcons)
                        {
                            _customSize = 80;
                        }
                    }

                    RecalculateTotalBytes();
                    ApplyFilter();
                    IsLoading = false;

                    // 可視範囲は ContainerContentChanging でオンデマンド読み込みするため、初期プリフェッチは先頭48件のみに制限
                    int prefetchLimit = Math.Min(_allItems.Count, 48);
                    for (int i = 0; i < prefetchLimit; i++)
                    {
                        IconThumbnailService.Instance.Enqueue(_allItems[i]);
                    }

                    if (!string.IsNullOrEmpty(selectTargetName))
                    {
                        var targetItem = Items.FirstOrDefault(i => 
                            i.Name.Equals(selectTargetName, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(i.ShortcutPath) && Path.GetFileName(i.ShortcutPath).Equals(selectTargetName, StringComparison.OrdinalIgnoreCase)))
                            ?? Items.FirstOrDefault(i =>
                                Path.GetFileNameWithoutExtension(i.Name).Equals(Path.GetFileNameWithoutExtension(selectTargetName), StringComparison.OrdinalIgnoreCase) ||
                                (i.IsDrive && i.FullPath.StartsWith(selectTargetName, StringComparison.OrdinalIgnoreCase)));
                        if (targetItem != null)
                        {
                            targetItem.IsSelected = true;
                            ItemSelectionRequested?.Invoke(this, targetItem.Name);
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
