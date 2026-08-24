using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        private class DisplayEntry
        {
            public bool IsGroup { get; set; }
            public string Key { get; set; } = string.Empty;
            public List<KeyValuePair<string, bool>>? Children { get; set; }
            public bool SingleValue { get; set; }
        }

        private string _detectedFilterStatus = "All"; // "All", "Enabled", "Disabled"

        private void RenderDetectedItemsList(string filterText = "")
        {
            if (DetectedItemsContainer == null) return;
            DetectedItemsContainer.Children.Clear();
            _renderedCards.Clear();
            _renderedKeys.Clear();
            _initialCardTops.Clear();

            var dict = ConfigService.Current.ShellMenu.ItemVisibilityState;
            if (dict.Count == 0)
            {
                var emptyBorder = new Border
                {
                    Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(24, 20, 24, 20)
                };
                var emptyStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new FontIcon { Glyph = "\uE74C", FontSize = 28, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "まだ検出された項目がありません",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "ファイルやフォルダーを右クリックすると、OSのシェル拡張項目が自動的にここに収集・分類されます。",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyBorder.Child = emptyStack;
                DetectedItemsContainer.Children.Add(emptyBorder);
                return;
            }

            EnsureMenuOrderInitialized();

            string filter = filterText.Trim().ToLowerInvariant();
            int count = 0;

            // 親グループ別に分類 (例: "PeaZip → 解凍..." -> 親: "PeaZip", 子: "解凍...")
            var groups = new Dictionary<string, List<KeyValuePair<string, bool>>>(StringComparer.OrdinalIgnoreCase);
            var singleItems = new List<KeyValuePair<string, bool>>();

            foreach (var kvp in dict)
            {
                // 状態フィルター (有効のみ / 無効のみ)
                if (_detectedFilterStatus == "Enabled" && !kvp.Value) continue;
                if (_detectedFilterStatus == "Disabled" && kvp.Value) continue;

                string itemName = kvp.Key;
                if (!string.IsNullOrEmpty(filter) && !itemName.ToLowerInvariant().Contains(filter))
                {
                    continue;
                }

                if (itemName.Contains(" → "))
                {
                    var parts = itemName.Split(new[] { " → " }, 2, StringSplitOptions.None);
                    string rawParent = parts[0];
                    string child = parts[1];

                    var vendor = ShellMenuFilter.FindMatchingVendorRule(rawParent)
                              ?? ShellMenuFilter.FindMatchingVendorRule(child)
                              ?? ShellMenuFilter.FindMatchingVendorRule(itemName);

                    string parent = (vendor != null && vendor.IsClusterable)
                        ? vendor.DisplayName
                        : rawParent;

                    if (!groups.ContainsKey(parent))
                    {
                        groups[parent] = new List<KeyValuePair<string, bool>>();
                    }
                    groups[parent].Add(kvp);
                }
                else if (ShellMenuFilter.FindMatchingVendorRule(itemName) is { } vendor && vendor.IsClusterable)
                {
                    string parent = vendor.DisplayName;
                    if (!groups.ContainsKey(parent))
                    {
                        groups[parent] = new List<KeyValuePair<string, bool>>();
                    }
                    groups[parent].Add(kvp);
                }
                else
                {
                    singleItems.Add(kvp);
                }

                count++;
            }

            // 表示用エントリーリストを構築して MenuOrder 順にソート
            var entries = new List<DisplayEntry>();

            foreach (var grp in groups)
            {
                if (grp.Value.Count == 1 && _detectedFilterStatus == "All")
                {
                    singleItems.Add(grp.Value[0]);
                    continue;
                }

                entries.Add(new DisplayEntry
                {
                    IsGroup = true,
                    Key = grp.Key,
                    Children = grp.Value
                });
            }

            foreach (var s in singleItems)
            {
                entries.Add(new DisplayEntry
                {
                    IsGroup = false,
                    Key = s.Key,
                    SingleValue = s.Value
                });
            }

            var sortedEntries = entries.OrderBy(e => GetItemOrderRank(e.Key)).ToList();

            foreach (var entry in sortedEntries)
            {
                if (entry.IsGroup && entry.Children != null)
                {
                    RenderGroupEntry(entry.Key, entry.Children);
                }
                else
                {
                    RenderSingleEntry(entry.Key, entry.SingleValue);
                }
            }

            if (count == 0)
            {
                var noMatchCard = new Border
                {
                    Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(20, 16, 20, 16)
                };
                var noMatchText = new TextBlock
                {
                    Text = "条件に一致する項目がありません。",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                noMatchCard.Child = noMatchText;
                DetectedItemsContainer.Children.Add(noMatchCard);
            }
        }

        private void RenderSingleEntry(string itemName, bool isEnabled)
        {
            var cardGrid = new Grid
            {
                Padding = new Thickness(12, 8, 12, 8),
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 4),
                Tag = itemName,
                RenderTransform = new TranslateTransform()
            };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ドラッグハンドル ≡
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 項目名
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Toggle

            var dragHandle = CreateDragHandle(cardGrid, itemName);

            var textBlock = new TextBlock
            {
                Text = itemName,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var toggle = new ToggleSwitch
            {
                IsOn = isEnabled,
                MinWidth = 0,
                Margin = new Thickness(0)
            };

            string capturedName = itemName;
            toggle.Toggled += (s, e) =>
            {
                if (_isInitializing) return;
                ConfigService.Current.ShellMenu.ItemVisibilityState[capturedName] = toggle.IsOn;
                ConfigService.Save();
            };

            Grid.SetColumn(dragHandle, 0);
            Grid.SetColumn(textBlock, 1);
            Grid.SetColumn(toggle, 2);

            cardGrid.Children.Add(dragHandle);
            cardGrid.Children.Add(textBlock);
            cardGrid.Children.Add(toggle);

            _renderedCards.Add(cardGrid);
            _renderedKeys.Add(itemName);
            DetectedItemsContainer.Children.Add(cardGrid);
        }

        private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RenderDetectedItemsList(SearchFilterBox.Text);
        }

        private void DetectedFilterStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (DetectedFilterStatusComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _detectedFilterStatus = tag;
                RenderDetectedItemsList(SearchFilterBox?.Text ?? "");
            }
        }

        private void AllOn_Click(object sender, RoutedEventArgs e)
        {
            var dict = ConfigService.Current.ShellMenu.ItemVisibilityState;
            var keys = dict.Keys.ToList();
            foreach (var k in keys)
            {
                dict[k] = true;
            }
            ConfigService.Save();
            RenderDetectedItemsList(SearchFilterBox.Text);
        }

        private void AllOff_Click(object sender, RoutedEventArgs e)
        {
            var dict = ConfigService.Current.ShellMenu.ItemVisibilityState;
            var keys = dict.Keys.ToList();
            foreach (var k in keys)
            {
                dict[k] = false;
            }
            ConfigService.Save();
            RenderDetectedItemsList(SearchFilterBox.Text);
        }

        private void RecommendedPreset_Click(object sender, RoutedEventArgs e)
        {
            var dict = ConfigService.Current.ShellMenu.ItemVisibilityState;
            var keys = dict.Keys.ToList();

            // よく使われる開発・アーカイバ・便利ツールのみ ON にし、冗長な項目を OFF にする
            string[] usefulKeywords = ["7-zip", "peazip", "winrar", "google", "code", "git", "defender", "powerrename", "share", "designer", "フォト"];

            foreach (var k in keys)
            {
                string lower = k.ToLowerInvariant();
                bool shouldEnable = usefulKeywords.Any(kw => lower.Contains(kw));
                dict[k] = shouldEnable;
            }

            ConfigService.Save();
            RenderDetectedItemsList(SearchFilterBox.Text);
        }

        private async void ResetDetectedItems_Click(object sender, RoutedEventArgs e)
        {
            var dict = ConfigService.Current.ShellMenu.ItemVisibilityState;
            if (dict.Count == 0 && ConfigService.Current.ShellMenu.MenuOrder.Count == 0) return;

            var dialog = new ContentDialog
            {
                Title = "検出項目と並び順をリセット",
                Content = "これまでに検出されたすべてのコンテキストメニュー項目、表示順序、および有効/無効設定を初期化しますか？\n\n（再度ファイルを右クリックした際に、OS メニューの項目が再検出されます）",
                PrimaryButtonText = "リセット",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                dict.Clear();
                ConfigService.Current.ShellMenu.MenuOrder.Clear();
                ConfigService.Save();
                SearchFilterBox.Text = string.Empty;
                EnsureMenuOrderInitialized();
                RenderDetectedItemsList();
            }
        }
    }
}
