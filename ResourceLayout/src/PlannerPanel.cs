using CrusaderDE;
using Noesis;
using System.Collections.Generic;

namespace ResourceLayout
{
    /// <summary>
    /// 规划模式时右下角（底部面板上方）的小条：可选建筑图标（点击或 Shift+数字切换），每种的规划数量，状态和操作提示。
    /// 挂在 MainHUD 的根 Grid 上。
    /// </summary>
    internal sealed class PlannerPanel
    {
        private static readonly SolidColorBrush Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x0C, 0x08));
        private static readonly SolidColorBrush Text = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xE0, 0xC8));
        private static readonly SolidColorBrush Hint = new SolidColorBrush(Color.FromArgb(0xFF, 0xB8, 0xB0, 0x98));
        private static readonly SolidColorBrush Off = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80));

        private Border border;
        private Panel host;
        private StackPanel toggles;
        private TextBlock title, status, hint;
        private readonly List<Border> toggleBoxes = new List<Border>();
        private readonly List<TextBlock> countTexts = new List<TextBlock>();
        private LayoutType[] shownTypes;

        public bool IsMouseOver => border != null && border.Visibility == Visibility.Visible && border.IsMouseOver;

        /// <summary>鼠标下的类型开关下标，没有则 -1。</summary>
        public int ToggleUnderMouse()
        {
            for (int i = 0; i < toggleBoxes.Count; i++)
                if (toggleBoxes[i].IsMouseOver)
                    return i;
            return -1;
        }

        public void EnsureAttached()
        {
            HUD_Buildings buildings = MainViewModel.Instance?.HUDBuildingPanel;
            Panel root = (buildings?.Parent as FrameworkElement)?.Parent as Panel;
            if (root == null || ReferenceEquals(root, host))
                return;

            if (border != null && host != null)
                host.Children.Remove(border);

            title = new TextBlock { FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Text, Margin = new Thickness(0, 0, 0, 4) };
            toggles = new StackPanel { Orientation = Orientation.Horizontal };
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 530, FontSize = 14, Foreground = Text, Margin = new Thickness(0, 4, 0, 0) };
            hint = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 530, FontSize = 13, Foreground = Hint, Margin = new Thickness(0, 2, 0, 0) };
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(toggles);
            stack.Children.Add(status);
            stack.Children.Add(hint);

            border = new Border
            {
                Child = stack,
                Background = Background,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 176),
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(border, 120);
            root.Children.Add(border);
            host = root;
            shownTypes = null;
        }

        public void Show(string heading, LayoutType[] types, bool[] selected, int[] counts, int[] mins, string statusText, string hintText)
        {
            EnsureAttached();
            if (border == null)
                return;

            if (!ReferenceEquals(types, shownTypes))
                BuildToggles(types);

            title.Text = heading;
            for (int i = 0; i < types.Length; i++)
            {
                toggleBoxes[i].Opacity = selected[i] ? 1f : 0.45f;
                toggleBoxes[i].BorderBrush = selected[i] ? new SolidColorBrush(ToNoesis(types[i].Color)) : Off;
                string label = counts != null && selected[i] ? $"{types[i].DisplayName} ×{counts[i]}" : types[i].DisplayName;
                if (mins != null && selected[i] && mins[i] > 0)
                    label += $"  ≥{mins[i]}";
                countTexts[i].Text = label;
            }
            status.Text = statusText;
            hint.Text = hintText;
            border.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            if (border != null)
                border.Visibility = Visibility.Collapsed;
        }

        private void BuildToggles(LayoutType[] types)
        {
            shownTypes = types;
            toggles.Children.Clear();
            toggleBoxes.Clear();
            countTexts.Clear();
            for (int i = 0; i < types.Length; i++)
            {
                var icon = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform };
                if (FindImage(types[i].Icon) is ImageSource src)
                    icon.Source = src;
                var label = new TextBlock { FontSize = 13, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Center };
                var key = new TextBlock
                {
                    Text = ReferenceEquals(types, LayoutType.Farms) ? $"Shift+{i + 1}" : "",
                    FontSize = 11,
                    Foreground = Hint,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var col = new StackPanel();
                col.Children.Add(icon);
                col.Children.Add(label);
                col.Children.Add(key);
                var box = new Border
                {
                    Child = col,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)), // 可点击
                };
                toggles.Children.Add(box);
                toggleBoxes.Add(box);
                countTexts.Add(label);
            }
        }

        private static object FindImage(string key)
        {
            MainViewModel vm = MainViewModel.Instance;
            return vm?.HUDmain?.TryFindResource(key) ?? vm?.HUDBuildingPanel?.TryFindResource(key);
        }

        private static Color ToNoesis(UnityEngine.Color c) =>
            Color.FromArgb(0xFF, (byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255));
    }
}
