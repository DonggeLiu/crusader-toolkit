using CrusaderDE;
using Noesis;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace BuildingProduction
{
    /// <summary>
    /// 建筑面板右半边的一列：每行一种物资（先原料后产品），图标 + 近 12 个月的月平均（大字）| 本月至今（小字）。
    /// 整列在羊皮纸上垂直居中。作坊的产品行直接用游戏自己的“选择产品”按钮当图标；
    /// 采石场、奶牛场（没有皮革作坊时）另有一条存量条。显示期间把面板里的说明文字收窄，避免重叠。
    /// </summary>
    internal sealed class ProductionPanel
    {
        private const float RowHeight = 44f;
        private const float IconSize = 40f;
        private const float BarWidth = 110f;
        private const float ContentCenter = 96f; // 羊皮纸可用区域的垂直中心（面板单位）
        private const float MinTop = 30f;
        private const float WarnFill = 0.9f;

        private static readonly SolidColorBrush Up = Brush(0xFF, 0x0A, 0x62, 0x0A);
        private static readonly SolidColorBrush Down = Brush(0xFF, 0xA0, 0x10, 0x10);
        private static readonly SolidColorBrush Flat = Brush(0xFF, 0x5A, 0x50, 0x42);
        private static readonly SolidColorBrush Separator = Brush(0xFF, 0x6E, 0x5E, 0x46);
        private static readonly SolidColorBrush BarNormal = Brush(0xFF, 0x86, 0x7C, 0x6C);
        private static readonly SolidColorBrush BarWarn = Brush(0xFF, 0xC8, 0x7A, 0x0A);

        private readonly Grid host;
        private readonly FrameworkElement hud;
        private readonly Grid table;
        private readonly List<Cell> cells = new List<Cell>();
        private ProductionGoods shownGoods;
        private bool shownBar;

        private Border barFill;
        private FrameworkElement barWarning;

        private sealed class Cell
        {
            public int Key;
            public TextBlock Average;
            public TextBlock Current;
        }

        // 被挪动的游戏控件及其原始布局，隐藏时还原。
        private readonly List<Action> restores = new List<Action>();

        public ProductionPanel(Grid host, FrameworkElement hud)
        {
            this.host = host;
            this.hud = hud;
            table = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(table, 50);
            host.Children.Add(table);
        }

        public bool IsAttachedTo(Grid grid) => ReferenceEquals(host, grid);

        /// <param name="fill">存量条的比例（0–1）；小于 0 表示不显示存量条。</param>
        public void Show(ProductionGoods goods, (Dictionary<int, double> Average, Dictionary<int, int> Current) data, float fill)
        {
            bool showBar = fill >= 0f;
            if (!ReferenceEquals(goods, shownGoods) || showBar != shownBar)
                Build(goods, showBar);

            foreach (Cell cell in cells)
            {
                double? average = null;
                if (data.Average != null)
                    average = data.Average.TryGetValue(cell.Key, out double a) ? a : 0;
                data.Current.TryGetValue(cell.Key, out int current);

                SetNumber(cell.Average, average);
                SetNumber(cell.Current, current);
            }

            if (barFill != null)
            {
                float f = Math.Max(0f, Math.Min(1f, fill));
                barFill.Width = (BarWidth - 4f) * f;
                bool warn = f >= WarnFill;
                barFill.Background = warn ? BarWarn : BarNormal;
                if (barWarning != null)
                    barWarning.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
            }

            table.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            table.Visibility = Visibility.Collapsed;
            RestoreGameLayout();
            shownGoods = null;
        }

        private void Build(ProductionGoods goods, bool showBar)
        {
            RestoreGameLayout();
            shownGoods = goods;
            shownBar = showBar;
            cells.Clear();
            barFill = null;
            barWarning = null;
            table.Children.Clear();
            table.RowDefinitions.Clear();
            table.ColumnDefinitions.Clear();
            for (int i = 0; i < 4; i++)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 奶牛场没有皮革作坊时，牛那一行换成存量条。
            bool herdBar = showBar && goods.Bar == StockBar.DairyHerd;
            var rows = new List<(int Good, bool Input, bool Numbers)>();
            foreach (int good in goods.Inputs)
                rows.Add((good, true, true));
            foreach (int good in goods.Outputs)
                rows.Add((good, false, !(herdBar && good == ProductionGoods.Cows)));
            bool pileBar = showBar && goods.Bar == StockBar.QuarryPile;

            int rowCount = rows.Count + (pileBar ? 1 : 0);
            float top = Math.Max(MinTop, ContentCenter - rowCount * RowHeight / 2f);
            table.Margin = new Thickness(goods.Left, top, 0, 0);

            int row = 0;
            foreach ((int good, bool input, bool numbers) in rows)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
                bool button = !input && goods.Buttons != null;
                if (!button)
                    Place(MakeIcon(good), row, 0);

                if (numbers)
                    AddNumbers(input ? ProductionLedger.ConsumedKey(good) : good, row);
                else
                    AddBar(row, null);
                row++;
            }

            if (pileBar)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
                AddBar(row, "transport");
            }

            if (goods.Buttons != null)
                MoveWorkshopButtons(goods, top);
            NarrowInformationText(goods.Left);
        }

        private void AddNumbers(int key, int row)
        {
            var cell = new Cell
            {
                Key = key,
                Average = new TextBlock
                {
                    FontSize = 24,
                    FontWeight = FontWeight.Bold,
                    // 这一列按本面板最宽的数字自动定宽，数字右对齐贴着竖线：离图标、离竖线都近，各行竖线对齐。
                    Margin = new Thickness(8, 0, 0, 0),
                    TextAlignment = TextAlignment.Right,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Current = new TextBlock
                {
                    FontSize = 18,
                    MinWidth = 30,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            cells.Add(cell);
            Place(cell.Average, row, 1);
            Place(new Border
            {
                Width = 2,
                Height = 20,
                Background = Separator,
                Margin = new Thickness(7, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }, row, 2);
            Place(cell.Current, row, 3);
        }

        private void AddBar(int row, string warning)
        {
            barFill = new Border
            {
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = BarNormal,
            };
            var frame = new Border
            {
                Width = BarWidth,
                Height = 16,
                BorderBrush = Separator,
                BorderThickness = new Thickness(1.5f),
                Padding = new Thickness(1.5f),
                Margin = new Thickness(6, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = barFill,
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(frame);
            if (warning != null)
            {
                var alert = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                var ox = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform };
                if (FindImage("UI-Buildings D007") is ImageSource oxSource) ox.Source = oxSource;
                alert.Children.Add(ox);
                alert.Children.Add(new TextBlock { Text = "!", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = BarWarn });
                barWarning = alert;
                line.Children.Add(barWarning);
            }
            // 放进 Canvas：Canvas 不参与测量宽度，存量条不会把数字列撑宽。
            var holder = new Canvas { Height = RowHeight };
            Canvas.SetTop(line, (RowHeight - 20f) / 2f);
            holder.Children.Add(line);
            Place(holder, row, 1);
        }

        private UIElement MakeIcon(int good)
        {
            var icon = new Image
            {
                Width = IconSize,
                Height = IconSize,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (FindImage(ProductionGoods.IconKey(good)) is ImageSource source)
                icon.Source = source;
            return icon;
        }

        private object FindImage(string key)
        {
            if (key == null)
                return null;
            return hud.TryFindResource(key) ?? MainViewModel.Instance?.HUDmain?.TryFindResource(key) ??
                   MainViewModel.Instance?.HUDTroopPanel?.TryFindResource(key);
        }

        private void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            table.Children.Add(element);
        }

        /// <summary>把作坊的两个产品按钮缩小、竖排，正好落在产品行的图标位置。</summary>
        private void MoveWorkshopButtons(ProductionGoods goods, float top)
        {
            if (!(hud.FindName(goods.Buttons[0]) is FrameworkElement first) || !(first.Parent is StackPanel stack))
                return;

            Orientation orientation = stack.Orientation;
            float width = stack.Width, height = stack.Height;
            Thickness margin = stack.Margin;
            HorizontalAlignment h = stack.HorizontalAlignment;
            VerticalAlignment v = stack.VerticalAlignment;
            restores.Add(() =>
            {
                stack.Orientation = orientation;
                stack.Width = width;
                stack.Height = height;
                stack.Margin = margin;
                stack.HorizontalAlignment = h;
                stack.VerticalAlignment = v;
            });

            stack.Orientation = Orientation.Vertical;
            stack.Width = IconSize;
            stack.Height = float.NaN;
            stack.HorizontalAlignment = HorizontalAlignment.Left;
            stack.VerticalAlignment = VerticalAlignment.Top;
            stack.Margin = new Thickness(goods.Left, top + goods.Inputs.Length * RowHeight + (RowHeight - IconSize) / 2f, 0, 0);

            foreach (string name in goods.Buttons)
            {
                if (!(hud.FindName(name) is FrameworkElement button))
                    continue;
                float bw = button.Width, bh = button.Height;
                Thickness bm = button.Margin;
                restores.Add(() =>
                {
                    button.Width = bw;
                    button.Height = bh;
                    button.Margin = bm;
                });
                button.Width = IconSize;
                button.Height = IconSize;
                button.Margin = new Thickness(0, 0, 0, RowHeight - IconSize);
            }
        }

        /// <summary>面板中部的说明文字（例如系牛桩“牛会把石料……”）默认很宽，显示期间收窄到本列左侧并自动换行。</summary>
        private void NarrowInformationText(float columnLeft)
        {
            if (!(hud.FindName("Information") is TextBlock info))
                return;

            float width = info.Width, height = info.Height;
            Thickness margin = info.Margin;
            restores.Add(() =>
            {
                info.Width = width;
                info.Height = height;
                info.Margin = margin;
            });
            info.Width = Math.Max(100f, columnLeft - margin.Left - 12f);
            info.Height = float.NaN;
            // 换行后变高，往上抬一点，避免压到下面的“工人”两行。
            info.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + 12f);
        }

        private void RestoreGameLayout()
        {
            for (int i = restores.Count - 1; i >= 0; i--)
                restores[i]();
            restores.Clear();
        }

        /// <summary>带符号，最多两位小数（四舍五入）；null 显示“—”。</summary>
        private static void SetNumber(TextBlock text, double? value)
        {
            if (!value.HasValue)
            {
                text.Text = "—";
                text.Foreground = Flat;
                return;
            }

            double v = Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
            string digits = Math.Abs(v).ToString("0.##", CultureInfo.InvariantCulture);
            text.Text = v > 0 ? "+" + digits : v < 0 ? "−" + digits : "0";
            text.Foreground = v > 0 ? Up : v < 0 ? Down : Flat;
        }

        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
