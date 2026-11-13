using CrusaderDE;
using Noesis;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GranaryOverview
{
    /// <summary>
    /// 把粮仓的配给页和食物页合成一页（坐标为粮仓面板单位，面板 800×170，可见的羊皮纸约为 x 10–545、y 22–168）：
    /// 左列总览：总库存，食物满意度竖式相加（当前配给 +N、计入种类的食物 +N、横线、总数）；
    /// 右上四种食物（原版的开关按钮、库存、上月 · 本月净变化、要补的建筑）；
    /// 右下五个配给（原版的配给按钮排成一行，下方每月净变化和库存能撑几个月；当前配给放大、用按钮的高亮贴图，其余变淡）。
    /// 格子之间竖线分隔。用到的原版控件显示期间挪位置、改大小，关闭时全部还原。
    /// </summary>
    internal sealed class GranaryView
    {
        private static readonly string[] RationButtons =
            { "ButtonNoRations", "ButtonHalfRations", "ButtonFullRations", "ButtonExtraRations", "ButtonDoubleRations" };

        // 食物按原版顺序排：肉、奶酪、面包、苹果（下标为 FoodModel.Foods 的下标）。
        private static readonly int[] FoodOrder = { 2, 1, 0, 3 };
        private static readonly string[] FoodButtons = { "EatingBread", "EatingCheese", "EatingMeat", "EatingApples" };
        private static readonly string[] StopImages = { "StopBreadConsumption", "StopCheeseConsumption", "StopMeatConsumption", "StopApplesConsumption" };

        // 库房、粮仓面板里食物的图标（面包、奶酪、肉、苹果）。
        private static readonly string[] FoodIcons = { "UISprites I013", "UISprites I015", "UISprites I017", "UISprites I019" };

        // 布局
        private const float LeftX = 20f, ColX = 128f, RightX = 540f;
        private const float FoodTop = 26f, FoodBottom = 82f, BandY = 84f, RationBottom = 160f;
        private const float FoodW = (RightX - ColX) / 4, RationW = (RightX - ColX) / 5;
        private const float PlateY = 104f, PlateW = 44f, PlateH = 27f, CurrentScale = 1.1f;
        private const float SumRight = 94f; // 左列数字右对齐的位置
        private const float Faded = 0.45f;  // 没选的配给

        private static readonly SolidColorBrush Ink = Brush(0xFF, 0x1E, 0x14, 0x0A);
        private static readonly SolidColorBrush Soft = Brush(0xFF, 0x4A, 0x3C, 0x2C);
        private static readonly SolidColorBrush Up = Brush(0xFF, 0x12, 0x6A, 0x14);
        private static readonly SolidColorBrush Down = Brush(0xFF, 0xA8, 0x12, 0x10);
        // 配给一行的数字压在羊皮纸下缘较深的地方：描一圈浅色边。
        private static readonly SolidColorBrush Halo = Brush(0xC8, 0xF4, 0xEA, 0xD0);

        private readonly HUD_Buildings hud;
        private readonly Grid panel;
        private readonly Canvas back;
        private readonly Canvas front;
        private readonly List<Action> restores = new List<Action>();
        private bool active;

        // 左列
        private TextBlock totalText, unitText, rationPop, typesPop, popText;
        private Image rationIcon, popMask;
        private readonly Image[] typeIcons = new Image[4];

        // 食物格
        private readonly TextBlock[] stockText = new TextBlock[4];
        private readonly TextBlock[] changeLine = new TextBlock[4];
        private readonly Run[] lastText = new Run[4];
        private readonly Run[] nowText = new Run[4];
        private readonly Image[] hintIcon = new Image[4];
        private readonly TextBlock[] hintText = new TextBlock[4];

        // 配给格
        private readonly TextBlock[] netText = new TextBlock[5];
        private readonly TextBlock[] monthsText = new TextBlock[5];
        private readonly Path[] hourglass = new Path[5];
        private readonly StackPanel[] rationNumbers = new StackPanel[5];
        private readonly FrameworkElement[] plates = new FrameworkElement[5];
        private readonly object[] plateSprite = new object[5];
        private Ellipse glow;
        private readonly Ellipse[] veils = new Ellipse[5];
        private int shownCurrent = -1;

        public GranaryView(HUD_Buildings hud, Grid panel)
        {
            this.hud = hud;
            this.panel = panel;
            back = new Canvas { Width = 800, Height = 170, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            front = new Canvas { Width = 800, Height = 170, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            back.UseLayoutRounding = true;
            Panel.SetZIndex(back, 1);
            Panel.SetZIndex(front, 20);
            BuildOverlay();
        }

        public bool IsAttachedTo(Grid grid) => ReferenceEquals(panel, grid);

        public void Show(Snapshot s)
        {
            if (!active)
                Activate();

            // 原版每次换配给都会重新显示手指等：每次刷新都再藏一遍。
            foreach (UIElement child in panel.Children)
                if (child != back && child != front && !IsKept(child))
                    child.Visibility = Visibility.Hidden;

            // “不吃”标记：原版只在打开食物页时刷新，这里按游戏状态自己设。
            for (int food = 0; food < 4; food++)
                if (hud.FindName(StopImages[food]) is UIElement stop)
                    stop.Visibility = s.Model.Enabled[food] ? Visibility.Hidden : Visibility.Visible;

            UpdateLeft(s);
            UpdateFoods(s);
            UpdateRations(s);
        }

        public void Hide()
        {
            if (!active)
                return;
            active = false;
            for (int i = restores.Count - 1; i >= 0; i--)
                restores[i]();
            restores.Clear();
            // 手指和“不吃”标记按游戏状态重新设一遍。
            MainViewModel.Instance?.InGranarySetRationHand(0, 1);
            MainViewModel.Instance?.InFoodReportShowEaten(1);
            panel.Children.Remove(back);
            panel.Children.Remove(front);
            shownCurrent = -1;
        }

        // ---------------- 挂载与还原 ----------------

        private bool IsKept(UIElement child)
        {
            if (!(child is FrameworkElement fe))
                return false;
            if (Array.IndexOf(RationButtons, fe.Name) >= 0 || Array.IndexOf(FoodButtons, fe.Name) >= 0 || Array.IndexOf(StopImages, fe.Name) >= 0)
                return true;
            // 市场交易按钮（只有有市场时显示）。
            return fe is Button && string.IsNullOrEmpty(fe.Name);
        }

        private void Activate()
        {
            active = true;
            panel.Children.Add(back);
            panel.Children.Add(front);

            foreach (UIElement child in panel.Children)
            {
                if (child == back || child == front)
                    continue;
                Visibility visibility = child.Visibility;
                float opacity = child.Opacity;
                UIElement c = child;
                restores.Add(() => { c.Visibility = visibility; c.Opacity = opacity; });
                if (!IsKept(child))
                    child.Visibility = Visibility.Hidden;
                else if (child is Button trade && string.IsNullOrEmpty(trade.Name))
                    Move(trade, 96, 28, 26, 26);
            }

            // 切到食物页的按钮已经用不上（两页合一）；建筑面板共用的“?”只是打开这种建筑的帮助文字，粮仓这页腾出位置给配给。
            // 按钮样式会盖掉 Visibility，直接缩成 0。
            if (hud.FindName("ButtonRationsGoToTypesEaten") is FrameworkElement toTypes)
                Move(toTypes, 0, 0, 0, 0);
            if (hud.FindName("BuildingHelpButton") is FrameworkElement help)
                Move(help, 0, 0, 0, 0);

            for (int level = 0; level < 5; level++)
            {
                if (!(hud.FindName(RationButtons[level]) is FrameworkElement plate))
                    continue;
                plates[level] = plate;
                plateSprite[level] = PropEx.GetSprite1(plate);
                object sprite = plateSprite[level];
                FrameworkElement p = plate;
                restores.Add(() => PropEx.SetSprite1(p, sprite));
                Move(plate, RationCenter(level) - PlateW / 2, PlateY - PlateH / 2, PlateW, PlateH);
                Panel.SetZIndex(plate, 10);
            }

            // 食物开关按钮和“不吃”标记从食物页借过来，关闭时放回原处。
            for (int slot = 0; slot < 4; slot++)
            {
                int food = FoodOrder[slot];
                float x = ColX + slot * FoodW;
                Borrow(FoodButtons[food], x + 4, 34, 29, 27);
                Borrow(StopImages[food], x + 8, 32, 21, 26);
            }
        }

        /// <summary>把控件改成左上角定位，记下原布局以便还原。</summary>
        private void Move(FrameworkElement e, float x, float y, float w, float h)
        {
            Thickness margin = e.Margin;
            HorizontalAlignment ha = e.HorizontalAlignment;
            VerticalAlignment va = e.VerticalAlignment;
            float width = e.Width, height = e.Height, opacity = e.Opacity;
            int z = Panel.GetZIndex(e);
            restores.Add(() =>
            {
                e.Margin = margin;
                e.HorizontalAlignment = ha;
                e.VerticalAlignment = va;
                e.Width = width;
                e.Height = height;
                e.Opacity = opacity;
                Panel.SetZIndex(e, z);
            });
            e.HorizontalAlignment = HorizontalAlignment.Left;
            e.VerticalAlignment = VerticalAlignment.Top;
            e.Margin = new Thickness(x, y, 0, 0);
            e.Width = w;
            e.Height = h;
        }

        private void Borrow(string name, float x, float y, float w, float h)
        {
            if (!(hud.FindName(name) is FrameworkElement e) || !(e.Parent is Panel home))
                return;
            int index = home.Children.IndexOf(e);
            home.Children.Remove(e);
            panel.Children.Add(e);
            restores.Add(() =>
            {
                panel.Children.Remove(e);
                home.Children.Insert(Math.Min(index, home.Children.Count), e);
            });
            Move(e, x, y, w, h);
            Panel.SetZIndex(e, 10);
        }

        // ---------------- 自己画的部分 ----------------

        private static float RationCenter(int level) => ColX + (level + 0.5f) * RationW;

        private void BuildOverlay()
        {
            // 左列：总库存（数字 + 游戏自己的“单位食物”），满意度竖式：配给 +N、食物种类 +N，横线，总数。
            totalText = Text(17, true, Ink);
            Place(front, totalText, LeftX, 61);
            unitText = Text(11, false, Soft);
            Place(front, unitText, LeftX, 80);

            // 总库存与满意度之间一道淡出的分隔线。
            Place(back, Fade(SumRight - LeftX + 8, true, 0x58), LeftX - 4, 97);

            // 竖式顺序与右边一致：先食物种类，再配给。
            for (int i = 0; i < 4; i++)
            {
                typeIcons[i] = new Image { Width = 16, Height = 15, Stretch = Stretch.Uniform };
                Place(front, typeIcons[i], LeftX + i * 11, 103);
            }
            typesPop = Text(15, true, Soft);
            PlaceRight(front, typesPop, SumRight, 101);

            rationIcon = new Image { Width = 28, Height = 17, Stretch = Stretch.Uniform };
            Place(front, rationIcon, LeftX - 2, 119);
            rationPop = Text(15, true, Soft);
            PlaceRight(front, rationPop, SumRight, 117);

            Place(back, Fade(SumRight - LeftX + 6, true, 0x58), LeftX - 3, 135);

            popMask = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform };
            Place(front, popMask, LeftX, 139);
            popText = Text(19, true, Ink);
            PlaceRight(front, popText, SumRight, 137);

            // 分隔线：食物和配给之间一条横线；各格之间竖线。
            // 分隔线两端淡出，像墨线；左列和右边之间不画横线，左列是一整块。
            Place(back, Fade(RightX - ColX, true), ColX, BandY);
            for (int i = 0; i < 4; i++)
                Place(back, Fade(FoodBottom - FoodTop - 6, false), ColX + i * FoodW, FoodTop + 3);
            for (int i = 0; i < 5; i++)
                Place(back, Fade(RationBottom - BandY - 8, false), ColX + i * RationW, BandY + 4);

            // 食物格：图标（原版开关按钮）右边第一行库存，第二行上月 · 本月；格子右上角要补的建筑和座数。
            for (int slot = 0; slot < 4; slot++)
            {
                int food = FoodOrder[slot];
                float x = ColX + slot * FoodW;
                stockText[food] = Text(18, true, Ink);
                Place(front, stockText[food], x + 37, 31);

                // 上月 · 本月放在同一个 TextBlock 里的几段文字：共用一条基线。上月加粗。
                lastText[food] = new Run { FontWeight = FontWeight.Bold };
                nowText[food] = new Run();
                changeLine[food] = Text(13, false, Ink);
                changeLine[food].Inlines.Add(lastText[food]);
                changeLine[food].Inlines.Add(new Run { Text = " · ", Foreground = Soft });
                changeLine[food].Inlines.Add(nowText[food]);
                Place(front, changeLine[food], x + 37, 54);

                hintIcon[food] = new Image { Width = 17, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                hintText[food] = Text(12, true, Down);
                hintText[food].Margin = new Thickness(1, 0, 0, 0);
                // 要补的建筑：格子右上角，与库存同一行。
                var hint = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                hint.Children.Add(hintIcon[food]);
                hint.Children.Add(hintText[food]);
                var hintCell = new Grid { Width = FoodW - 5 };
                hintCell.Children.Add(hint);
                Place(front, hintCell, x, 34);
            }

            // 配给格：当前配给背后的光；盘子下方每月净变化，再下方库存能撑几个月（数字 + 沙漏，不用文字）。
            glow = new Ellipse
            {
                Width = 70,
                Height = 40,
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0xB0, 0xFF, 0xE2, 0x8C), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xE2, 0x8C), Offset = 1 },
                    },
                },
            };
            back.Children.Add(glow);

            // 没选的盘子上盖一层半透明羊皮纸色（按钮样式会覆盖它自己的 Opacity）。
            for (int level = 0; level < 5; level++)
            {
                veils[level] = new Ellipse { Width = PlateW + 4, Height = PlateH + 2, Fill = Brush(0x9C, 0xEC, 0xDC, 0xB8) };
                Place(front, veils[level], RationCenter(level) - (PlateW + 4) / 2, PlateY - (PlateH + 2) / 2);
            }

            for (int level = 0; level < 5; level++)
            {
                netText[level] = Text(16, true, Ink);
                netText[level].HorizontalAlignment = HorizontalAlignment.Center;
                TextElement.SetStroke(netText[level], Halo);
                TextElement.SetStrokeThickness(netText[level], 3);

                monthsText[level] = Text(13, true, Ink);
                TextElement.SetStroke(monthsText[level], Halo);
                TextElement.SetStrokeThickness(monthsText[level], 3);
                hourglass[level] = Hourglass();
                var months = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                months.Children.Add(monthsText[level]);
                months.Children.Add(hourglass[level]);

                rationNumbers[level] = new StackPanel { Width = RationW };
                rationNumbers[level].Children.Add(netText[level]);
                rationNumbers[level].Children.Add(months);
                Place(front, rationNumbers[level], ColX + level * RationW, 120);
            }
        }

        // ---------------- 刷新 ----------------

        internal sealed class Snapshot
        {
            public FoodModel Model;
            public bool Ready; // 已有足够的产量数据
            public int TotalFood;
            public int RationPopularity, TypesPopularity;
            public readonly int?[] LastMonth = new int?[4];
            public readonly int[] ThisMonth = new int[4];
        }

        private void UpdateLeft(Snapshot s)
        {
            FoodModel m = s.Model;
            totalText.Text = s.TotalFood.ToString(CultureInfo.InvariantCulture);
            unitText.Text = GameText(s.TotalFood == 1 ? 2 : 1, ReleaseText.Pick("单位食物", "Food units"));

            int level = Math.Max(0, Math.Min(4, m.Ration));
            rationIcon.Source = plates[level] != null ? PropEx.GetSprite2(plates[level]) as ImageSource : null;
            rationPop.Text = Signed(s.RationPopularity);

            // 计入种类加成的食物：有库存且没关掉（与原版“N 种食物配给”一致）。
            int n = 0;
            for (int slot = 0; slot < 4; slot++)
            {
                int food = FoodOrder[slot];
                if (m.Enabled[food] && m.Stock[food] > 0)
                    typeIcons[n++].Source = FindImage(FoodIcons[food]);
            }
            for (int i = 0; i < typeIcons.Length; i++)
                typeIcons[i].Visibility = i < n ? Visibility.Visible : Visibility.Collapsed;
            typesPop.Text = Signed(s.TypesPopularity);

            int total = s.RationPopularity + s.TypesPopularity;
            popText.Text = Signed(total);
            popMask.Source = Mask(total);
        }

        private void UpdateFoods(Snapshot s)
        {
            FoodModel m = s.Model;
            for (int food = 0; food < 4; food++)
            {
                bool on = m.Enabled[food];
                stockText[food].Text = m.Stock[food].ToString(CultureInfo.InvariantCulture);
                int? last = s.LastMonth[food];
                lastText[food].Text = last.HasValue ? Signed(last.Value) : "—";
                lastText[food].Foreground = last.HasValue ? Tone(last.Value) : Ink;
                nowText[food].Text = Signed(s.ThisMonth[food]);
                nowText[food].Foreground = Tone(s.ThisMonth[food]);
                // 关掉的食物：原版的禁止标记，数字变淡。
                float opacity = on ? 1f : 0.5f;
                stockText[food].Opacity = opacity;
                changeLine[food].Opacity = opacity;

                if (s.Ready && on && m.Hints[food] is BuildHint hint)
                {
                    hintIcon[food].Source = FindImage(hint.Icon);
                    hintIcon[food].Visibility = Visibility.Visible;
                    hintText[food].Text = "+" + hint.Count;
                }
                else
                {
                    hintIcon[food].Visibility = Visibility.Collapsed;
                    hintText[food].Text = "";
                }
            }
        }

        private void UpdateRations(Snapshot s)
        {
            FoodModel m = s.Model;
            int current = Math.Max(0, Math.Min(4, m.Ration));
            if (current != shownCurrent)
            {
                shownCurrent = current;
                for (int level = 0; level < 5; level++)
                {
                    bool cur = level == current;
                    rationNumbers[level].Opacity = cur ? 1f : Faded + 0.15f;
                    veils[level].Visibility = cur ? Visibility.Collapsed : Visibility.Visible;
                    FrameworkElement plate = plates[level];
                    if (plate == null)
                        continue;
                    // 当前配给：放大、用按钮自己的高亮贴图；其余变淡。
                    float scale = cur ? CurrentScale : 1f;
                    float cx = RationCenter(level);
                    plate.Width = PlateW * scale;
                    plate.Height = PlateH * scale;
                    plate.Margin = new Thickness(cx - PlateW * scale / 2, PlateY - PlateH * scale / 2, 0, 0);
                    PropEx.SetSprite1(plate, cur ? PropEx.GetSprite2(plate) : plateSprite[level]);
                }
                Canvas.SetLeft(glow, RationCenter(current) - glow.Width / 2);
                Canvas.SetTop(glow, PlateY - glow.Height / 2);
            }

            for (int level = 0; level < 5; level++)
            {
                if (!s.Ready)
                {
                    netText[level].Text = "…";
                    netText[level].Foreground = Soft;
                    monthsText[level].Text = "";
                    hourglass[level].Visibility = Visibility.Collapsed;
                    continue;
                }
                int net = (int)Math.Round(m.Net[level]);
                netText[level].Text = Signed(net);
                netText[level].Foreground = Tone(net);
                // 四舍五入为 0 的当作持平。
                double? months = net < 0 ? m.Months[level] : null;
                monthsText[level].Text = !months.HasValue ? "–" : months.Value >= 100 ? "99+" : $"{Math.Floor(months.Value):0}";
                hourglass[level].Visibility = months.HasValue ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------------- 工具 ----------------

        /// <summary>游戏自己的翻译（TEXT_IN_GRANARY，随游戏语言）；取不到时用中文。</summary>
        private static string GameText(int index, string fallback)
        {
            try
            {
                string text = Translate.Instance?.lookUpText(Enums.eTextSections.TEXT_IN_GRANARY, index);
                return string.IsNullOrEmpty(text) ? fallback : text;
            }
            catch
            {
                return fallback;
            }
        }

        private static Path Hourglass()
        {
            return new Path
            {
                Data = Geometry.Parse("M 1,1 L 9,1 M 1,13 L 9,13 M 2,1 C 2,5 5,6 5,7 C 5,8 2,9 2,13 M 8,1 C 8,5 5,6 5,7 C 5,8 8,9 8,13"),
                Stroke = Soft,
                StrokeThickness = 1.3f,
                // 与数字同高，并对齐数字的视觉中心（文字框下方留有字母下伸的空间，框的中心偏低）。
                Stretch = Stretch.Uniform,
                Width = 8,
                Height = 10,
                Margin = new Thickness(3, 0, 0, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>两端淡出的细线（horizontal 为横线）。</summary>
        private static Border Fade(float length, bool horizontal, byte alpha = 0x78)
        {
            // 整数单位的粗细、只在两端很短地淡出：避免细线落在半像素上时粗细不匀、断断续续。
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x00, 0x5E, 0x44, 0x26), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(alpha, 0x5E, 0x44, 0x26), Offset = 0.08f },
                    new GradientStop { Color = Color.FromArgb(alpha, 0x5E, 0x44, 0x26), Offset = 0.92f },
                    new GradientStop { Color = Color.FromArgb(0x00, 0x5E, 0x44, 0x26), Offset = 1 },
                },
            };
            var line = horizontal
                ? new Border { Width = (float)Math.Round(length), Height = 1, Background = brush }
                : new Border { Width = 1, Height = (float)Math.Round(length), Background = brush };
            line.UseLayoutRounding = true;
            return line;
        }

        private static SolidColorBrush Tone(int value) => value > 0 ? Up : value < 0 ? Down : Ink;

        private static TextBlock Text(float size, bool bold, Brush color, string text = "")
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = color,
            };
        }

        private static void Place(Canvas canvas, UIElement e, float x, float y)
        {
            Canvas.SetLeft(e, (float)Math.Round(x));
            Canvas.SetTop(e, (float)Math.Round(y));
            canvas.Children.Add(e);
        }

        private static void PlaceRight(Canvas canvas, TextBlock t, float right, float y)
        {
            t.Width = 50;
            t.TextAlignment = TextAlignment.Right;
            Place(canvas, t, right - 50, y);
        }

        private ImageSource FindImage(string key)
        {
            return (hud.TryFindResource(key) ?? MainViewModel.Instance?.HUDmain?.TryFindResource(key)) as ImageSource;
        }

        private static ImageSource Mask(int value)
        {
            var sprites = MainViewModel.Instance?.GameSprites;
            if (sprites == null || sprites.Count < 3)
                return null;
            return sprites[value < 0 ? 2 : value > 0 ? 0 : 1];
        }

        private static string Signed(int value) => value > 0 ? "+" + value : value < 0 ? "−" + (-value) : "0";

        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
