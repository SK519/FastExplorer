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
        public void Refresh()
        {
            RefreshIncremental();
        }

        public void RefreshIncremental()
        {
            try
            {
                var newScanned = ScanCurrentPath()
                    .DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();

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

                    // WSL パスは wsl.exe 経由でスキャン (UNC パスの直接スキャンは数分かかる場合があるため)
                    if (CurrentPath.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
                    {
                        scanned.AddRange(WslScanner.ScanWslPath(CurrentPath, showHidden));
                    }
                    else
                    {
                        scanned.AddRange(NativeFileScanner.ScanDirectory(CurrentPath, showHidden));
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
            var uniqueTargetList = targetList.DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToList();

            // Items コレクションの同期 (初回読み込み時や差分が大きい時は一括、小差分時は最小の変更でスクロール位置・選択状態を維持)
            if (Items.Count == 0 || Math.Abs(Items.Count - uniqueTargetList.Count) > 50 || uniqueTargetList.Count > 1000)
            {
                Items.ReplaceAll(uniqueTargetList);
            }
            else
            {
                var targetPathSet = new HashSet<string>(uniqueTargetList.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var item in uniqueTargetList)
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

                for (int targetIndex = 0; targetIndex < uniqueTargetList.Count; targetIndex++)
                {
                    var targetItem = uniqueTargetList[targetIndex];
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
            CancelSubfolderSearch();
            IconThumbnailService.Instance.ClearQueue();
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
                    var distinctScanned = scanned.DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                    _allItems.AddRange(distinctScanned);

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

                    // キューへの投入はUI描画後に非同期実行
                    foreach (var item in _allItems)
                    {
                        IconThumbnailService.Instance.Enqueue(item);
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

        private void StartSubfolderSearch(string searchKeyword, string rootPath)
        {
            var cts = new System.Threading.CancellationTokenSource();
            _searchCts = cts;
            var token = cts.Token;
            _isSearchingSubfolders = true;

            // 初期探索対象のサブフォルダー一覧 (直下の通常フォルダー)
            var initialDirs = new List<string>();
            lock (_allItems)
            {
                foreach (var item in _allItems)
                {
                    if (item.IsDirectory && !item.IsReparsePoint && !string.IsNullOrEmpty(item.FullPath))
                    {
                        initialDirs.Add(item.FullPath);
                    }
                }
            }

            UpdateStatusText();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootPath };
                    var dirQueue = new Queue<string>(initialDirs);
                    bool showHidden = ConfigService.Current.Ui.ShowHiddenFiles;
                    var batch = new List<FileItem>(64);
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    while (dirQueue.Count > 0)
                    {
                        if (token.IsCancellationRequested) return;

                        string currentDir = dirQueue.Dequeue();
                        if (!visitedDirs.Add(currentDir)) continue;

                        List<FileItem> scanned;
                        try
                        {
                            scanned = NativeFileScanner.ScanDirectory(currentDir, showHidden);
                        }
                        catch
                        {
                            continue;
                        }

                        foreach (var item in scanned)
                        {
                            if (token.IsCancellationRequested) return;

                            // サブフォルダーの再帰キュー追加 (ジャンクション・リパースポイントは無限ループ防止のため除外)
                            if (item.IsDirectory && !item.IsReparsePoint && !string.IsNullOrEmpty(item.FullPath))
                            {
                                string dirName = item.Name;
                                if (!dirName.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) &&
                                    !dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                                {
                                    dirQueue.Enqueue(item.FullPath);
                                }
                            }

                            // 検索キーワードとのマッチング判定
                            if (item.Name.Contains(searchKeyword, StringComparison.OrdinalIgnoreCase))
                            {
                                batch.Add(item);

                                // 30件蓄積、または150ms経過でUIへ逐次反映
                                if (batch.Count >= 30 || (batch.Count > 0 && stopwatch.ElapsedMilliseconds >= 150))
                                {
                                    var currentBatch = batch;
                                    batch = new List<FileItem>(64);
                                    stopwatch.Restart();

                                    EnqueueBatchToUI(currentBatch, searchKeyword, token);
                                }
                            }
                        }
                    }

                    // 残りのバッチを反映
                    if (batch.Count > 0 && !token.IsCancellationRequested)
                    {
                        EnqueueBatchToUI(batch, searchKeyword, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 検索キャンセル時は静かに終了
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SubfolderSearch] Exception: {ex.Message}");
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                    {
                        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            if (!token.IsCancellationRequested && _searchCts == cts)
                            {
                                _isSearchingSubfolders = false;
                                UpdateStatusText();
                            }
                        });
                    }
                }
            }, token);
        }

        private void EnqueueBatchToUI(List<FileItem> batch, string searchKeyword, System.Threading.CancellationToken token)
        {
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (token.IsCancellationRequested || !string.Equals(_filterText, searchKeyword, StringComparison.Ordinal))
                {
                    return;
                }

                bool isGrid = ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons;
                bool allowThumb = IconThumbnailService.IsImageOrientedMode(_viewMode);

                var existingSet = new HashSet<string>(Items.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
                var toAdd = new List<FileItem>(batch.Count);

                foreach (var item in batch)
                {
                    if (existingSet.Add(item.FullPath))
                    {
                        item.IsCut = FileOperationService.IsPathCut(item.FullPath);
                        item.AllowThumbnail = allowThumb;
                        IconThumbnailService.Instance.ApplyImmediateDefaultIcon(item);
                        ApplySizeToItem(item, CustomSize, isGrid, ViewMode);
                        item.ApplyDetailsScale(_viewScale);
                        toAdd.Add(item);
                    }
                }

                if (toAdd.Count > 0)
                {
                    Items.AddRange(toAdd);
                    foreach (var item in toAdd)
                    {
                        IconThumbnailService.Instance.Enqueue(item);
                    }
                }

                UpdateStatusText();
            });
        }
    }
}
