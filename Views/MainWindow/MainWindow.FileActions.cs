using System;
using System.Diagnostics;
using System.IO;
using FastExplorer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Sorting Handlers

        private void SortByName_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.SortBy(SortColumn.Name);
        }

        private void SortByDate_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.SortBy(SortColumn.DateModified);
        }

        private void SortByType_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.SortBy(SortColumn.FileType);
        }

        private void SortBySize_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.SortBy(SortColumn.Size);
        }

        #endregion

        #region Inline Renaming

        private long _lastRenameCommittedTimestamp;

        private void RenameBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                tb.PreviewKeyDown -= RenameBox_PreviewKeyDown;
                tb.PreviewKeyDown += RenameBox_PreviewKeyDown;

                if (tb.DataContext is FileItem item && item.IsRenaming)
                {
                    FocusAndSelectRenameBox(tb, item);
                }
            }
        }

        private static void FocusAndSelectRenameBox(TextBox tb, FileItem item)
        {
            tb.Focus(FocusState.Programmatic);
            string text = tb.Text;
            int dot = text.LastIndexOf('.');
            if (dot > 0 && !item.IsDirectory)
            {
                tb.Select(0, dot);
            }
            else
            {
                tb.SelectAll();
            }
        }

        private void RenameBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is FileItem item && item.IsRenaming)
            {
                if (e.Key == VirtualKey.Enter && (int)e.Key != 229 && (int)e.OriginalKey != 229)
                {
                    _lastRenameCommittedTimestamp = Stopwatch.GetTimestamp();
                    CommitRename(item, tb.Text.Trim());
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.Escape)
                {
                    _lastRenameCommittedTimestamp = Stopwatch.GetTimestamp();
                    CancelRename(item);
                    e.Handled = true;
                }
            }
        }

        private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is FileItem item && item.IsRenaming)
            {
                if (e.Key == VirtualKey.Enter && (int)e.Key != 229 && (int)e.OriginalKey != 229)
                {
                    _lastRenameCommittedTimestamp = Stopwatch.GetTimestamp();
                    CommitRename(item, tb.Text.Trim());
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.Escape)
                {
                    _lastRenameCommittedTimestamp = Stopwatch.GetTimestamp();
                    CancelRename(item);
                    e.Handled = true;
                }
            }
        }

        private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is FileItem item && item.IsRenaming)
            {
                // フォーカスが外れたときは入力内容で確定（Windows Explorer 準拠）
                CommitRename(item, tb.Text.Trim());
            }
        }

        private void CancelRename(FileItem item)
        {
            _lastRenameCommittedTimestamp = Stopwatch.GetTimestamp();
            item.RenameText = item.Name;
            item.IsRenaming = false;
            ActiveListControl?.Focus(FocusState.Programmatic);
        }

        private void CommitRename(FileItem item, string newName)
        {
            _lastRenameCommittedTimestamp = Stopwatch.GetTimestamp();
            if (string.IsNullOrWhiteSpace(newName) || newName == item.Name)
            {
                item.RenameText = item.Name;
                item.IsRenaming = false;
                ActiveListControl?.Focus(FocusState.Programmatic);
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(item.FullPath) ?? "";
                string newPath = Path.Combine(dir, newName);

                if (item.IsDirectory)
                {
                    Directory.Move(item.FullPath, newPath);
                }
                else
                {
                    File.Move(item.FullPath, newPath);
                }

                item.Name = newName;
                item.FullPath = newPath;
                item.IsRenaming = false;
                ActiveListControl?.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Rename error: {ex.Message}");
                item.RenameText = item.Name;
                item.IsRenaming = false;
                ActiveListControl?.Focus(FocusState.Programmatic);
            }
        }

        #endregion
    }
}
