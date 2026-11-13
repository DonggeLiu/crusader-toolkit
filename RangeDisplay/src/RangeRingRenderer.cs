using SHCDESE.API;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RangeDisplay
{
    /// <summary>
    /// 用 LineRenderer 在地面上画虚线射程圈、点线军医范围。
    /// 原生判定为 dx²+dy² ≤ R²（dx、dy 为整格差），因此射程圈在“格坐标”里是正圆；
    /// 这里把格坐标经游戏自己的 Tilemap 映射到 Unity 坐标，自动适配等距视角与四个旋转方向。
    /// </summary>
    internal sealed class RangeRingRenderer
    {
        // 测量椭圆周长时的采样数；实际折线段数按虚线周期另算。
        private const int PerimeterSamples = 64;
        private const int MinSegments = 16;
        private const int MaxSegments = 4000;
        private const int SortingOrder = 32000;
        private const float DottedWidthScale = 1.8f;
        private const float DottedAlphaBoost = 0.3f;
        private const float PreviewWidthScale = 2.2f;
        private const float PreviewAlphaBoost = 0.35f;

        private readonly List<LineRenderer> pool = new List<LineRenderer>();
        private readonly RingIconRenderer icons = new RingIconRenderer();
        private Vector3[] points = new Vector3[256];
        private GameObject root;
        private Material dashMaterial;
        private Material dotMaterial;
        private Material longDashMaterial;
        private bool loggedPlacementFailure;

        /// <param name="dashPixels">虚线一“划”加一“空”的屏幕长度（像素）。</param>
        /// <param name="dotPixels">点线一“点”加一“空”的屏幕长度（像素）。</param>
        /// <returns>实际画出的圈数。</returns>
        public int Draw(List<RangeRing> rings, float widthPixels, float dashPixels, float dotPixels)
        {
            GameMap map = GameMap.instance;
            if (map == null)
            {
                HideAll();
                return 0;
            }

            EnsureRoot();
            float width = PixelsToWorld(widthPixels);
            float dashPeriod = PixelsToWorld(Mathf.Max(2f, dashPixels));
            float dotPeriod = PixelsToWorld(Mathf.Max(3f, dotPixels));
            bool flattened = EngineInterface.FlattenedLandscape;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;

            int used = 0;
            icons.Begin();
            foreach (RangeRing ring in rings)
            {
                bool dotted = ring.Style == RingStyle.Dotted;
                bool longDash = ring.Style == RingStyle.LongDash;
                float period = dotted ? dotPeriod : longDash ? dashPeriod * 3f : dashPeriod;
                int count = ring.IsFootprint
                    ? BuildFootprint(map, tiles, flattened, ring, period)
                    : BuildRing(map, tiles, flattened, ring, period);
                if (count == 0)
                    continue;

                // 点线只有三分之一有颜色，同样粗细和透明度下比虚线淡得多，所以加粗、调高不透明度。
                Color color = ring.Color;
                float lineWidth = width;
                if (dotted)
                {
                    color.a = Mathf.Min(1f, color.a + DottedAlphaBoost);
                    lineWidth *= DottedWidthScale;
                }
                else if (ring.IsFootprint && ring.Radius > 0f)
                {
                    // 放置塔楼时的预览圈只在比对位置时出现，画得更醒目。
                    color.a = Mathf.Min(1f, color.a + PreviewAlphaBoost);
                    lineWidth *= PreviewWidthScale;
                }

                LineRenderer line = GetLine(used++);
                line.sharedMaterial = dotted ? GetDotMaterial() : longDash ? GetLongDashMaterial() : GetDashMaterial();
                line.startColor = color;
                line.endColor = color;
                line.startWidth = lineWidth;
                line.endWidth = lineWidth;
                line.positionCount = count;
                line.SetPositions(points);
                line.enabled = true;

                if (ring.Icon != RingIcon.None)
                    icons.Place(ring.Icon, points, count, PixelsToWorld(1f), line.sharedMaterial);
            }

            icons.End();

            for (int i = used; i < pool.Count; i++)
                pool[i].enabled = false;

            return used;
        }

        public void HideAll()
        {
            icons.HideAll();
            foreach (LineRenderer line in pool)
            {
                if (line != null)
                    line.enabled = false;
            }
        }

        /// <returns>写入 points 的点数；无法放置时为 0。</returns>
        private int BuildRing(GameMap map, GameTileManagerAPI tiles, bool flattened, RangeRing ring, float period)
        {
            int cx = ring.TileX;
            int cy = ring.TileY;
            if (!TryGetFrame(map, cx, cy, out Vector3 origin, out Vector3 axisX, out Vector3 axisY, out float z))
                return 0;

            // 半径由调用方给出：射程圈画在被覆盖格子的外沿（R + 0.5），军医圈画在正好 R 格处。
            float radius = ring.Radius;

            // 每段折线贴一次“实-空”纹理，所以段数 = 周长 / 周期，缩放时虚线长度不变。
            int count = Mathf.Clamp(Mathf.RoundToInt(Perimeter(axisX, axisY, radius) / period), MinSegments, MaxSegments);
            EnsurePoints(count);

            for (int i = 0; i < count; i++)
            {
                double angle = 2.0 * Math.PI * i / count;
                float dx = (float)(radius * Math.Cos(angle));
                float dy = (float)(radius * Math.Sin(angle));

                Vector3 p = origin + axisX * dx + axisY * dy;
                int gx = cx + Mathf.RoundToInt(dx);
                int gy = cy + Mathf.RoundToInt(dy);
                p.y += GroundHeight(map, tiles, flattened, gx, gy);
                p.z = z;
                points[i] = p;
            }

            return count;
        }

        /// <summary>
        /// 半径 0：沿建筑占地外框（格边缘，即 min − 0.5 到 max + 0.5）画在塔顶。
        /// 半径 > 0：占地内每格为圆心的圆的外包络，即四角各一段四分之一圆弧连成的圆角矩形，画在地面。
        /// </summary>
        private int BuildFootprint(GameMap map, GameTileManagerAPI tiles, bool flattened, RangeRing ring, float period)
        {
            int cx = ring.TileX;
            int cy = ring.TileY;
            if (!TryGetFrame(map, cx, cy, out Vector3 origin, out Vector3 axisX, out Vector3 axisY, out float z))
                return 0;

            float w = ring.MaxX - cx + 1;
            float h = ring.MaxY - cy + 1;
            float top = 0f;
            path.Clear();

            if (ring.Radius <= 0f)
            {
                // 画在塔顶：GameMapTile.buildingHeight 与地形高度同一单位（原生值 / 32），游戏判断鼠标位置时也是两者相加。
                for (int x = cx; x <= ring.MaxX; x++)
                {
                    for (int y = cy; y <= ring.MaxY; y++)
                    {
                        map.mapGameTileToTilemapCoord(x, y, out int mapX, out int mapY);
                        GameMapTile tile = map.getMapTile(mapX, mapY);
                        if (tile != null && tile.buildingHeight > top)
                            top = tile.buildingHeight;
                    }
                }

                path.Add(new Vector2(-0.5f, -0.5f));
                path.Add(new Vector2(w - 0.5f, -0.5f));
                path.Add(new Vector2(w - 0.5f, h - 0.5f));
                path.Add(new Vector2(-0.5f, h - 0.5f));
            }
            else
            {
                AddArc(new Vector2(w - 1, 0), ring.Radius, -90f);
                AddArc(new Vector2(w - 1, h - 1), ring.Radius, 0f);
                AddArc(new Vector2(0, h - 1), ring.Radius, 90f);
                AddArc(new Vector2(0, 0), ring.Radius, 180f);
            }

            // 按屏幕长度均匀重采样，每段贴一次纹理，虚线长度才一致。
            float total = 0f;
            for (int i = 0; i < path.Count; i++)
                total += ScreenLength(axisX, axisY, path[i], path[(i + 1) % path.Count]);

            int count = Mathf.Clamp(Mathf.RoundToInt(total / period), 4, MaxSegments);
            EnsurePoints(count);
            float step = total / count;
            int segment = 0;
            float segmentStart = 0f;
            float segmentLength = ScreenLength(axisX, axisY, path[0], path[1 % path.Count]);
            for (int i = 0; i < count; i++)
            {
                float target = i * step;
                while (segmentStart + segmentLength < target && segment < path.Count - 1)
                {
                    segmentStart += segmentLength;
                    segment++;
                    segmentLength = ScreenLength(axisX, axisY, path[segment], path[(segment + 1) % path.Count]);
                }

                float u = segmentLength > 0f ? (target - segmentStart) / segmentLength : 0f;
                Vector2 t = Vector2.Lerp(path[segment], path[(segment + 1) % path.Count], u);
                Vector3 p = origin + axisX * t.x + axisY * t.y;
                p.y += GroundHeight(map, tiles, flattened, cx + Mathf.RoundToInt(t.x), cy + Mathf.RoundToInt(t.y)) + top;
                p.z = z;
                points[i] = p;
            }

            return count;
        }

        private readonly List<Vector2> path = new List<Vector2>();

        // 以 center 为圆心、从 startDegrees 起逆时针的四分之一圆弧（格坐标）。
        private void AddArc(Vector2 center, float radius, float startDegrees)
        {
            const int samples = 16;
            for (int i = 0; i <= samples; i++)
            {
                double angle = (startDegrees + 90.0 * i / samples) * Math.PI / 180.0;
                path.Add(center + new Vector2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle))));
            }
        }

        private static float ScreenLength(Vector3 axisX, Vector3 axisY, Vector2 a, Vector2 b)
        {
            Vector2 d = axisX * (b.x - a.x) + axisY * (b.y - a.y);
            return d.magnitude;
        }

        /// <summary>某格中心及格坐标两个方向在 Unity 坐标里的向量；无法放置时返回 false。</summary>
        private bool TryGetFrame(GameMap map, int cx, int cy, out Vector3 origin, out Vector3 axisX, out Vector3 axisY, out float z)
        {
            origin = axisX = axisY = Vector3.zero;
            z = 0f;

            map.mapGameTileToTilemapCoord(cx, cy, out int mapX, out int mapY);
            if (map.getMapTile(mapX, mapY) == null || MainControls.instance == null)
            {
                if (!loggedPlacementFailure)
                {
                    loggedPlacementFailure = true;
                    RangeDisplayPlugin.Log?.LogWarning(
                        $"Cannot place ring at tile ({cx},{cy}) -> tilemap ({mapX},{mapY}), " +
                        $"tilemapSize={GameMap.tilemapSize}, MainControls={(MainControls.instance != null)}");
                }
                return false;
            }

            // GameMapTile.tilemapRef 游戏并不赋值，改用游戏放置精灵时用的 MainControls 网格。
            MainControls controls = MainControls.instance;
            origin = CellCenter(map, controls, cx, cy);
            axisX = CellCenter(map, controls, cx + 1, cy) - origin;
            axisY = CellCenter(map, controls, cx, cy + 1) - origin;

            // 深度沿用游戏精灵在该格的 z 值，保证处于相机裁剪范围内。
            z = map.getSpritePosVector(mapX, mapY).z;
            return true;
        }

        private void EnsurePoints(int count)
        {
            if (points.Length < count)
                Array.Resize(ref points, Mathf.NextPowerOfTwo(count));
        }

        // 格坐标里的圆在屏幕上是椭圆（等距视角），用折线近似周长，不计地形起伏。
        private static float Perimeter(Vector3 axisX, Vector3 axisY, float radius)
        {
            float length = 0f;
            Vector2 previous = axisX * radius;
            for (int i = 1; i <= PerimeterSamples; i++)
            {
                double angle = 2.0 * Math.PI * i / PerimeterSamples;
                Vector2 current = axisX * (float)(radius * Math.Cos(angle)) + axisY * (float)(radius * Math.Sin(angle));
                length += Vector2.Distance(previous, current);
                previous = current;
            }

            return length;
        }

        private static Vector3 CellCenter(GameMap map, MainControls controls, int gameX, int gameY)
        {
            map.mapGameTileToTilemapCoord(gameX, gameY, out int x, out int y);
            return controls.getCellCentre(x, y);
        }

        // 与 CastlePlanner 已验证的做法一致：普通视图用原生地形默认高度 / 32，
        // 平面视图用 GameMapTile.height。只取地形高度，不含城墙和塔楼。
        internal static float GroundHeight(GameMap map, GameTileManagerAPI tiles, bool flattened, int gameX, int gameY)
        {
            if (flattened)
            {
                map.mapGameTileToTilemapCoord(gameX, gameY, out int x, out int y);
                GameMapTile tile = map.getMapTile(x, y);
                return tile != null ? tile.height : 0f;
            }

            if (tiles == null || !tiles.IsTileInsideMapBounds(gameX, gameY))
                return 0f;

            int tileId = tiles.GetTileId(gameX, gameY);
            if (!tiles.IsValidTileId(tileId))
                return 0f;

            return tiles.GetTileDefaultHeight(tileId) / 32f;
        }

        private static float PixelsToWorld(float pixels)
        {
            Camera camera = Camera.main;
            if (camera == null || !camera.orthographic || Screen.height <= 0)
                return 0.05f;

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
            root = new GameObject("RangeDisplay_Rings");
            Object.DontDestroyOnLoad(root);
            RangeDisplayPlugin.Log?.LogInfo("Created range ring renderer objects.");
        }

        // 画在最上层的排序层，避免被地形或建筑遮住。
        private static int TopSortingLayerId()
        {
            SortingLayer[] layers = SortingLayer.layers;
            return layers.Length > 0 ? layers[layers.Length - 1].id : 0;
        }

        private LineRenderer GetLine(int index)
        {
            while (pool.Count <= index)
            {
                var go = new GameObject("RangeRing_" + pool.Count);
                go.transform.SetParent(root.transform, false);
                LineRenderer line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.loop = true;
                line.alignment = LineAlignment.View;
                line.numCornerVertices = 0;
                line.textureMode = LineTextureMode.RepeatPerSegment;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.sortingLayerID = TopSortingLayerId();
                line.sortingOrder = SortingOrder;
                line.enabled = false;
                pool.Add(line);
            }

            return pool[index];
        }

        private Material GetDashMaterial() =>
            dashMaterial != null ? dashMaterial : dashMaterial = CreateMaterial("RangeDisplay_Dash", 2);

        private Material GetDotMaterial() =>
            dotMaterial != null ? dotMaterial : dotMaterial = CreateMaterial("RangeDisplay_Dot", 3);

        private Material GetLongDashMaterial() =>
            longDashMaterial != null ? longDashMaterial : longDashMaterial = CreateMaterial("RangeDisplay_LongDash", 4, 3);

        /// <summary>
        /// 每段折线贴一次纹理：前 opaque 个像素不透明、其余透明。
        /// 2 像素 1 实 = 一半实一半空（虚线）；3 像素 1 实 = 点线；4 像素 3 实 = 长虚线。
        /// </summary>
        private static Material CreateMaterial(string name, int texels, int opaque = 1)
        {
            Shader shader =
                Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                throw new InvalidOperationException("No usable unlit shader found for range rings.");

            var texture = new Texture2D(texels, 1, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            var pixels = new Color32[texels];
            for (int i = 0; i < texels; i++)
                pixels[i] = new Color32(255, 255, 255, (byte)(i < opaque ? 255 : 0));
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Object.DontDestroyOnLoad(texture);

            var material = new Material(shader) { name = name, mainTexture = texture };
            Object.DontDestroyOnLoad(material);
            RangeDisplayPlugin.Log?.LogInfo($"Range ring material {name}: shader {shader.name}");
            return material;
        }
    }
}
