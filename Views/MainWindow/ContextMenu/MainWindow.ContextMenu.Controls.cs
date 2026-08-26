using System;
using System.Linq;
using FastExplorer.Views.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FastExplorer
{
    public sealed partial class MainWindow
    {
        #region Context Menu Dynamic UI Controls & Styling

        private static FrameworkElement CreateMarqueeLabel(string text, out Action onPointerEntered, out Action onPointerExited)
        {
            var container = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Clip = new RectangleGeometry()
            };

            var transform = new TranslateTransform();
            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                RenderTransform = transform
            };

            // Horizontal StackPanel を配置することで、ホバー時に TextBlock が親の横幅制約を受けずに ... を完全解除して全長を展開可能にする
            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            stackPanel.Children.Add(label);
            container.Children.Add(stackPanel);

            // コンテナのサイズが変わったときにクリッピング領域と通常時の幅を更新
            container.SizeChanged += (s, args) =>
            {
                if (args.NewSize.Width > 0 && args.NewSize.Height > 0)
                {
                    if (container.Clip is RectangleGeometry rectGeo)
                    {
                        rectGeo.Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height);
                    }
                    if (label.TextTrimming == TextTrimming.CharacterEllipsis)
                    {
                        label.Width = args.NewSize.Width;
                    }
                }
            };

            DispatcherTimer? timer = null;
            double currentX = 0;
            double targetOverflow = 0;
            int direction = -1;
            int pauseFrames = 0;

            onPointerEntered = () =>
            {
                // 全文の本来の幅を正確に測定
                var measureBlock = new TextBlock
                {
                    Text = text,
                    FontSize = label.FontSize,
                    FontFamily = label.FontFamily,
                    FontWeight = label.FontWeight
                };
                measureBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

                double fullTextWidth = measureBlock.DesiredSize.Width;
                double containerWidth = container.ActualWidth;

                if (fullTextWidth > containerWidth + 2 && containerWidth > 0)
                {
                    // ホバー時は省略を完全解除し、幅制約を無くして StackPanel 内で全文字を展開
                    label.TextTrimming = TextTrimming.None;
                    label.Width = double.NaN;

                    targetOverflow = fullTextWidth - containerWidth + 14;
                    currentX = 0;
                    direction = -1;
                    pauseFrames = 15; // 約250ms待機してからスクロール開始

                    timer?.Stop();
                    timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(16) // 60 FPS
                    };
                    timer.Tick += (s, e) =>
                    {
                        if (pauseFrames > 0)
                        {
                            pauseFrames--;
                            return;
                        }

                        double speed = 1.0; // 毎フレーム 1px
                        currentX += direction * speed;

                        if (direction < 0 && currentX <= -targetOverflow)
                        {
                            currentX = -targetOverflow;
                            direction = 1;
                            pauseFrames = 35; // 端で約550ms停止
                        }
                        else if (direction > 0 && currentX >= 0)
                        {
                            currentX = 0;
                            direction = -1;
                            pauseFrames = 35; // 先頭で約550ms停止
                        }

                        transform.X = currentX;
                    };
                    timer.Start();
                }
            };

            onPointerExited = () =>
            {
                if (timer != null)
                {
                    timer.Stop();
                    timer = null;
                }
                currentX = 0;
                transform.X = 0;
                // 離脱時はコンテナ幅で省略表示 (CharacterEllipsis) に完全復帰
                double containerWidth = container.ActualWidth;
                if (containerWidth > 0)
                {
                    label.Width = containerWidth;
                }
                label.TextTrimming = TextTrimming.CharacterEllipsis;
            };

            return container;
        }

        private Button CreateContextButton(string glyph, string text, RoutedEventHandler onClick, Style? style = null)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = null
            };
            if (style != null) btn.Style = style;

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var marqueeContainer = CreateMarqueeLabel(text, out var marqueeEnter, out var marqueeExit);
            Grid.SetColumn(marqueeContainer, 1);

            grid.Children.Add(icon);
            grid.Children.Add(marqueeContainer);
            btn.Content = grid;

            // 通常項目にカーソルが来たら、未オープンの予約をキャンセル
            btn.PointerEntered += (s, args) =>
            {
                CancelPendingSubmenuOpen();
                marqueeEnter();
            };

            btn.PointerExited += (s, args) =>
            {
                marqueeExit();
            };

            btn.Click += onClick;
            return btn;
        }

        private Button CreateContextSubmenuButton(string glyph, string text, MenuFlyout subFlyout, Style? style = null)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = subFlyout
            };
            if (style != null) btn.Style = style;

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var marqueeContainer = CreateMarqueeLabel(text, out var marqueeEnter, out var marqueeExit);
            Grid.SetColumn(marqueeContainer, 1);

            var arrow = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = SettingsControl.GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray))
            };
            Grid.SetColumn(arrow, 2);

            grid.Children.Add(icon);
            grid.Children.Add(marqueeContainer);
            grid.Children.Add(arrow);
            btn.Content = grid;

            btn.PointerEntered += (s, args) =>
            {
                ScheduleSubmenuOpen(1, btn, subFlyout);
                marqueeEnter();
            };

            btn.PointerExited += (s, args) =>
            {
                if (_pendingChildFlyout == subFlyout)
                {
                    CancelSubmenuOpen();
                }
                marqueeExit();
            };

            btn.Click += (s, args) =>
            {
                if (_activeSubmenuChain.Any(e => e.Level == 1 && e.Flyout == subFlyout))
                {
                    CancelSubmenuOpen();
                    return;
                }
                ShowSubmenuImmediateInternal(1, btn, subFlyout);
            };

            return btn;
        }

        private static Border CreateContextMenuSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = SettingsControl.GetThemeBrush("CardStrokeColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Gray)),
                Margin = new Thickness(4, 2, 4, 2)
            };
        }

        /// <summary>
        /// 「その他のオプションを表示 (Shift+右クリック)」のテキスト実描画幅を基準に、
        /// コンテキストメニューの理想的な横幅を動的に計測・算出（多言語・スケーリング完全対応）
        /// </summary>
        private static double CalculateStandardContextMenuWidth()
        {
            try
            {
                // 将来の多言語化 (OS/言語リソース) にも対応可能な基準テキスト
                string referenceText = "その他のオプションを表示 (Shift+右クリック)";

                var dummyTextBlock = new TextBlock
                {
                    Text = referenceText,
                    FontSize = 13
                };

                dummyTextBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double textWidth = dummyTextBlock.DesiredSize.Width;

                // 内訳:
                // ContextMenuRootPanel 内の実際のコンテンツ幅:
                // テキスト実測幅: textWidth
                // アイコン幅: 20
                // アイコンとテキストの間隔 (ColumnSpacing): 12
                // ボタンの左右パディング (Padding="10,6" -> 左右で 20)
                // ContextMenuItemsPanel の右マージン (Margin="0,0,6,0" -> 6)
                // フォントレンダリングおよびスクロールバー微小余白: 12
                double totalWidth = textWidth + 20 + 12 + 20 + 6 + 12;

                return Math.Clamp(Math.Ceiling(totalWidth), 270, 500);
            }
            catch
            {
                return 315;
            }
        }

        #endregion
    }
}
