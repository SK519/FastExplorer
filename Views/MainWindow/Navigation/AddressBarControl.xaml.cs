using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FastExplorer.Models;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FastExplorer.Views.MainWindow.Navigation
{
    public sealed partial class AddressBarControl : UserControl
    {
        private readonly ObservableCollection<TypedPathSuggestionItem> _suggestions = [];

        public event RoutedEventHandler? BackRequested;
        public event RoutedEventHandler? ForwardRequested;
        public event RoutedEventHandler? UpRequested;
        public event RoutedEventHandler? RefreshRequested;
        public event RoutedEventHandler? SettingsRequested;
        public event RoutedEventHandler? UpdateRequested;
        public event Action<string>? SearchFilterChanged;
        public event Action? SearchFilterEscaped;
        public event Action? AddressInputRequested;
        public event Action<string>? AddressNavigateRequested;
        public event Action<BreadcrumbItem>? BreadcrumbItemClicked;
        public event Action<Button, BreadcrumbItem>? BreadcrumbArrowClicked;
        public event DragEventHandler? BreadcrumbDragOver;
        public event DragEventHandler? BreadcrumbDrop;

        public AddressBarControl()
        {
            this.InitializeComponent();
            AddressSuggestBox.ItemsSource = _suggestions;

            this.Loaded += (s, e) =>
            {
                ApplyUpdateInfo(FastExplorer.Services.Update.UpdateService.LastUpdateInfo);
                FastExplorer.Services.Update.UpdateService.UpdateStatusChanged += OnUpdateStatusChanged;
            };

            this.Unloaded += (s, e) =>
            {
                FastExplorer.Services.Update.UpdateService.UpdateStatusChanged -= OnUpdateStatusChanged;
            };
        }

        private void OnUpdateStatusChanged(FastExplorer.Services.Update.UpdateInfo info)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyUpdateInfo(info);
            });
        }

        public void ApplyUpdateInfo(FastExplorer.Services.Update.UpdateInfo? info)
        {
            if (UpdateAvailableButton == null) return;

            if (info != null && info.IsUpdateAvailable)
            {
                UpdateAvailableButton.Visibility = Visibility.Visible;
                string label = string.IsNullOrWhiteSpace(info.LatestVersion) ? "更新" : $"更新 (v{info.LatestVersion})";
                if (UpdateAvailableText != null)
                {
                    UpdateAvailableText.Text = label;
                }
                ToolTipService.SetToolTip(UpdateAvailableButton, $"FastExplorer v{info.LatestVersion} にアップデート可能です (クリックして設定を開く)");
            }
            else
            {
                UpdateAvailableButton.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateNavigationButtons(bool canGoBack, bool canGoForward, bool canGoUp)
        {
            BackButton.IsEnabled = canGoBack;
            ForwardButton.IsEnabled = canGoForward;
            UpButton.IsEnabled = canGoUp;
        }

        public void SetBreadcrumbs(IEnumerable<BreadcrumbItem> items)
        {
            BreadcrumbItemsControl.ItemsSource = items;
        }

        public void SetSearchFilterText(string text)
        {
            SearchFilterBox.Text = text;
        }

        public string GetSearchFilterText() => SearchFilterBox.Text;

        public void FocusSearchBox()
        {
            SearchFilterBox.Focus(FocusState.Programmatic);
            SearchFilterBox.SelectAll();
        }

        public void SwitchToAddressInput(string currentPath)
        {
            BreadcrumbContainer.Visibility = Visibility.Collapsed;
            HistoryDropDownButton.Visibility = Visibility.Collapsed;
            AddressSuggestBox.Visibility = Visibility.Visible;
            AddressSuggestBox.Text = currentPath;
            UpdateSuggestions(string.Empty);
            AddressSuggestBox.Focus(FocusState.Programmatic);

            DispatcherQueue.TryEnqueue(() =>
            {
                var textBox = Helpers.VisualTreeExtensions.FindDescendant<TextBox>(AddressSuggestBox);
                textBox?.SelectAll();
                AddressSuggestBox.IsSuggestionListOpen = true;
            });
        }

        public void SwitchToBreadcrumbs()
        {
            AddressSuggestBox.Visibility = Visibility.Collapsed;
            BreadcrumbContainer.Visibility = Visibility.Visible;
            HistoryDropDownButton.Visibility = Visibility.Visible;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(sender, e);
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => ForwardRequested?.Invoke(sender, e);
        private void UpButton_Click(object sender, RoutedEventArgs e) => UpRequested?.Invoke(sender, e);
        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(sender, e);
        private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(sender, e);
        private void UpdateAvailableButton_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke(sender, e);

        private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchFilterChanged?.Invoke(SearchFilterBox.Text);
        }

        private void SearchFilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                SearchFilterBox.Text = string.Empty;
                SearchFilterEscaped?.Invoke();
                e.Handled = true;
            }
        }

        private void BreadcrumbContainer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                if (Helpers.VisualTreeExtensions.FindParent<HyperlinkButton>(dep) != null ||
                    Helpers.VisualTreeExtensions.FindParent<Button>(dep) != null)
                {
                    return;
                }
            }
            if (AddressInputRequested != null)
            {
                AddressInputRequested.Invoke();
            }
            else
            {
                SwitchToAddressInput(AddressSuggestBox.Text);
            }
        }

        private void UpdateSuggestions(string? query)
        {
            var history = TypedPathsService.GetHistory();
            _suggestions.Clear();

            if (history != null && history.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    foreach (var path in history)
                    {
                        _suggestions.Add(new TypedPathSuggestionItem(path));
                    }
                }
                else
                {
                    string q = query.Trim();
                    foreach (var path in history)
                    {
                        if (path.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            _suggestions.Add(new TypedPathSuggestionItem(path));
                        }
                    }
                }
            }

            if (_suggestions.Count > 0)
            {
                AddressSuggestBox.IsSuggestionListOpen = true;
            }
        }

        private void AddressSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                UpdateSuggestions(sender.Text);
                sender.IsSuggestionListOpen = true;
            }
        }

        private void AddressSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (_isDeletingSuggestion) return;
            if (args.SelectedItem is TypedPathSuggestionItem item)
            {
                sender.Text = item.Path;
            }
        }

        private void AddressSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (_isDeletingSuggestion) return;
            string path = (args.ChosenSuggestion as TypedPathSuggestionItem)?.Path ?? args.QueryText?.Trim() ?? string.Empty;
            SwitchToBreadcrumbs();
            if (!string.IsNullOrEmpty(path))
            {
                if (RecycleBinService.IsRecycleBinPath(path))
                {
                    path = RecycleBinService.RecycleBinUri;
                }
                AddressNavigateRequested?.Invoke(path);
            }
        }

        private void AddressSuggestBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                SwitchToBreadcrumbs();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Down)
            {
                AddressSuggestBox.IsSuggestionListOpen = true;
            }
        }

        private void AddressSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateSuggestions(string.Empty);
            DispatcherQueue.TryEnqueue(() =>
            {
                AddressSuggestBox.IsSuggestionListOpen = true;
            });
        }

        private bool _isDeletingSuggestion;

        private void AddressSuggestBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isDeletingSuggestion) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isDeletingSuggestion) return;

                var focused = FocusManager.GetFocusedElement(this.XamlRoot);
                if (focused is DependencyObject dep)
                {
                    if (Helpers.VisualTreeExtensions.FindParent<AutoSuggestBox>(dep) != null ||
                        Helpers.VisualTreeExtensions.FindParent<FlyoutPresenter>(dep) != null ||
                        Helpers.VisualTreeExtensions.FindParent<MenuFlyoutPresenter>(dep) != null ||
                        Helpers.VisualTreeExtensions.FindParent<ListViewItem>(dep) != null)
                    {
                        return;
                    }
                }
                SwitchToBreadcrumbs();
            });
        }

        private void DeleteSuggestion_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
            ExecuteDeleteSuggestion(sender);
        }

        private void DeleteSuggestion_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeleteSuggestion(sender);
        }

        private void ExecuteDeleteSuggestion(object sender)
        {
            if (sender is Button btn && btn.DataContext is TypedPathSuggestionItem item)
            {
                _isDeletingSuggestion = true;
                try
                {
                    TypedPathsService.RemovePath(item.Path);

                    var target = _suggestions.FirstOrDefault(s => s.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                    {
                        _suggestions.Remove(target);
                    }

                    if (_suggestions.Count > 0)
                    {
                        AddressSuggestBox.IsSuggestionListOpen = true;
                    }
                    else
                    {
                        AddressSuggestBox.IsSuggestionListOpen = false;
                    }
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (_suggestions.Count > 0)
                        {
                            AddressSuggestBox.IsSuggestionListOpen = true;
                        }
                        _isDeletingSuggestion = false;
                    });
                }
            }
        }

        private void HistoryDropDownButton_Click(object sender, RoutedEventArgs e)
        {
            ShowHistoryFlyout();
        }

        public void ShowHistoryFlyout()
        {
            var history = TypedPathsService.GetHistory();
            var flyout = new MenuFlyout();

            if (history == null || history.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = "(入力履歴はありません)",
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var path in history)
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = path,
                        Icon = new FontIcon { Glyph = "\uE8B7" }
                    };
                    string targetPath = path;
                    item.Click += (s, args) =>
                    {
                        SwitchToBreadcrumbs();
                        AddressNavigateRequested?.Invoke(targetPath);
                    };
                    flyout.Items.Add(item);
                }

                flyout.Items.Add(new MenuFlyoutSeparator());

                var clearItem = new MenuFlyoutItem
                {
                    Text = "履歴をクリア",
                    Icon = new FontIcon { Glyph = "\uE74D" }
                };
                clearItem.Click += (s, args) =>
                {
                    TypedPathsService.Clear();
                };
                flyout.Items.Add(clearItem);
            }

            flyout.ShowAt(HistoryDropDownButton);
        }

        private void BreadcrumbFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is HyperlinkButton btn && btn.DataContext is BreadcrumbItem item)
            {
                BreadcrumbItemClicked?.Invoke(item);
            }
        }

        private void BreadcrumbArrow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is BreadcrumbItem item)
            {
                BreadcrumbArrowClicked?.Invoke(btn, item);
            }
        }

        private void Breadcrumb_DragOver(object sender, DragEventArgs e) => BreadcrumbDragOver?.Invoke(sender, e);
        private void Breadcrumb_Drop(object sender, DragEventArgs e) => BreadcrumbDrop?.Invoke(sender, e);
    }
}
