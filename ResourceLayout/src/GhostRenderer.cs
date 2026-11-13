using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ResourceLayout
{
    /// <summary>
    /// 在地面上画幽灵布局：每个建筑一圈类型颜色的轮廓加两条对角线（先画深色描边再画亮线，沙地草地上都清楚）。
    /// 轮廓顶点逐格贴合地形高度；坐标用游戏格换算，自动适配视角旋转。
    /// </summary>
    internal sealed class GhostRenderer
    {
        private const int SortingOrder = 31011;
        private const int OutlineSortingOrder = 31010;

        private readonly List<LineRenderer> pool = new List<LineRenderer>();
        private readonly List<Vector3> points = new List<Vector3>();
        private GameObject root;
        private Material material;
        private int used;

        public void Begin()
        {
            used = 0;
            EnsureRoot();
        }

        public void End()
        {
            for (int i = used; i < pool.Count; i++)
                if (pool[i] != null)
                    pool[i].enabled = false;
        }

        public void HideAll()
        {
            used = 0;
            End();
        }

        public void Draw(LayoutType type, int x, int y, float alpha, float widthPixels)
        {
            if (GameMap.instance == null || MainControls.instance == null)
                return;

            float width = PixelsToWorld(widthPixels);
            Color color = type.Color;
            color.a = alpha;

            Rectangle(x, y, type.Size, type.Size, color, width);
            Diagonal(x, y, type.Size, color, width * 0.6f);
            if (type.HasQuarryPile)
                Rectangle(x + LayoutType.PileDx, y + LayoutType.PileDy, LayoutType.PileSize, LayoutType.PileSize, color, width * 0.7f);
        }

        // 矩形轮廓：沿四条边逐格取点，贴合地形。
        private void Rectangle(int x, int y, int w, int h, Color color, float width)
        {
            points.Clear();
            for (int i = 0; i <= w; i++) points.Add(Corner(x + i, y));
            for (int j = 1; j <= h; j++) points.Add(Corner(x + w, y + j));
            for (int i = w - 1; i >= 0; i--) points.Add(Corner(x + i, y + h));
            for (int j = h - 1; j >= 0; j--) points.Add(Corner(x, y + j));
            Emit(color, width);
        }

        private void Diagonal(int x, int y, int s, Color color, float width)
        {
            points.Clear();
            for (int i = 0; i <= s; i++) points.Add(Corner(x + i, y + i));
            Emit(color, width);
            points.Clear();
            for (int i = 0; i <= s; i++) points.Add(Corner(x + s - i, y + i));
            Emit(color, width);
        }

        // 格角 (cx, cy) = 游戏格 (cx, cy) 的左上角；高度取周围四格平均。
        private static Vector3 Corner(int cx, int cy)
        {
            Vector3 c = GameGrid.World(cx, cy, out _);
            GameGrid.Axes(cx, cy, out Vector3 ax, out Vector3 ay);
            float h = 0f;
            int n = 0;
            for (int dx = -1; dx <= 0; dx++)
            for (int dy = -1; dy <= 0; dy++)
            {
                GameGrid.World(cx + dx, cy + dy, out bool ok);
                if (!ok) continue;
                h += TileHeight(cx + dx, cy + dy);
                n++;
            }
            Vector3 flat = c;
            flat.y -= TileHeight(cx, cy);
            Vector3 p = flat - 0.5f * ax - 0.5f * ay;
            p.y += n > 0 ? h / n : 0f;
            p.z = c.z;
            return p;
        }

        private static float TileHeight(int gx, int gy)
        {
            GameMap map = GameMap.instance;
            map.mapGameTileToTilemapCoord(gx, gy, out int tx, out int ty);
            GameMapTile tile = map.getMapTile(tx, ty);
            return tile?.height ?? 0f;
        }

        private void Emit(Color color, float width)
        {
            Vector3[] arr = points.ToArray();
            // z 取最前的点，避免被地形精灵挡住。
            float z = float.MaxValue;
            foreach (Vector3 p in arr) z = Math.Min(z, p.z);
            for (int i = 0; i < arr.Length; i++) arr[i].z = z;

            LineRenderer outline = GetLine(used++, OutlineSortingOrder);
            outline.positionCount = arr.Length;
            outline.SetPositions(arr);
            outline.startWidth = outline.endWidth = width * 2.2f;
            Color dark = new Color(0f, 0f, 0f, 0.6f * color.a);
            outline.startColor = outline.endColor = dark;
            outline.enabled = true;

            LineRenderer line = GetLine(used++, SortingOrder);
            line.positionCount = arr.Length;
            line.SetPositions(arr);
            line.startWidth = line.endWidth = width;
            line.startColor = line.endColor = color;
            line.enabled = true;
        }

        private static float PixelsToWorld(float pixels)
        {
            Camera camera = Camera.main;
            if (camera == null || !camera.orthographic || Screen.height <= 0)
                return 0.03f;
            return pixels * 2f * camera.orthographicSize / Screen.height;
        }

        private void EnsureRoot()
        {
            if (root != null && pool.TrueForAll(l => l != null))
                return;
            if (root != null)
                Object.Destroy(root);
            pool.Clear();
            root = new GameObject("ResourceLayout_Ghosts");
            Object.DontDestroyOnLoad(root);
        }

        private LineRenderer GetLine(int index, int sortingOrder)
        {
            while (pool.Count <= index)
            {
                var go = new GameObject("ResourceLayoutLine_" + pool.Count);
                go.transform.SetParent(root.transform, false);
                LineRenderer l = go.AddComponent<LineRenderer>();
                l.useWorldSpace = true;
                l.loop = false;
                l.alignment = LineAlignment.View;
                l.numCornerVertices = 2;
                l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                l.receiveShadows = false;
                l.sharedMaterial = GetMaterial();
                SortingLayer[] layers = SortingLayer.layers;
                l.sortingLayerID = layers.Length > 0 ? layers[layers.Length - 1].id : 0;
                l.enabled = false;
                pool.Add(l);
            }
            LineRenderer result = pool[index];
            result.sortingOrder = sortingOrder;
            return result;
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
                throw new InvalidOperationException("No usable unlit shader found for the layout ghosts.");
            material = new Material(shader) { name = "ResourceLayout_Line" };
            Object.DontDestroyOnLoad(material);
            return material;
        }
    }
}
