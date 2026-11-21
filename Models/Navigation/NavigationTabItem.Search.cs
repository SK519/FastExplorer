using System;
using System.Collections.Generic;
using System.IO;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;

namespace FastExplorer
{
    public partial class NavigationTabItem
    {
        private void StartSubfolderSearch(string searchKeyword, string rootPath)
        {
            CancelSubfolderSearch();
            var cts = new System.Threading.CancellationTokenSource();
            _searchCts = cts;
            var token = cts.Token;
            _isSearchingSubfolders = true;

            // 初期探索対象のサブフォルダー一覧 (直下の通常フォルダー)
            var initialDirs = new List<string>();
            var existingUiPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_allItems)
            {
                foreach (var item in _allItems)
                {
                    existingUiPaths.Add(item.FullPath);
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
                    bool showHidden = ConfigService.Current.Ui.ShowHiddenFiles;

                    // Rust ネイティブ並列サブフォルダー検索 (非ヒットファイルのマネージドアロケーションゼロ & マルチコア並列)
                    if (NativeArchiveInterop.SearchSubfolders(
                            rootPath,
                            searchKeyword,
                            showHidden,
                            skipRootDirect: true,
                            hits =>
                            {
                                if (token.IsCancellationRequested) return;
                                var converted = new List<FileItem>(hits.Count);
                                foreach (var h in hits)
                                {
                                    bool isHidden = (h.Attributes & Win32Interop.FILE_ATTRIBUTE_HIDDEN) != 0;
                                    bool isSystem = (h.Attributes & Win32Interop.FILE_ATTRIBUTE_SYSTEM) != 0;
                                    bool isReadOnly = (h.Attributes & Win32Interop.FILE_ATTRIBUTE_READONLY) != 0;
                                    bool isReparse = (h.Attributes & Win32Interop.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                                    string fileType;
                                    string glyphIcon;
                                    if (h.IsDirectory)
                                    {
                                        fileType = "フォルダ";
                                        glyphIcon = "\uE8B7";
                                    }
                                    else
                                    {
                                        NativeFileScanner.GetFileInfoForExtension(Path.GetExtension(h.Name.AsSpan()), out fileType, out glyphIcon);
                                    }

                                    converted.Add(new FileItem
                                    {
                                        Name = h.Name,
                                        FullPath = h.FullPath,
                                        IsDirectory = h.IsDirectory,
                                        IsReparsePoint = isReparse,
                                        HasCustomFolderAttribute = h.IsDirectory && (isReadOnly || isSystem),
                                        SizeInBytes = h.IsDirectory ? 0 : h.Size,
                                        DateModified = h.LastModifiedTime,
                                        FileType = fileType,
                                        IsHidden = isHidden,
                                        IsReadOnly = isReadOnly,
                                        GlyphIcon = glyphIcon
                                    });
                                }

                                if (converted.Count > 0)
                                {
                                    EnqueueBatchToUI(converted, searchKeyword, existingUiPaths, token);
                                }
                            },
                            token))
                    {
                        return;
                    }

                    var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootPath };
                    var dirQueue = new Queue<string>(initialDirs);
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

                                    EnqueueBatchToUI(currentBatch, searchKeyword, existingUiPaths, token);
                                }
                            }
                        }
                    }

                    // 残りのバッチを反映
                    if (batch.Count > 0 && !token.IsCancellationRequested)
                    {
                        EnqueueBatchToUI(batch, searchKeyword, existingUiPaths, token);
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

        private void EnqueueBatchToUI(List<FileItem> batch, string searchKeyword, HashSet<string> existingSet, System.Threading.CancellationToken token)
        {
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (token.IsCancellationRequested || !string.Equals(_filterText, searchKeyword, StringComparison.Ordinal))
                {
                    return;
                }

                bool isGrid = ViewMode is FolderViewMode.SmallIcons or FolderViewMode.MediumIcons or FolderViewMode.LargeIcons or FolderViewMode.ExtraLargeIcons;
                bool allowThumb = IconThumbnailService.IsImageOrientedMode(_viewMode);

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
