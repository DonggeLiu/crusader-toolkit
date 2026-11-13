using CrusaderDE;
using Noesis;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AutoTax
{
    // 一次刷新要显示的内容。
    internal sealed class TaxPanelState
    {
        public bool Enabled;
        public string Verb = "", Detail = "", Explanation = "";
        public bool Alert;
        public int Adjustments;
        public double Extra;
        public double Lower, Upper;
    }

    // 把自动调税做进原版要塞羊皮纸（800×170 的 KeepPanel，以下坐标都是面板单位）：
    // 左列对齐滑槽左端（本月税收 / 税档与民心影响），右列右对齐、与问号按钮保持间距
    // （状态行 / 火漆印开关与累计额外收益 / 目标区间）。原版控件只移动位置，绑定照常更新。
    internal sealed class TaxPanelView
    {
        private const float Gap = 10;          // 组内间距
        private const float HelpClearance = 21; // 右列与问号按钮的距离
        private const float StatusY = 41, TopRowY = 74, BottomRowY = 146; // 各行中线
        private const float LevelStep = 25;    // 滑槽每档宽度
        private const float GlideSeconds = 0.35f, TrailSeconds = 1.6f;

        private static readonly Color Ink = Color.FromArgb(255, 22, 15, 8);
        private static readonly Color Sepia = Color.FromArgb(255, 92, 60, 28);
        private static readonly Color Red = Color.FromArgb(255, 150, 32, 20);
        private static readonly Color Wax = Color.FromArgb(255, 150, 28, 22);
        private static readonly Color WaxEdge = Color.FromArgb(255, 105, 15, 10);
        private static readonly Color WaxText = Color.FromArgb(255, 238, 196, 166);
        private static readonly Color Gold = Color.FromArgb(255, 214, 160, 34);
        private const float FadedOpacity = 0.5f;

        private readonly Grid host;
        private readonly HUD_Buildings hud;
        private readonly Grid overlay;
        private readonly Canvas effects;
        private readonly FontFamily font;
        private readonly List<Action> restore = new List<Action>();
        private readonly float left, right;

        private readonly StackPanel statusRow, extraRow, targetRow;
        private readonly Path cycleIcon;
        private readonly TextBlock verb, detail, count, extraLabel, extraValue, lowerText, upperText;
        private readonly Grid seal;
        private readonly Path sealWax;
        private readonly Ellipse sealRing, sealOutline;
        private readonly TextBlock sealText;
        private readonly Border tip;
        private readonly TextBlock tipText;

        private readonly Image handle;
        private Image popHead, popIcon;
        private TextBlock incomeText, rateText, popText;
        private Image glider, ghost;
        private Ellipse glow;
        private readonly List<Ellipse> dots = new List<Ellipse>();
        private float animationStart = -1;
        private float fromX, toX, handleY, handleW, handleH;

        public TaxPanelView(Grid host, HUD_Buildings hud, Action toggle, Action<bool, int> adjust)
        {
            this.host = host;
            this.hud = hud;
            font = (Find("KeepTaxRate") as TextBlock)?.FontFamily;

            left = PanelX(Find("TaxDownButton"), 175);
            float helpX = PanelX(Find("BuildingHelpButton"), 512);
            right = Math.Min(helpX - HelpClearance, 800 - HelpClearance);

            overlay = new Grid();
            effects = new Canvas { IsHitTestVisible = false };
            overlay.Children.Add(effects);

            // 状态行：循环箭头 + 动作 + 民心 | 调整次数；悬停显示原因。
            statusRow = Row(StatusY, 18);
            statusRow.Background = Brushes.Transparent;
            cycleIcon = new Path {
                Data = Geometry.Parse("M 9.5,3.2 A 4.6,4.6 0 1 1 2.6,5.4 M 9.9,0.6 L 10.2,4.2 L 6.7,3.9"),
                Stroke = Brush(Wax), StrokeThickness = 1.6f, Width = 12, Height = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            };
            statusRow.Children.Add(cycleIcon);
            verb = Text("", 14, Ink, true);
            detail = Text("", 11, Sepia);
            detail.Margin = new Thickness(5, 1, 0, 0);
            count = Text("", 10, Sepia);
            count.Opacity = FadedOpacity;
            statusRow.Children.Add(verb);
            statusRow.Children.Add(detail);
            statusRow.Children.Add(new Border {
                Width = 1, Height = 10, Background = Brush(Sepia), Opacity = FadedOpacity,
                Margin = new Thickness(Gap / 2, 0, Gap / 2, 0), VerticalAlignment = VerticalAlignment.Center
            });
            statusRow.Children.Add(count);
            tipText = Text("", 11, Ink);
            tipText.TextWrapping = TextWrapping.Wrap;
            tipText.MaxWidth = 300;
            tip = new Border {
                Child = tipText, Padding = new Thickness(7, 4, 7, 5),
                Background = Brush(Color.FromArgb(250, 239, 226, 196)),
                BorderBrush = Brush(Sepia), BorderThickness = new Thickness(1),
                // 面板上方会被裁掉，所以说明框放在面板内、状态行正下方，悬停时暂时盖住下面几行。
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, StatusY + 10, 800 - right, 0),
                Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            statusRow.MouseEnter += (_, __) => tip.Visibility = string.IsNullOrEmpty(tipText.Text) ? Visibility.Collapsed : Visibility.Visible;
            statusRow.MouseLeave += (_, __) => tip.Visibility = Visibility.Collapsed;

            // 火漆印开关 + 累计额外收益。
            extraRow = Row(TopRowY, 28);
            seal = new Grid { Width = 28, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            sealWax = new Path { Data = Geometry.Parse(SealOutline(14, 14, 13.2f)), Fill = Brush(Wax) };
            sealRing = new Ellipse { Width = 19, Height = 19, Stroke = Brush(WaxEdge), StrokeThickness = 1.2f };
            sealOutline = new Ellipse { Width = 26, Height = 26, Stroke = Brush(Sepia), StrokeThickness = 1, Opacity = FadedOpacity };
            sealText = Text("", 9, WaxText);
            sealText.HorizontalAlignment = HorizontalAlignment.Center;
            seal.Children.Add(sealWax);
            seal.Children.Add(sealRing);
            seal.Children.Add(sealOutline);
            seal.Children.Add(sealText);
            seal.MouseLeftButtonDown += (_, e) => { e.Handled = true; toggle(); };
            extraRow.Children.Add(seal);
            extraLabel = Text(ReleaseText.Pick("额外收益", "Extra"), 10, Sepia);
            extraLabel.Margin = new Thickness(Gap, 1, Gap / 2, 0);
            extraValue = Text("", 17, Ink, true);
            extraRow.Children.Add(extraLabel);
            extraRow.Children.Add(extraValue);
            if (CoinSource() is ImageSource coin)
                extraRow.Children.Add(new Image { Source = coin, Height = 30, Stretch = Stretch.Uniform,
                    Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

            // 目标区间：每个数字上下各一个小三角，点一下 ±0.25。
            targetRow = Row(BottomRowY, 40);
            var targetLabel = Text(ReleaseText.Pick("目标", "Target"), 10, Sepia);
            targetLabel.Margin = new Thickness(0, 1, Gap, 0);
            targetRow.Children.Add(targetLabel);
            lowerText = Text("", 15, Ink, true);
            upperText = Text("", 15, Ink, true);
            targetRow.Children.Add(Spinner(lowerText, step => adjust(true, step)));
            var dash = Text("–", 13, Sepia);
            dash.Margin = new Thickness(5, 0, 5, 0);
            targetRow.Children.Add(dash);
            targetRow.Children.Add(Spinner(upperText, step => adjust(false, step)));

            overlay.Children.Add(statusRow);
            overlay.Children.Add(extraRow);
            overlay.Children.Add(targetRow);
            overlay.Children.Add(tip);
            Panel.SetZIndex(overlay, 100);
            host.Children.Add(overlay);

            // 左列：原版的纳税人数／本月税收和税档／民心影响是定宽居中的，间距没法对齐，
            // 所以隐藏原版这两组，按同一套间距重画；文字取自原版界面的数据，图标沿用原版图片。
            if (Find("KeepIncomeText") is FrameworkElement nativeIncome && nativeIncome.Parent is FrameworkElement incomeRow)
                Collapse(incomeRow);
            if (Find("KeepTaxRate") is FrameworkElement nativeRate && nativeRate.Parent is FrameworkElement rateRow)
                Collapse(rateRow);
            popHead = Find("WGT_TaxPop") is FrameworkElement pop ? FindChild(pop, "PopHead") as Image : null;

            var incomeLine = LeftRow(TopRowY, 28);
            incomeText = Text("", 20, Ink);
            incomeLine.Children.Add(incomeText);
            if (CoinSource() is ImageSource coinIcon)
                incomeLine.Children.Add(new Image { Source = coinIcon, Height = 30, Stretch = Stretch.Uniform,
                    Margin = new Thickness(Gap / 4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var rateLine = LeftRow(BottomRowY, 28);
            rateText = Text("", 20, Ink);
            popText = Text("", 20, Ink);
            // 税档名以全角引号结尾，引号右侧自带空白，少留一点，视觉上才是组内间距。
            popText.Margin = new Thickness(Gap - 6, 0, 3, 0);
            popIcon = new Image { Height = 23, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            rateLine.Children.Add(rateText);
            rateLine.Children.Add(popText);
            rateLine.Children.Add(popIcon);
            overlay.Children.Add(incomeLine);
            overlay.Children.Add(rateLine);
            handle = Find("SliderHandle") as Image;
        }

        public bool IsFor(Grid panel) => ReferenceEquals(host, panel);

        public void Update(TaxPanelState s)
        {
            overlay.Visibility = Visibility.Visible;
            var vm = MainViewModel.Instance;
            if (vm != null)
            {
                incomeText.Text = vm.InKeepIncomeText ?? "";
                rateText.Text = vm.InKeepTaxRateText ?? "";
                popText.Text = vm.InKeepTaxPopText ?? "";
            }
            if (popHead != null) popIcon.Source = popHead.Source;
            verb.Text = s.Verb;
            verb.Foreground = Brush(s.Alert ? Red : Ink);
            detail.Text = s.Detail;
            detail.Foreground = Brush(s.Alert ? Red : Sepia);
            count.Text = ReleaseText.Pick($"{s.Adjustments} 次调整", $"{s.Adjustments} adjustments");
            tipText.Text = s.Explanation ?? "";
            cycleIcon.Stroke = Brush(s.Alert ? Red : Wax);
            cycleIcon.Opacity = s.Enabled ? 1 : FadedOpacity;
            if (!s.Enabled) { verb.Foreground = Brush(Sepia); cycleIcon.Stroke = Brush(Sepia); }

            sealWax.Visibility = sealRing.Visibility = s.Enabled ? Visibility.Visible : Visibility.Collapsed;
            sealOutline.Visibility = s.Enabled ? Visibility.Collapsed : Visibility.Visible;
            sealText.Text = s.Enabled ? ReleaseText.Pick("自动", "AUTO") : ReleaseText.Pick("手动", "OFF");
            sealText.Foreground = Brush(s.Enabled ? WaxText : Sepia);
            extraValue.Text = FormatGold(s.Extra);
            extraLabel.Opacity = s.Enabled ? 1 : FadedOpacity;
            for (int i = 1; i < extraRow.Children.Count; i++) extraRow.Children[i].Opacity = s.Enabled ? 1 : FadedOpacity;
            extraLabel.Opacity = FadedOpacity + (s.Enabled ? 0.3f : 0);

            lowerText.Text = s.Lower.ToString("0.##", CultureInfo.InvariantCulture);
            upperText.Text = s.Upper.ToString("0.##", CultureInfo.InvariantCulture);
            targetRow.Opacity = s.Enabled ? 1 : FadedOpacity;
        }

        // 自动调税写入新税档时调用：手柄滑到新位置，旧位置留渐隐虚影和金点轨迹，新位置闪一下金光。
        public void AnimateMove(int from, int to, float now)
        {
            if (handle == null || from == to) return;
            FinishAnimation();
            fromX = PanelX(handle, float.NaN);
            if (float.IsNaN(fromX)) return;
            handleY = PanelY(handle, 89);
            handleW = handle.ActualWidth;
            handleH = handle.ActualHeight;
            toX = fromX + LevelStep * (to - from);

            ghost = new Image { Source = handle.Source, Width = handleW, Height = handleH, Opacity = 0.35f, Stretch = Stretch.Fill };
            Put(ghost, fromX, handleY);
            int n = (int)(Math.Abs(toX - fromX) / 5);
            for (int i = 1; i < n; i++)
            {
                var dot = new Ellipse { Width = 2.4f, Height = 2.4f, Fill = Brush(Gold), Opacity = 0 };
                Put(dot, fromX + handleW / 2 + (toX - fromX) * i / n - 1.2f, handleY - 4.5f);
                dots.Add(dot);
            }
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(170, 255, 205, 70), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 205, 70), Offset = 1 });
            glow = new Ellipse { Width = handleW + 22, Height = handleH + 16, Fill = brush, Opacity = 0 };
            Put(glow, toX + handleW / 2 - glow.Width / 2, handleY + handleH / 2 - glow.Height / 2);
            glider = new Image { Source = handle.Source, Width = handleW, Height = handleH, Stretch = Stretch.Fill };
            Put(glider, fromX, handleY);
            handle.Opacity = 0;
            animationStart = now;
        }

        // 每帧调用，推进动画。
        public void Animate(float now)
        {
            if (animationStart < 0) return;
            float t = now - animationStart;
            float glide = Math.Min(1f, t / GlideSeconds);
            float eased = 1 - (1 - glide) * (1 - glide) * (1 - glide);
            if (glider != null) Canvas.SetLeft(glider, fromX + (toX - fromX) * eased);
            if (glide >= 1 && glider != null)
            {
                effects.Children.Remove(glider);
                glider = null;
                handle.Opacity = 1;
            }
            float fade = Math.Max(0f, 1 - Math.Max(0f, t - GlideSeconds) / (TrailSeconds - GlideSeconds));
            for (int i = 0; i < dots.Count; i++)
            {
                float shown = Math.Min(1f, eased * dots.Count / Math.Max(1, i + 1));
                dots[i].Opacity = (eased * dots.Count >= i ? 0.9f : 0f) * fade * Math.Min(1f, shown);
            }
            if (ghost != null) ghost.Opacity = 0.35f * fade;
            if (glow != null) glow.Opacity = (float)(glide < 1 ? glide : fade);
            if (t >= TrailSeconds) FinishAnimation();
        }

        public void Hide()
        {
            overlay.Visibility = Visibility.Collapsed;
            tip.Visibility = Visibility.Collapsed;
            FinishAnimation();
        }

        public void Detach()
        {
            FinishAnimation();
            host.Children.Remove(overlay);
            for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
            restore.Clear();
        }

        private void FinishAnimation()
        {
            effects.Children.Clear();
            dots.Clear();
            glider = ghost = null;
            glow = null;
            if (handle != null) handle.Opacity = 1;
            animationStart = -1;
        }

        private object Find(string name) => hud?.FindName(name);

        private static FrameworkElement FindChild(DependencyObject root, string name)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Name == name) return fe;
                var found = FindChild(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private ImageSource CoinSource()
        {
            if (Find("KeepIncomeText") is FrameworkElement text && text.Parent is StackPanel row)
            {
                int i = row.Children.IndexOf(text);
                if (i + 1 < row.Children.Count && row.Children[i + 1] is Image icon) return icon.Source;
            }
            return null;
        }

        // 元素左上角在面板中的坐标（面板单位）；取不到时用 fallback。
        private float PanelX(object element, float fallback) => PanelPoint(element, fallback, true);
        private float PanelY(object element, float fallback) => PanelPoint(element, fallback, false);
        private float PanelPoint(object element, float fallback, bool x)
        {
            try
            {
                if (!(element is UIElement e) || host.ActualWidth <= 0) return fallback;
                Point origin = host.PointToScreen(new Point(0, 0));
                Point unit = host.PointToScreen(new Point(100, 100));
                float scale = (unit.X - origin.X) / 100;
                if (scale <= 0) return fallback;
                Point p = e.PointToScreen(new Point(0, 0));
                return x ? (p.X - origin.X) / scale : (p.Y - origin.Y) / scale;
            }
            catch { return fallback; }
        }

        private void Put(UIElement element, float x, float y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            effects.Children.Add(element);
        }

        private void Collapse(UIElement element)
        {
            var v = element.Visibility;
            element.Visibility = Visibility.Collapsed;
            restore.Add(() => element.Visibility = v);
        }

        // 与滑槽左端对齐的一行，竖直方向以 centerY 为中线。
        private StackPanel LeftRow(float centerY, float height) => new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Height = height, Margin = new Thickness(left, centerY - height / 2, 0, 0), IsHitTestVisible = false
        };

        // 右对齐的一行，竖直方向以 centerY 为中线。
        private StackPanel Row(float centerY, float height) => new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Height = height, Margin = new Thickness(0, centerY - height / 2, 800 - right, 0)
        };

        private TextBlock Text(string text, float size, Color color, bool bold = false)
        {
            var t = new TextBlock {
                Text = text, FontSize = size, Foreground = Brush(color),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
            };
            if (font != null) t.FontFamily = font;
            return t;
        }

        private UIElement Spinner(TextBlock value, Action<int> step)
        {
            value.HorizontalAlignment = HorizontalAlignment.Center;
            value.Margin = new Thickness(0, -2, 0, -2);
            var column = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(Arrow(true, () => step(1)));
            column.Children.Add(value);
            column.Children.Add(Arrow(false, () => step(-1)));
            return column;
        }

        private UIElement Arrow(bool up, Action click)
        {
            var hit = new Border { Width = 24, Height = 8, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            hit.Child = new Path {
                Data = Geometry.Parse(up ? "M 0,4 L 4,0 L 8,4 Z" : "M 0,0 L 8,0 L 4,4 Z"),
                Fill = Brush(Sepia), Width = 8, Height = 4,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
            return hit;
        }

        // 火漆印的波浪外缘。
        private static string SealOutline(double cx, double cy, double r)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i <= 48; i++)
            {
                double a = 2 * Math.PI * i / 48;
                double rr = r * (1 + 0.05 * Math.Sin(a * 7) + 0.025 * Math.Sin(a * 13)) / 1.075;
                sb.Append(i == 0 ? "M " : " L ")
                  .Append((cx + rr * Math.Cos(a)).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append((cy + rr * Math.Sin(a)).ToString("0.##", CultureInfo.InvariantCulture));
            }
            return sb.Append(" Z").ToString();
        }

        private static string FormatGold(double gold)
        {
            long g = (long)Math.Round(gold);
            return g > 0 ? "+" + g.ToString("N0", CultureInfo.InvariantCulture) : g.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static SolidColorBrush Brush(Color c) => new SolidColorBrush(c);
    }
}
