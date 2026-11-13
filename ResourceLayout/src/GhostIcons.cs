using CrusaderDE;
using Noesis;
using System.Collections.Generic;

namespace ResourceLayout
{
    /// <summary>
    /// 在每个幽灵的正中画这种建筑的建造菜单图标（半透明），大小随占地在屏幕上的大小缩放，
    /// 不用记颜色就能认出是哪种建筑。挂在 MainHUD 根 Grid 上的一个不接收鼠标的 Canvas 里。
    /// </summary>
    internal sealed class GhostIcons
    {
        private Canvas canvas;
        private Panel host;
        private readonly List<Image> pool = new List<Image>();
        private readonly List<LayoutType> poolType = new List<LayoutType>();
        private int used;

        public void Begin()
        {
            used = 0;
            EnsureAttached();
        }

        public void End()
        {
            for (int i = used; i < pool.Count; i++)
                pool[i].Visibility = Visibility.Collapsed;
        }

        public void HideAll()
        {
            used = 0;
            End();
        }

        public void Draw(LayoutType type, int x, int y, float opacity)
        {
            if (canvas == null)
                return;
            UnityEngine.Camera camera = UnityEngine.Camera.main;
            if (camera == null)
                return;

            int s = type.Size;
            // 占地两个对角格中心的屏幕位置：中点是图标中心，距离决定大小。
            if (!ToCanvas(camera, x, y + s - 1, out Point a) || !ToCanvas(camera, x + s - 1, y, out Point b) ||
                !ToCanvas(camera, x, y, out Point c) || !ToCanvas(camera, x + s - 1, y + s - 1, out Point d))
                return;
            float cx = (a.X + b.X + c.X + d.X) / 4f, cy = (a.Y + b.Y + c.Y + d.Y) / 4f;
            float width = System.Math.Max(System.Math.Abs(a.X - b.X), System.Math.Abs(c.X - d.X));
            float size = width * 0.45f;
            if (size < 12f)
                return;

            int index = used++;
            Image img = Get(index);
            if (!ReferenceEquals(poolType[index], type))
            {
                poolType[index] = type;
                object src = MainViewModel.Instance?.HUDmain?.TryFindResource(type.Icon);
                img.Source = src as ImageSource;
            }
            img.Width = size;
            img.Height = size;
            img.Opacity = opacity;
            Canvas.SetLeft(img, cx - size / 2f);
            Canvas.SetTop(img, cy - size / 2f);
            img.Visibility = Visibility.Visible;
        }

        private bool ToCanvas(UnityEngine.Camera camera, int gx, int gy, out Point p)
        {
            UnityEngine.Vector3 world = GameGrid.World(gx, gy, out bool ok);
            UnityEngine.Vector3 screen = camera.WorldToScreenPoint(world);
            p = canvas.PointFromScreen(new Point(screen.x, UnityEngine.Screen.height - screen.y));
            return ok;
        }

        private Image Get(int i)
        {
            while (pool.Count <= i)
            {
                // Tag = "Ignore"：原版 FatControler.overNoesisGUI 的命中检测不理会 IsHitTestVisible，只跳过这个标记，
                // 否则鼠标在图标上时地图点击会被当成点在界面上而失效。
                var img = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false, Tag = "Ignore" };
                canvas.Children.Add(img);
                pool.Add(img);
                poolType.Add(null);
            }
            return pool[i];
        }

        private void EnsureAttached()
        {
            HUD_Buildings buildings = MainViewModel.Instance?.HUDBuildingPanel;
            Panel root = (buildings?.Parent as FrameworkElement)?.Parent as Panel;
            if (root == null || ReferenceEquals(root, host))
                return;
            if (canvas != null && host != null)
                host.Children.Remove(canvas);
            pool.Clear();
            poolType.Clear();
            canvas = new Canvas { IsHitTestVisible = false, Tag = "Ignore" };
            Panel.SetZIndex(canvas, 90);
            root.Children.Add(canvas);
            host = root;
        }
    }
}
