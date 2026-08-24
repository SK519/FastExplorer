using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region File List Key Handlers & Navigation

        private void FileListView_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            HandleFileListKeyDown(e);
        }

        private void FileGridView_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            HandleFileListKeyDown(e);
        }

        private void HandleFileListKeyDown(KeyRoutedEventArgs e)
        {
            if (CurrentTab == null) return;

            bool isCtrl = IsCtrlPressed();
            bool isShift = IsShiftPressed();
            bool isAlt = IsAltPressed();

            // 特殊操作: Enter
            if (e.Key == VirtualKey.Enter)
            {
                if (CurrentTab?.Items?.Any(x => x.IsRenaming) == true ||
                    Stopwatch.GetElapsedTime(_lastRenameCommittedTimestamp).TotalMilliseconds < 500)
                {
                    // 名前変更入力中、および名前変更確定直後はEnterによるファイル起動を抑止
                    e.Handled = true;
                    return;
                }

                if (ShortcutService.Matches("Properties", e.Key, isCtrl, isShift, isAlt) || isAlt)
                {
                    ContextMenuProperties_Click(this, e);
                }
                else
                {
                    OpenSelectedItems();
                }
                e.Handled = true;
                return;
            }

            // 特殊操作: Escape (切り取り解除)
            if (e.Key == VirtualKey.Escape)
            {
                if (FileOperationService.IsCutOperation)
                {
                    FileOperationService.CancelCut();
                    e.Handled = true;
                    return;
                }
            }

            // ショートカット判定 (ShortcutService 連携)
            if (ShortcutService.Matches("Properties", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuProperties_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("NewFolder", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuNewFolder_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Rename", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuRename_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("DeletePermanently", e.Key, isCtrl, isShift, isAlt))
            {
                DeletePermanentlyAction();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Delete", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuDelete_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Refresh", e.Key, isCtrl, isShift, isAlt))
            {
                CurrentTab.Refresh();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Copy", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuCopy_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Cut", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuCut_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Paste", e.Key, isCtrl, isShift, isAlt))
            {
                ContextMenuPaste_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("NewTab", e.Key, isCtrl, isShift, isAlt))
            {
                CreateNewTab();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("CloseTab", e.Key, isCtrl, isShift, isAlt))
            {
                if (MainTabView.SelectedItem is TabViewItem current)
                {
                    CloseTab(current);
                }
                e.Handled = true;
            }
            else if (ShortcutService.Matches("NextTab", e.Key, isCtrl, isShift, isAlt))
            {
                SelectNextTab();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("PrevTab", e.Key, isCtrl, isShift, isAlt))
            {
                SelectPrevTab();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("ToggleHiddenFiles", e.Key, isCtrl, isShift, isAlt))
            {
                ToggleShowHiddenFiles();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Search", e.Key, isCtrl, isShift, isAlt))
            {
                AddressBar?.FocusSearchBox();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("AddressBar", e.Key, isCtrl, isShift, isAlt))
            {
                SwitchToAddressInput();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("SelectAll", e.Key, isCtrl, isShift, isAlt))
            {
                ActiveListControl.SelectAll();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("Settings", e.Key, isCtrl, isShift, isAlt))
            {
                OpenSettingsTab();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("GoUp", e.Key, isCtrl, isShift, isAlt))
            {
                CurrentTab.GoUp();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("GoBack", e.Key, isCtrl, isShift, isAlt))
            {
                CurrentTab.GoBack();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("GoForward", e.Key, isCtrl, isShift, isAlt))
            {
                CurrentTab.GoForward();
                e.Handled = true;
            }
            else if (ShortcutService.Matches("ZoomIn", e.Key, isCtrl, isShift, isAlt))
            {
                ViewZoomIn_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("ZoomOut", e.Key, isCtrl, isShift, isAlt))
            {
                ViewZoomOut_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("ZoomReset", e.Key, isCtrl, isShift, isAlt))
            {
                ViewZoomReset_Click(this, e);
                e.Handled = true;
            }
            else if (ShortcutService.Matches("TogglePreview", e.Key, isCtrl, isShift, isAlt))
            {
                IsPreviewPaneVisible = !IsPreviewPaneVisible;
                e.Handled = true;
            }
        }

        private void SelectNextTab()
        {
            if (MainTabView.TabItems.Count <= 1) return;
            int idx = MainTabView.SelectedIndex;
            int nextIdx = (idx + 1) % MainTabView.TabItems.Count;
            MainTabView.SelectedIndex = nextIdx;
        }

        private void SelectPrevTab()
        {
            if (MainTabView.TabItems.Count <= 1) return;
            int idx = MainTabView.SelectedIndex;
            int prevIdx = (idx - 1 + MainTabView.TabItems.Count) % MainTabView.TabItems.Count;
            MainTabView.SelectedIndex = prevIdx;
        }

        public void OpenSelectedItems()
        {
            var selectedList = ActiveListControl?.SelectedItems?.OfType<FileItem>().ToList();
            if (selectedList != null && selectedList.Count > 0)
            {
                foreach (var item in selectedList)
                {
                    OpenFileItem(item);
                }
            }
            else
            {
                var target = GetContextTargetItem();
                if (target != null)
                {
                    OpenFileItem(target);
                }
            }
        }

        private void OpenSelectedItem()
        {
            var selected = GetContextTargetItem();
            if (selected != null)
            {
                OpenFileItem(selected);
            }
        }

        private void OpenFileItem(FileItem item)
        {
            if (item == null || CurrentTab == null) return;

            if (item.IsDirectory)
            {
                CurrentTab.NavigateTo(item.FullPath);
            }
            else if (ArchiveService.IsSupportedArchive(item.FullPath))
            {
                // 圧縮ファイルの場合は外部エクスプローラーを開かず、フォルダーのように FastExplorer 内でプレビュー閲覧
                CurrentTab.NavigateTo(item.FullPath);
            }
            else if (ArchiveService.IsArchiveOrSubPath(item.FullPath, out _, out _))
            {
                // プレビュー時 (アーカイブ内部) のファイルは開かない
                return;
            }
            else
            {
                LaunchFile(item.FullPath);
            }
        }

        private static void LaunchFile(string filePath)
        {
            Core.Win32Interop.RecordRecentDocument(filePath);

            string? workingDir = null;
            try
            {
                workingDir = Path.GetDirectoryName(filePath);
            }
            catch { }

            try
            {
                var pExecInfo = new Win32Interop.SHELLEXECUTEINFOW
                {
                    cbSize = Marshal.SizeOf<Win32Interop.SHELLEXECUTEINFOW>(),
                    lpVerb = "open",
                    lpFile = filePath,
                    lpDirectory = workingDir,
                    nShow = Win32Interop.SW_SHOWNORMAL
                };
                Win32Interop.ShellExecuteExW(ref pExecInfo);
            }
            catch
            {
                try
                {
                    var psi = new ProcessStartInfo(filePath)
                    {
                        UseShellExecute = true
                    };
                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        psi.WorkingDirectory = workingDir;
                    }
                    Process.Start(psi);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static bool IsCtrlPressed()
        {
            if ((Win32Interop.GetAsyncKeyState(0x11) & 0x8000) != 0) return true;
            if ((Win32Interop.GetKeyState(0x11) & 0x8000) != 0) return true;
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        }

        private static bool IsShiftPressed()
        {
            if ((Win32Interop.GetAsyncKeyState(0x10) & 0x8000) != 0) return true;
            if ((Win32Interop.GetKeyState(0x10) & 0x8000) != 0) return true;
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        }

        private static bool IsAltPressed()
        {
            if ((Win32Interop.GetAsyncKeyState(0x12) & 0x8000) != 0) return true;
            if ((Win32Interop.GetKeyState(0x12) & 0x8000) != 0) return true;
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        }

        #endregion
    }
}
