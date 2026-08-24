using System;
using System.Collections.Generic;
using System.Linq;
using FastExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace FastExplorer.Views.Settings
{
    public sealed partial class SettingsControl
    {
        private string _selectedShortcutCategory = "All";
        private string _shortcutSearchFilter = "";

        private void InitShortcutsSection()
        {
            RenderShortcutsList();
        }

        private void RenderShortcutsList()
        {
            if (ShortcutsContainer == null) return;
            ShortcutsContainer.Children.Clear();

            var actions = ShortcutService.AllActions;

            // カテゴリフィルター
            if (_selectedShortcutCategory != "All")
            {
                actions = actions.Where(a => a.Category == _selectedShortcutCategory).ToList();
            }

            // 検索フィルター
            if (!string.IsNullOrWhiteSpace(_shortcutSearchFilter))
            {
                string query = _shortcutSearchFilter.Trim().ToLowerInvariant();
                actions = actions.Where(a =>
                    a.Name.ToLowerInvariant().Contains(query) ||
                    a.Description.ToLowerInvariant().Contains(query) ||
                    a.Category.ToLowerInvariant().Contains(query) ||
                    ShortcutService.GetCurrentShortcut(a.Id).ToLowerInvariant().Contains(query)
                ).ToList();
            }

            if (actions.Count == 0)
            {
                var emptyCard = new Border
                {
                    Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(24, 20, 24, 20)
                };
                var emptyStack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new FontIcon { Glyph = "\uE721", FontSize = 24, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
                emptyStack.Children.Add(new TextBlock { Text = "一致するショートカットが見つかりませんでした", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Center });
                emptyCard.Child = emptyStack;
                ShortcutsContainer.Children.Add(emptyCard);
                return;
            }

            // カテゴリごとにグループ化して表示
            var grouped = actions.GroupBy(a => a.Category);

            foreach (var group in grouped)
            {
                var categoryHeader = new TextBlock
                {
                    Text = group.Key,
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    Margin = new Thickness(4, 12, 0, 4)
                };
                ShortcutsContainer.Children.Add(categoryHeader);

                var cardBorder = new Border
                {
                    Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var itemStack = new StackPanel { Spacing = 10 };
                var list = group.ToList();

                for (int i = 0; i < list.Count; i++)
                {
                    var action = list[i];
                    var itemRow = CreateShortcutItemRow(action);
                    itemStack.Children.Add(itemRow);

                    if (i < list.Count - 1)
                    {
                        itemStack.Children.Add(new Rectangle
                        {
                            Height = 1,
                            Fill = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                            Opacity = 0.6
                        });
                    }
                }

                cardBorder.Child = itemStack;
                ShortcutsContainer.Children.Add(cardBorder);
            }
        }

        private UIElement CreateShortcutItemRow(ShortcutActionDef action)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // アクション名・説明
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // キーバッジ
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 変更ボタン
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // リセットボタン

            // 左: 名前と説明
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var nameBlock = new TextBlock { Text = action.Name, FontWeight = Microsoft.UI.Text.FontWeights.Medium, FontSize = 13.5 };
            var descBlock = new TextBlock { Text = action.Description, FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            textStack.Children.Add(nameBlock);
            textStack.Children.Add(descBlock);

            // 中央: 現在のキーバッジ
            string currentKey = ShortcutService.GetCurrentShortcut(action.Id);
            bool isCustom = ShortcutService.IsCustomized(action.Id);

            var keyBadgePanel = CreateKeyBadgeVisual(currentKey, isCustom);
            keyBadgePanel.Margin = new Thickness(0, 0, 12, 0);

            // 右: 変更ボタン
            var editBtn = new Button
            {
                Content = "変更",
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 12,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            // リセットボタン
            var resetBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE777", FontSize = 11 },
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(4),
                IsEnabled = isCustom,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(resetBtn, "初期値に戻す");

            resetBtn.Click += (s, e) =>
            {
                ShortcutService.ResetShortcut(action.Id);
                RenderShortcutsList();
            };

            editBtn.Click += (s, e) =>
            {
                ShowShortcutEditFlyout(editBtn, action);
            };

            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(keyBadgePanel, 1);
            Grid.SetColumn(editBtn, 2);
            Grid.SetColumn(resetBtn, 3);

            grid.Children.Add(textStack);
            grid.Children.Add(keyBadgePanel);
            grid.Children.Add(editBtn);
            grid.Children.Add(resetBtn);

            return grid;
        }

        private static StackPanel CreateKeyBadgeVisual(string keyCombination, bool isCustom)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            var tokens = keyCombination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keyCombination.EndsWith("++", StringComparison.Ordinal) || (keyCombination.Trim().EndsWith("+", StringComparison.Ordinal) && tokens.Length > 0))
            {
                // "+" キー対策
                var list = new List<string>(tokens);
                list.Add("+");
                tokens = list.Distinct().ToArray();
            }

            foreach (var token in tokens)
            {
                var keyBorder = new Border
                {
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush = isCustom
                        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                        : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var keyText = new TextBlock
                {
                    Text = token,
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = isCustom
                        ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                };

                keyBorder.Child = keyText;
                panel.Children.Add(keyBorder);
            }

            return panel;
        }

        private void ShowShortcutEditFlyout(Button targetButton, ShortcutActionDef action)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

            var flyoutStack = new StackPanel { Spacing = 12, Width = 280 };

            var titleText = new TextBlock
            {
                Text = $"{action.Name} のキー割り当て",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13
            };

            var guideText = new TextBlock
            {
                Text = "キーボードのキーを押して新しいショートカットを設定してください (Ctrl, Shift, Alt と同時押し可能)",
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            };

            string recordedCombo = ShortcutService.GetCurrentShortcut(action.Id);

            var keyDisplayBox = new TextBox
            {
                Text = recordedCombo,
                IsReadOnly = true,
                TextAlignment = TextAlignment.Center,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Height = 36
            };

            var conflictText = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            keyDisplayBox.KeyDown += (s, e) =>
            {
                e.Handled = true;

                bool isCtrl = IsCtrlPressed();
                bool isShift = IsShiftPressed();
                bool isAlt = IsAltPressed();

                if (e.Key == VirtualKey.Control || e.Key == VirtualKey.LeftControl || e.Key == VirtualKey.RightControl ||
                    e.Key == VirtualKey.Shift || e.Key == VirtualKey.LeftShift || e.Key == VirtualKey.RightShift ||
                    e.Key == VirtualKey.Menu || e.Key == VirtualKey.LeftMenu || e.Key == VirtualKey.RightMenu)
                {
                    // 単なる修飾キー単体は表示のみ
                    var modTokens = new List<string>();
                    if (isCtrl) modTokens.Add("Ctrl");
                    if (isAlt) modTokens.Add("Alt");
                    if (isShift) modTokens.Add("Shift");
                    keyDisplayBox.Text = string.Join("+", modTokens);
                    return;
                }

                recordedCombo = ShortcutService.FormatKeyCombination(e.Key, isCtrl, isShift, isAlt);
                keyDisplayBox.Text = recordedCombo;

                var conflict = ShortcutService.FindConflict(action.Id, recordedCombo);
                if (conflict != null)
                {
                    conflictText.Text = $"⚠️ 「{conflict.Name}」と重複しています";
                    conflictText.Visibility = Visibility.Visible;
                }
                else
                {
                    conflictText.Visibility = Visibility.Collapsed;
                }
            };

            var btnStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var saveBtn = new Button
            {
                Content = "決定",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };

            var cancelBtn = new Button
            {
                Content = "キャンセル"
            };

            saveBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(recordedCombo))
                {
                    ShortcutService.SetCustomShortcut(action.Id, recordedCombo);
                    flyout.Hide();
                    RenderShortcutsList();
                }
            };

            cancelBtn.Click += (s, e) =>
            {
                flyout.Hide();
            };

            btnStack.Children.Add(saveBtn);
            btnStack.Children.Add(cancelBtn);

            flyoutStack.Children.Add(titleText);
            flyoutStack.Children.Add(guideText);
            flyoutStack.Children.Add(keyDisplayBox);
            flyoutStack.Children.Add(conflictText);
            flyoutStack.Children.Add(btnStack);

            flyout.Content = flyoutStack;
            flyout.ShowAt(targetButton);

            // フォーカスをキー入力ボックスへ当てる
            flyout.Opened += (s, e) =>
            {
                keyDisplayBox.Focus(FocusState.Programmatic);
            };
        }

        #region イベントハンドラー

        private void ShortcutSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            _shortcutSearchFilter = ShortcutSearchBox.Text;
            RenderShortcutsList();
        }

        private void ShortcutCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (ShortcutCategoryComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _selectedShortcutCategory = tag;
                RenderShortcutsList();
            }
        }

        private async void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "すべてのショートカットをリセット",
                Content = "すべてのキーボードショートカット設定を規定値（デフォルト）に戻しますか？",
                PrimaryButtonText = "リセット",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                ShortcutService.ResetAll();
                RenderShortcutsList();
            }
        }

        #endregion
    }
}
