using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BuildGrid
{
    /// <summary>
    /// 在鼠标所在格周围画局部网格：每条格线一条 LineRenderer，
    /// 顶点落在格角并贴合游戏当前渲染的地形高度；离中心越远越淡。
    /// 坐标一律用渲染网格（tilemap）坐标，与游戏放置精灵用的网格一致，自动适配四个旋转方向。
    /// </summary>
    internal sealed class GridRenderer
    {
        private const int SortingOrder = 31001;
        private const int OutlineSortingOrder = 31000;
        private const float OutlineWidthFactor = 2.4f;
        private const float OutlineAlpha = 0.7f;

        private readonly List<LineRenderer> pool = new List<LineRenderer>();
        private Vector3[] points = new Vector3[64];
        private GameObject root;
        private Material material;

        public void HideAll()
        {
            foreach (LineRenderer line in pool)
            {
                if (line != null)
                    line.enabled = false;
            }
        }

        /// <param name="centerX">鼠标所在格（渲染网格坐标）。</param>
        /// <returns>是否画出了网格。</returns>
        public bool Draw(GameMap map, int centerX, int centerY, int radius, Color color, float widthPixels)
        {
            MainControls controls = MainControls.instance;
            if (map == null || controls == null || map.getMapTile(centerX, centerY) == null)
            {
                HideAll();
                return false;
            }

            EnsureRoot();
            Vector3 origin = controls.getCellCentre(centerX, centerY);
            Vector3 axisX = controls.getCellCentre(centerX + 1, centerY) - origin;
            Vector3 axisY = controls.getCellCentre(centerX, centerY + 1) - origin;
            float z = map.getSpritePosVector(centerX, centerY).z;
            float width = PixelsToWorld(widthPixels);

            // 半径从建筑占地边缘算起：先量出预览占地离鼠标格最远有几格。
            footprintHalf = MeasureFootprint(map, centerX, centerY);
            fadeRadius = radius;
            radius += footprintHalf;

            int vertexCount = 2 * radius + 2;
            if (points.Length < vertexCount)
                points = new Vector3[vertexCount];

            CollectOccluders(map, controls, centerX, centerY, radius);

            int used = 0;
            // 格线在相邻两格中心之间：偏移 k - 0.5，k 从 -radius 到 radius + 1。
            for (int k = -radius; k <= radius + 1; k++)
            {
                float lineAlpha = FadeAt(k - 0.5f);
                used = DrawLine(map, centerX, centerY, radius, origin, axisX, axisY, z, k, true, color, lineAlpha, width, used);
                used = DrawLine(map, centerX, centerY, radius, origin, axisX, axisY, z, k, false, color, lineAlpha, width, used);
            }

            for (int i = used; i < pool.Count; i++)
                pool[i].enabled = false;
            return true;
        }

        // vertical=true：固定 x 方向偏移、沿 y 方向延伸的格线；false 反之。
        // 格线按段拆开：两侧都被建筑预览覆盖的段不画（不压在建筑上），
        // 预览边缘的段保留，正好勾出占地轮廓。连续可见的段合成一条折线。
        private int DrawLine(GameMap map, int cx, int cy, int radius, Vector3 origin, Vector3 axisX, Vector3 axisY,
            float z, int k, bool vertical, Color color, float lineAlpha, float width, int used)
        {
            int segments = 2 * radius + 1;
            int runStart = -1;
            for (int s = 0; s <= segments; s++)
            {
                bool visible = s < segments && SegmentVisible(map, cx, cy, radius, k, s, vertical) &&
                               !Occluded(SegmentMidpoint(map, cx, cy, radius, origin, axisX, axisY, k, s, vertical));
                if (visible && runStart < 0)
                    runStart = s;
                if (!visible && runStart >= 0)
                {
                    used = DrawRun(map, cx, cy, radius, origin, axisX, axisY, z, k, vertical, runStart, s, color, lineAlpha, width, used);
                    runStart = -1;
                }
            }

            return used;
        }

        // 预览建筑各格的精灵在世界平面上的矩形。高的墙、塔精灵会向上盖住后面的地面格，
        // 那里的格线也要隐藏，否则会画在建筑身上。
        private readonly List<Rect> occluders = new List<Rect>();

        private void CollectOccluders(GameMap map, MainControls controls, int cx, int cy, int radius)
        {
            occluders.Clear();
            int r = radius + 1;
            for (int x = cx - r; x <= cx + r; x++)
            {
                for (int y = cy - r; y <= cy + r; y++)
                {
                    GameMapTile tile = map.getMapTile(x, y);
                    if (tile == null || tile.constructionOrigImage == null || tile.tileImage == null)
                        continue;

                    Bounds b = tile.tileImage.bounds;
                    Vector3 c = controls.getCellCentre(x, y);
                    c.y += tile.height;
                    occluders.Add(Rect.MinMaxRect(c.x + b.min.x, c.y + b.min.y, c.x + b.max.x, c.y + b.max.y));
                }
            }
        }

        private bool Occluded(Vector2 point)
        {
            foreach (Rect rect in occluders)
            {
                if (rect.Contains(point))
                    return true;
            }

            return false;
        }

        private static Vector2 SegmentMidpoint(GameMap map, int cx, int cy, int radius, Vector3 origin, Vector3 axisX,
            Vector3 axisY, int k, int s, bool vertical)
        {
            float a = k - 0.5f;
            float b = s - radius;
            float dx = vertical ? a : b;
            float dy = vertical ? b : a;
            Vector3 p = origin + axisX * dx + axisY * dy;
            p.y += CornerHeight(map, cx + Mathf.FloorToInt(dx), cy + Mathf.FloorToInt(dy));
            return p;
        }

        // 段 s 位于第 (s - radius) 行/列，两侧的格为 k-1 与 k。
        private static bool SegmentVisible(GameMap map, int cx, int cy, int radius, int k, int s, bool vertical)
        {
            int along = s - radius;
            bool sideA = vertical ? IsPreview(map, cx + k - 1, cy + along) : IsPreview(map, cx + along, cy + k - 1);
            bool sideB = vertical ? IsPreview(map, cx + k, cy + along) : IsPreview(map, cx + along, cy + k);
            return !(sideA && sideB);
        }

        // 放置预览覆盖的格：游戏给这些格设置了 constructionOrigImage，普通地面没有。
        private static bool IsPreview(GameMap map, int x, int y)
        {
            GameMapTile tile = map.getMapTile(x, y);
            return tile != null && tile.constructionOrigImage != null;
        }

        // 画一段连续可见的格线：顶点 from..to（含），共 to - from + 1 个。
        private int DrawRun(GameMap map, int cx, int cy, int radius, Vector3 origin, Vector3 axisX, Vector3 axisY,
            float z, int k, bool vertical, int from, int to, Color color, float lineAlpha, float width, int used)
        {
            int count = to - from + 1;
            if (points.Length < count)
                points = new Vector3[count * 2];

            for (int i = 0; i < count; i++)
            {
                float a = k - 0.5f;
                float b = from + i - radius - 0.5f;
                float dx = vertical ? a : b;
                float dy = vertical ? b : a;

                Vector3 p = origin + axisX * dx + axisY * dy;
                p.y += CornerHeight(map, cx + Mathf.FloorToInt(dx), cy + Mathf.FloorToInt(dy));
                p.z = z;
                points[i] = p;
            }

            // 沿线方向的淡出按这段两端离中心的偏移计算，拆段后整体仍是中间亮、两端淡。
            float t0 = from - radius - 0.5f;
            float t1 = to - radius - 0.5f;

            // 先画一条更宽的深色描边，再画亮线，在浅色沙地和草地上都清楚。
            LineRenderer outline = GetLine(used, OutlineSortingOrder);
            outline.positionCount = count;
            outline.SetPositions(points);
            outline.startWidth = width * OutlineWidthFactor;
            outline.endWidth = width * OutlineWidthFactor;
            outline.colorGradient = FadeGradient(new Color(0f, 0f, 0f, OutlineAlpha), lineAlpha, t0, t1);
            outline.enabled = true;

            LineRenderer line = GetLine(used + 1, SortingOrder);
            line.positionCount = count;
            line.SetPositions(points);
            line.startWidth = width;
            line.endWidth = width;
            line.colorGradient = FadeGradient(color, lineAlpha, t0, t1);
            line.enabled = true;
            return used + 2;
        }

        // 格角由周围四格共享，取它们渲染高度的平均值，网格在坡上连续。
        private static float CornerHeight(GameMap map, int x, int y)
        {
            float sum = 0f;
            int n = 0;
            for (int ox = 0; ox <= 1; ox++)
            {
                for (int oy = 0; oy <= 1; oy++)
                {
                    GameMapTile tile = map.getMapTile(x + ox, y + oy);
                    if (tile == null)
                        continue;
                    sum += tile.height;
                    n++;
                }
            }

            return n > 0 ? sum / n : 0f;
        }

        // 预览占地的半宽（格）：扫描鼠标格周围，取被预览覆盖的格离中心的最大偏移。
        private const int FootprintScan = 12;
        private int footprintHalf;
        private int fadeRadius = 8;

        private static int MeasureFootprint(GameMap map, int cx, int cy)
        {
            int half = 0;
            for (int x = cx - FootprintScan; x <= cx + FootprintScan; x++)
            {
                for (int y = cy - FootprintScan; y <= cy + FootprintScan; y++)
                {
                    if (IsPreview(map, x, y))
                        half = Mathf.Max(half, Mathf.Max(Mathf.Abs(x - cx), Mathf.Abs(y - cy)));
                }
            }

            return half;
        }

        // 离占地边缘 fadeRadius 格内基本保持亮度，接近边缘才淡出（三次曲线）。
        private float FadeAt(float offset)
        {
            float d = Mathf.Max(0f, Mathf.Abs(offset) - footprintHalf - 0.5f) / Mathf.Max(1, fadeRadius);
            return Mathf.Clamp01(1f - d * d * d);
        }

        private Gradient FadeGradient(Color color, float lineAlpha, float from, float to)
        {
            float a = color.a * lineAlpha;
            var alphas = new GradientAlphaKey[5];
            for (int i = 0; i < alphas.Length; i++)
            {
                float u = i / (float)(alphas.Length - 1);
                alphas[i] = new GradientAlphaKey(a * FadeAt(Mathf.Lerp(from, to, u)), u);
            }

            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) }, alphas);
            return gradient;
        }

        private static float PixelsToWorld(float pixels)
        {
            Camera camera = Camera.main;
            if (camera == null || !camera.orthographic || Screen.height <= 0)
                return 0.03f;

            return pixels * 2f * camera.orthographicSize / Screen.height;
        }

        // 游戏可能在场景切换时销毁我们的对象，发现失效就整体重建。
        private void EnsureRoot()
        {
            if (root != null && pool.TrueForAll(line => line != null))
                return;

            if (root != null)
                Object.Destroy(root);

            pool.Clear();
            root = new GameObject("BuildGrid_Lines");
            Object.DontDestroyOnLoad(root);
        }

        private LineRenderer GetLine(int index, int sortingOrder)
        {
            LineRenderer result = GetLine(index);
            result.sortingOrder = sortingOrder;
            return result;
        }

        private LineRenderer GetLine(int index)
        {
            while (pool.Count <= index)
            {
                var go = new GameObject("BuildGridLine_" + pool.Count);
                go.transform.SetParent(root.transform, false);
                LineRenderer line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.loop = false;
                line.alignment = LineAlignment.View;
                line.numCornerVertices = 0;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.sharedMaterial = GetMaterial();
                SortingLayer[] layers = SortingLayer.layers;
                line.sortingLayerID = layers.Length > 0 ? layers[layers.Length - 1].id : 0;
                line.sortingOrder = SortingOrder;
                line.enabled = false;
                pool.Add(line);
            }

            return pool[index];
        }

        private Material GetMaterial()
        {
            if (material != null)
                return material;

            Shader shader =
                Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                throw new InvalidOperationException("No usable unlit shader found for the build grid.");

            material = new Material(shader) { name = "BuildGrid_Line" };
            Object.DontDestroyOnLoad(material);
            return material;
        }
    }
}
