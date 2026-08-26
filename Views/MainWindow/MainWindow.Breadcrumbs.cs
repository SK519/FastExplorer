using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FastExplorer.Core;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Breadcrumb & Address Bar Handlers

        private bool _addressBarEventsInitialized;

        public void InitializeAddressBarEvents()
        {
            if (_addressBarEventsInitialized || AddressBar == null) return;
            _addressBarEventsInitialized = true;

            AddressBar.BackRequested += (s, e) => BackButton_Click(s, e);
            AddressBar.ForwardRequested += (s, e) => ForwardButton_Click(s, e);
            AddressBar.UpRequested += (s, e) => UpButton_Click(s, e);
            AddressBar.RefreshRequested += (s, e) => RefreshButton_Click(s, e);
            AddressBar.SettingsRequested += (s, e) => SettingsButton_Click(s, e);
            AddressBar.UpdateRequested += (s, e) => OpenSettingsTab("About");
            AddressBar.SearchFilterChanged += text => { if (CurrentTab != null) CurrentTab.FilterText = text; };
            AddressBar.SearchFilterEscaped += () => { ActiveListControl?.Focus(FocusState.Programmatic); };
            AddressBar.AddressInputRequested += () => SwitchToAddressInput();
            AddressBar.AddressNavigateRequested += ExecuteAddressNavigation;
            AddressBar.BreadcrumbItemClicked += item => CurrentTab?.NavigateTo(item.FullPath);
            AddressBar.BreadcrumbArrowClicked += ShowBreadcrumbSubfoldersFlyout;
            AddressBar.BreadcrumbDragOver += Breadcrumb_DragOver;
            AddressBar.BreadcrumbDrop += Breadcrumb_Drop;
        }

        private void ShowBreadcrumbSubfoldersFlyout(Button btn, BreadcrumbItem item)
        {
            if (CurrentTab == null) return;
            var flyout = new MenuFlyout();

            if (item.FullPath.Equals("ThisPC", StringComparison.OrdinalIgnoreCase))
            {
                var drives = NativeFileScanner.GetDrives();
                foreach (var drive in drives)
                {
                    var menuItem = new MenuFlyoutItem
                    {
                        Text = drive.Name,
                        Icon = new FontIcon { Glyph = "\uEDA2" }
                    };
                    string targetPath = drive.FullPath;
                    menuItem.Click += (s, args) => CurrentTab.NavigateTo(targetPath);
                    flyout.Items.Add(menuItem);
                }
            }
            else
            {
                try
                {
                    var subItems = NativeFileScanner.ScanDirectory(item.FullPath, ConfigService.Current.Ui.ShowHiddenFiles);
                    var subFolders = subItems.Where(i => i.IsDirectory).OrderBy(i => i.Name, FastExplorer.Helpers.NaturalStringComparer.Instance).ToList();

                    if (subFolders.Count == 0)
                    {
                        flyout.Items.Add(new MenuFlyoutItem { Text = "(サブフォルダーなし)", IsEnabled = false });
                    }
                    else
                    {
                        foreach (var folder in subFolders)
                        {
                            var menuItem = new MenuFlyoutItem
                            {
                                Text = folder.Name,
                                Icon = new FontIcon { Glyph = "\uE8B7" }
                            };
                            string targetPath = folder.FullPath;
                            menuItem.Click += (s, args) => CurrentTab.NavigateTo(targetPath);
                            flyout.Items.Add(menuItem);
                        }
                    }
                }
                catch
                {
                    flyout.Items.Add(new MenuFlyoutItem { Text = "(読み込みエラー)", IsEnabled = false });
                }
            }

            flyout.ShowAt(btn);
        }

        public void SwitchToAddressInput()
        {
            if (MainTabView.SelectedItem is TabViewItem tabViewItem && tabViewItem.Tag as string == "SettingsTab")
            {
                AddressBar?.SwitchToAddressInput("FastExplorer://Settings");
                return;
            }
            AddressBar?.SwitchToAddressInput(CurrentTab?.CurrentPath ?? string.Empty);
        }

        private void ExecuteAddressNavigation(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return;

            string target = rawInput.Trim();

            if (target.Equals("FastExplorer://Settings", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("FastExplorer:Settings", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("設定", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("about:settings", StringComparison.OrdinalIgnoreCase) ||
                target.Equals("about:config", StringComparison.OrdinalIgnoreCase))
            {
                OpenSettingsTab();
                return;
            }

            // ユーザーが手動で打ち込んだパスのみを記憶
            TypedPathsService.AddPath(target);

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(target);

                string workingDir = (CurrentTab != null && !string.IsNullOrEmpty(CurrentTab.CurrentPath) && Directory.Exists(CurrentTab.CurrentPath))
                    ? CurrentTab.CurrentPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (RecycleBinService.IsRecycleBinPath(expanded))
                {
                    CurrentTab?.NavigateTo(RecycleBinService.RecycleBinUri);
                    return;
                }

                if (expanded.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
                    expanded.Equals("ThisPC", StringComparison.OrdinalIgnoreCase) ||
                    expanded.Equals("PC", StringComparison.OrdinalIgnoreCase) ||
                    expanded.Equals("Network", StringComparison.OrdinalIgnoreCase) ||
                    expanded.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase) ||
                    expanded.StartsWith("::{", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentTab?.NavigateTo(expanded);
                    return;
                }

                string unquoted = expanded.Trim('"');
                if (Directory.Exists(unquoted))
                {
                    CurrentTab?.NavigateTo(Path.GetFullPath(unquoted));
                    return;
                }

                try
                {
                    string combinedDir = Path.GetFullPath(Path.Combine(workingDir, unquoted));
                    if (Directory.Exists(combinedDir))
                    {
                        CurrentTab?.NavigateTo(combinedDir);
                        return;
                    }
                }
                catch { }

                string? targetFile = null;
                if (File.Exists(unquoted))
                {
                    targetFile = Path.GetFullPath(unquoted);
                }
                else
                {
                    try
                    {
                        string combinedFile = Path.GetFullPath(Path.Combine(workingDir, unquoted));
                        if (File.Exists(combinedFile))
                        {
                            targetFile = combinedFile;
                        }
                    }
                    catch { }
                }

                if (targetFile != null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = targetFile,
                            WorkingDirectory = workingDir,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AddressBar] File launch error: {ex.Message}");
                    }

                    string? parentDir = Path.GetDirectoryName(targetFile);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        CurrentTab?.NavigateTo(parentDir);
                    }
                    return;
                }

                if (expanded.Contains("://") ||
                    expanded.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                    expanded.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = expanded,
                            UseShellExecute = true
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AddressBar] Protocol launch error: {ex.Message}");
                    }
                }

                if (expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    if (expanded.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentTab?.NavigateTo(expanded);
                        return;
                    }

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = expanded,
                            UseShellExecute = true
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AddressBar] Shell special folder launch error: {ex.Message}");
                    }
                }

                var (command, arguments) = ParseCommandLine(expanded);
                if (!string.IsNullOrEmpty(command))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = command,
                            Arguments = arguments,
                            WorkingDirectory = workingDir,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AddressBar] Command execution '{command}' failed: {ex.Message}");
                    }
                }

                CurrentTab?.NavigateTo(unquoted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AddressBar] Navigation error: {ex.Message}");
                CurrentTab?.NavigateTo(target.Trim('"'));
            }
        }

        private static (string command, string arguments) ParseCommandLine(string input)
        {
            input = input.Trim();
            if (string.IsNullOrEmpty(input))
                return (string.Empty, string.Empty);

            if (input.StartsWith("\""))
            {
                int closingQuote = input.IndexOf('"', 1);
                if (closingQuote != -1)
                {
                    string command = input.Substring(1, closingQuote - 1);
                    string args = input.Substring(closingQuote + 1).Trim();
                    return (command, args);
                }
            }

            int spaceIndex = input.IndexOf(' ');
            if (spaceIndex != -1)
            {
                string command = input.Substring(0, spaceIndex);
                string args = input.Substring(spaceIndex + 1).Trim();
                return (command, args);
            }

            return (input, string.Empty);
        }

        #endregion

        #region Navigation & Toolbar Actions

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.GoBack();
            UpdateToolbarState();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.GoForward();
            UpdateToolbarState();
        }

        private void UpButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.GoUp();
            UpdateToolbarState();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentTab?.Refresh();
            UpdateToolbarState();
        }

        #endregion
    }
}
