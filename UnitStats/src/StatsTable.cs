using Noesis;
using System.Collections.Generic;

namespace UnitStats
{
    /// <summary>
    /// 常驻属性表：挂在游戏主 HUD（MainHUD）的根 Grid 上，贴在底部面板上方右侧，
    /// 列为当前菜单里的兵种，行为各项属性。不接收鼠标，不影响底下的地图操作。
    /// </summary>
    internal sealed class StatsTable
    {
        private static float ColumnWidth => UnitStatsText.IsChinese ? 86f : 96f;
        private static float LabelWidth => UnitStatsText.IsChinese ? 90f : 116f;
        private const float FontSizeValue = 13f;

        private static readonly SolidColorBrush Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x0C, 0x08));
        private static readonly SolidColorBrush ValueBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
        private static readonly SolidColorBrush HeaderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xD8, 0x9C));
        private static readonly SolidColorBrush LabelBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xB8, 0xB0, 0x98));

        private Border border;
        private Panel host;

        public bool IsAttached => border != null && host != null;

        /// <summary>挂到给定容器上（已挂在同一容器则不动）。</summary>
        public bool AttachTo(Panel container)
        {
            if (container == null || ReferenceEquals(container, host))
                return false;

            Detach();
            border = new Border
            {
                Background = Background,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                // 原版 FatControler.overNoesisGUI 的命中检测不理会 IsHitTestVisible，只跳过 Tag = "Ignore"，
                // 否则表格下面的地图点不动。
                Tag = "Ignore",
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(border, 100);
            container.Children.Add(border);
            host = container;
            return true;
        }

        public void Detach()
        {
            if (border != null && host != null)
                host.Children.Remove(border);
            border = null;
            host = null;
        }

        public void SetOffset(float right, float bottom)
        {
            if (border != null)
            {
                border.Margin = new Thickness(0, 0, right, bottom);
                if (border.Child is Viewbox fit && host != null && host.ActualWidth > 0)
                    fit.MaxWidth = System.Math.Max(200f, host.ActualWidth - right - 24f);
            }
        }

        public void Hide()
        {
            if (border != null)
                border.Visibility = Visibility.Collapsed;
        }

        public void Show(IReadOnlyList<UnitColumn> columns)
        {
            if (border == null)
                return;

            if (columns == null || columns.Count == 0)
            {
                Hide();
                return;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
            for (int c = 0; c < columns.Count; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth) });

            string[] labels = UnitStatsData.RowLabels;
            int rows = labels.Length + 2;
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < labels.Length; r++)
                Add(grid, labels[r], 0, r + 1, LabelBrush, TextAlignment.Left);

            for (int c = 0; c < columns.Count; c++)
            {
                Add(grid, columns[c].Name, c + 1, 0, HeaderBrush, TextAlignment.Center);
                for (int r = 0; r < columns[c].Values.Length; r++)
                    Add(grid, columns[c].Values[r], c + 1, r + 1, ValueBrush, TextAlignment.Center);
            }

            var note = new TextBlock
            {
                Text = UnitStatsText.Notes, FontSize = 11f, Foreground = LabelBrush,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
            };
            Grid.SetRow(note, rows - 1);
            Grid.SetColumnSpan(note, columns.Count + 1);
            grid.Children.Add(note);
            // Fit narrow HUDs without enlarging the normal-size table.
            var fit = new Viewbox { Child = grid, Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly };
            if (host.ActualWidth > 0)
                fit.MaxWidth = System.Math.Max(200f, host.ActualWidth - border.Margin.Right - 24f);
            border.Child = fit;
            border.Visibility = Visibility.Visible;
        }

        private static void Add(Grid grid, string text, int column, int row, Brush brush, TextAlignment alignment)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = FontSizeValue,
                Foreground = brush,
                TextAlignment = alignment,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(3, 2, 3, 2),
            };
            Grid.SetColumn(block, column);
            Grid.SetRow(block, row);
            grid.Children.Add(block);
        }
    }
}
