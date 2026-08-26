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
        private void RenderGroupEntry(string parentName, List<KeyValuePair<string, bool>> children)
        {
            bool isAllChildrenOn = children.All(c => c.Value);

            var groupBorder = new Border
            {
                Background = GetThemeBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Tag = parentName,
                RenderTransform = new TranslateTransform()
            };

            var mainStack = new StackPanel { Spacing = 8 };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ドラッグハンドル ≡
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 展開ボタン / グループ名
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 一括 Toggle

            var dragHandle = CreateDragHandle(groupBorder, parentName);

            var headerBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };

            int enabledCount = children.Count(c => c.Value);

            var headerTitleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var headerText = new TextBlock
            {
                Text = parentName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var badgeBorder = new Border
            {
                Background = GetThemeBrush("SubtleFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeText = new TextBlock
            {
                Text = $"{enabledCount}/{children.Count} 有効",
                FontSize = 11,
                Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray))
            };
            badgeBorder.Child = badgeText;

            var expandIcon = new FontIcon
            {
                Glyph = "\uE70D", // ChevronDown
                FontSize = 12,
                Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray)),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerTitleStack.Children.Add(headerText);
            headerTitleStack.Children.Add(badgeBorder);
            headerTitleStack.Children.Add(expandIcon);
            headerBtn.Content = headerTitleStack;

            var masterToggle = new ToggleSwitch
            {
                IsOn = isAllChildrenOn,
                MinWidth = 0,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(dragHandle, 0);
            Grid.SetColumn(headerBtn, 1);
            Grid.SetColumn(masterToggle, 2);

            headerGrid.Children.Add(dragHandle);
            headerGrid.Children.Add(headerBtn);
            headerGrid.Children.Add(masterToggle);
            mainStack.Children.Add(headerGrid);

            // 子要素のコンテナ (折りたたみ可能)
            var childContainer = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(24, 4, 0, 4),
                Visibility = Visibility.Collapsed
            };

            headerBtn.Click += (s, e) =>
            {
                childContainer.Visibility = childContainer.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                expandIcon.Glyph = childContainer.Visibility == Visibility.Visible ? "\uE70E" : "\uE70D";
            };

            var allChildToggles = new List<(string Key, ToggleSwitch Toggle)>();

            // 子要素を直接の単体項目と入れ子サブグループに分類
            var subGroups = new Dictionary<string, List<KeyValuePair<string, bool>>>(StringComparer.OrdinalIgnoreCase);
            var directItems = new List<KeyValuePair<string, bool>>();

            foreach (var childKvp in children)
            {
                if (childKvp.Key.Contains(" → "))
                {
                    var parts = childKvp.Key.Split(new[] { " → " }, 2, StringSplitOptions.None);
                    string subParent = parts[0];
                    if (!subParent.Equals(parentName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!subGroups.ContainsKey(subParent))
                        {
                            subGroups[subParent] = new List<KeyValuePair<string, bool>>();
                        }
                        subGroups[subParent].Add(childKvp);
                        continue;
                    }
                }
                directItems.Add(childKvp);
            }

            // A. 直接の単体項目の描画 (2列グリッドで幅を有効活用)
            if (directItems.Count > 0)
            {
                var directGrid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
                directGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                directGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int totalRows = (directItems.Count + 1) / 2;
                for (int r = 0; r < totalRows; r++)
                {
                    directGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                for (int idx = 0; idx < directItems.Count; idx++)
                {
                    var childKvp = directItems[idx];
                    int col = idx % 2;
                    int row = idx / 2;

                    string fullChildName = childKvp.Key;
                    string childDisplayName = fullChildName.Contains(" → ")
                        ? fullChildName.Split(new[] { " → " }, 2, StringSplitOptions.None)[1]
                        : fullChildName;
                    bool childEnabled = childKvp.Value;

                    var itemCard = new Border
                    {
                        Background = GetThemeBrush("SubtleFillColorTertiaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 4, 10, 4)
                    };

                    var childRowGrid = new Grid();
                    childRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    childRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var childTextBlock = new TextBlock
                    {
                        Text = childDisplayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 12.5,
                        Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray)),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };

                    var childToggle = new ToggleSwitch
                    {
                        IsOn = childEnabled,
                        MinWidth = 0,
                        Margin = new Thickness(0)
                    };

                    string capName = fullChildName;
                    allChildToggles.Add((capName, childToggle));

                    childToggle.Toggled += (s, e) =>
                    {
                        if (_isInitializing || _isUpdatingToggles) return;
                        ConfigService.Current.ShellMenu.ItemVisibilityState[capName] = childToggle.IsOn;
                        ConfigService.Save();

                        _isUpdatingToggles = true;
                        try
                        {
                            masterToggle.IsOn = allChildToggles.All(t => t.Toggle.IsOn);
                        }
                        finally
                        {
                            _isUpdatingToggles = false;
                        }
                    };

                    Grid.SetColumn(childTextBlock, 0);
                    Grid.SetColumn(childToggle, 1);
                    childRowGrid.Children.Add(childTextBlock);
                    childRowGrid.Children.Add(childToggle);
                    itemCard.Child = childRowGrid;

                    Grid.SetColumn(itemCard, col);
                    Grid.SetRow(itemCard, row);
                    directGrid.Children.Add(itemCard);
                }

                childContainer.Children.Add(directGrid);
            }

            // B. 入れ子サブグループの描画
            foreach (var sg in subGroups)
            {
                string subGroupName = sg.Key;
                var subItems = sg.Value;

                if (subItems.Count == 1)
                {
                    var sItem = subItems[0];
                    string fullChildName = sItem.Key;
                    string leafName = fullChildName.Split(new[] { " → " }, 2, StringSplitOptions.None)[1];
                    string singleDisplayName = $"{subGroupName} {leafName}";

                    var childGrid = new Grid();
                    childGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    childGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var childTextBlock = new TextBlock
                    {
                        Text = singleDisplayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 13,
                        Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray))
                    };

                    var childToggle = new ToggleSwitch
                    {
                        IsOn = sItem.Value,
                        MinWidth = 0,
                        Margin = new Thickness(0)
                    };

                    string capName = fullChildName;
                    allChildToggles.Add((capName, childToggle));

                    childToggle.Toggled += (s, e) =>
                    {
                        if (_isInitializing || _isUpdatingToggles) return;
                        ConfigService.Current.ShellMenu.ItemVisibilityState[capName] = childToggle.IsOn;
                        ConfigService.Save();

                        _isUpdatingToggles = true;
                        try
                        {
                            masterToggle.IsOn = allChildToggles.All(t => t.Toggle.IsOn);
                        }
                        finally
                        {
                            _isUpdatingToggles = false;
                        }
                    };

                    Grid.SetColumn(childTextBlock, 0);
                    Grid.SetColumn(childToggle, 1);

                    childGrid.Children.Add(childTextBlock);
                    childGrid.Children.Add(childToggle);
                    childContainer.Children.Add(childGrid);
                    continue;
                }

                // 2個以上ある場合は入れ子サブグループカードを描画
                var subBorder = new Border
                {
                    Background = GetThemeBrush("LayerFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 2, 0, 2)
                };

                var subStack = new StackPanel { Spacing = 6 };
                var subHeaderGrid = new Grid();
                subHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                subHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var subHeaderBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0)
                };

                var subTitleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var subHeaderText = new TextBlock
                {
                    Text = $"{subGroupName} >",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var subExpandIcon = new FontIcon
                {
                    Glyph = "\uE70D",
                    FontSize = 11,
                    Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                subTitleStack.Children.Add(subHeaderText);
                subTitleStack.Children.Add(subExpandIcon);
                subHeaderBtn.Content = subTitleStack;

                var subMasterToggle = new ToggleSwitch
                {
                    IsOn = subItems.All(i => i.Value),
                    MinWidth = 0,
                    Margin = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                Grid.SetColumn(subHeaderBtn, 0);
                Grid.SetColumn(subMasterToggle, 1);
                subHeaderGrid.Children.Add(subHeaderBtn);
                subHeaderGrid.Children.Add(subMasterToggle);
                subStack.Children.Add(subHeaderGrid);

                var subNestedContainer = new StackPanel
                {
                    Spacing = 4,
                    Margin = new Thickness(14, 4, 0, 2),
                    Visibility = Visibility.Collapsed
                };

                subHeaderBtn.Click += (s, e) =>
                {
                    subNestedContainer.Visibility = subNestedContainer.Visibility == Visibility.Visible
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    subExpandIcon.Glyph = subNestedContainer.Visibility == Visibility.Visible ? "\uE70E" : "\uE70D";
                };

                var subChildToggles = new List<(string Key, ToggleSwitch Toggle)>();

                foreach (var sItem in subItems)
                {
                    string fullChildName = sItem.Key;
                    string leafName = fullChildName.Split(new[] { " → " }, 2, StringSplitOptions.None)[1];

                    var nestedGrid = new Grid();
                    nestedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    nestedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var nestedTextBlock = new TextBlock
                    {
                        Text = leafName,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 12,
                        Foreground = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray))
                    };

                    var nestedToggle = new ToggleSwitch
                    {
                        IsOn = sItem.Value,
                        MinWidth = 0,
                        Margin = new Thickness(0)
                    };

                    string capName = fullChildName;
                    subChildToggles.Add((capName, nestedToggle));
                    allChildToggles.Add((capName, nestedToggle));

                    nestedToggle.Toggled += (s, e) =>
                    {
                        if (_isInitializing || _isUpdatingToggles) return;
                        ConfigService.Current.ShellMenu.ItemVisibilityState[capName] = nestedToggle.IsOn;
                        ConfigService.Save();

                        _isUpdatingToggles = true;
                        try
                        {
                            subMasterToggle.IsOn = subChildToggles.All(t => t.Toggle.IsOn);
                            masterToggle.IsOn = allChildToggles.All(t => t.Toggle.IsOn);
                        }
                        finally
                        {
                            _isUpdatingToggles = false;
                        }
                    };

                    Grid.SetColumn(nestedTextBlock, 0);
                    Grid.SetColumn(nestedToggle, 1);
                    nestedGrid.Children.Add(nestedTextBlock);
                    nestedGrid.Children.Add(nestedToggle);
                    subNestedContainer.Children.Add(nestedGrid);
                }

                subMasterToggle.Toggled += (s, e) =>
                {
                    if (_isInitializing || _isUpdatingToggles) return;
                    _isUpdatingToggles = true;
                    try
                    {
                        bool newState = subMasterToggle.IsOn;
                        foreach (var si in subChildToggles)
                        {
                            si.Toggle.IsOn = newState;
                            ConfigService.Current.ShellMenu.ItemVisibilityState[si.Key] = newState;
                        }
                        masterToggle.IsOn = allChildToggles.All(t => t.Toggle.IsOn);
                        ConfigService.Save();
                    }
                    finally
                    {
                        _isUpdatingToggles = false;
                    }
                };

                subStack.Children.Add(subNestedContainer);
                subBorder.Child = subStack;
                childContainer.Children.Add(subBorder);
            }

            masterToggle.Toggled += (s, e) =>
            {
                if (_isInitializing || _isUpdatingToggles) return;
                _isUpdatingToggles = true;
                try
                {
                    bool newState = masterToggle.IsOn;
                    foreach (var c in allChildToggles)
                    {
                        c.Toggle.IsOn = newState;
                        ConfigService.Current.ShellMenu.ItemVisibilityState[c.Key] = newState;
                    }
                    ConfigService.Save();
                }
                finally
                {
                    _isUpdatingToggles = false;
                }
            };

            mainStack.Children.Add(childContainer);
            groupBorder.Child = mainStack;

            _renderedCards.Add(groupBorder);
            _renderedKeys.Add(parentName);
            DetectedItemsContainer.Children.Add(groupBorder);
        }
    }
}
