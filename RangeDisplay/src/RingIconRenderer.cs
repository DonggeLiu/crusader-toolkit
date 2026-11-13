using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RangeDisplay
{
    /// <summary>
    /// 放置塔楼时，在每个预览圈的可见部分贴一个半透明图标（弓箭手、散射机、弩炮），说明这个圈是谁的射程。
    /// 图标直接取游戏界面用的图集 UI-MasterAtlas，裁切位置与游戏自己的 XAML 资源（globalgamemanagers 里的
    /// CroppedBitmap）一致，所以和建造菜单、兵营按钮上的图一样，也不需要翻译。
    /// </summary>
    internal sealed class RingIconRenderer
    {
        private const string AtlasName = "UI-MasterAtlas";
        // 图标高度占屏幕高度的比例，不同分辨率下看起来一样大。
        private const float HeightFraction = 0.08f;
        private const float Opacity = 0.6f;
        private const int SortingOrder = 32001;

        // 屏幕上可放图标的区域（比例）：避开四边和底部的建造栏、小地图。
        private const float MarginX = 0.04f;
        private const float MarginTop = 0.06f;
        private const float MarginBottom = 0.22f;

        // Noesis SourceRect（左上角为原点）：UI-Buildings O001 兵营弓箭手、M003 散射机、M005 弩炮。
        private static readonly Dictionary<RingIcon, Rect> SourceRects = new Dictionary<RingIcon, Rect>
        {
            { RingIcon.Archer, new Rect(6148, 1915, 91, 170) },
            { RingIcon.Mangonel, new Rect(6647, 2834, 158, 130) },
            { RingIcon.Ballista, new Rect(4336, 5378, 145, 158) },
        };

        private readonly Dictionary<RingIcon, Sprite> sprites = new Dictionary<RingIcon, Sprite>();
        private readonly List<SpriteRenderer> pool = new List<SpriteRenderer>();
        private Texture2D atlas;
        private float nextAtlasSearch;
        private bool loggedMissingAtlas;
        private Material material;
        private GameObject root;
        private int used;
        private bool[] visible = new bool[256];

        public void Begin() => used = 0;

        /// <summary>把图标放在 points[0..count) 在屏幕安全区内最长一段可见圆弧的中点；圈在屏幕上看不到时不放。</summary>
        public void Place(RingIcon icon, Vector3[] points, int count, float worldPerPixel, Material lineMaterial)
        {
            Camera camera = Camera.main;
            if (icon == RingIcon.None || camera == null || count == 0)
                return;

            Sprite sprite = GetSprite(icon);
            if (sprite == null)
                return;

            // 找出圈上落在安全区里的最长一段连续圆弧（首尾相接），图标放在这段弧的中点。
            // 圈上的点按屏幕长度均匀取样，所以按序号取中点就是弧长的中点。
            float w = Screen.width, h = Screen.height;
            if (visible.Length < count)
                visible = new bool[Mathf.NextPowerOfTwo(count)];
            int firstHidden = -1;
            for (int i = 0; i < count; i++)
            {
                Vector3 s = camera.WorldToScreenPoint(points[i]);
                visible[i] = s.x >= w * MarginX && s.x <= w * (1f - MarginX) && s.y >= h * MarginBottom && s.y <= h * (1f - MarginTop);
                if (!visible[i] && firstHidden < 0)
                    firstHidden = i;
            }

            int best;
            if (firstHidden < 0)
            {
                best = 0;   // 整圈都在屏幕上：取圈上任意一点即可
            }
            else
            {
                int bestStart = -1, bestLength = 0, runStart = -1, runLength = 0;
                for (int k = 1; k <= count; k++)
                {
                    int i = (firstHidden + k) % count;
                    if (visible[i])
                    {
                        if (runLength == 0) runStart = i;
                        runLength++;
                        if (runLength > bestLength) { bestLength = runLength; bestStart = runStart; }
                    }
                    else
                    {
                        runLength = 0;
                    }
                }

                if (bestLength == 0)
                    return;
                best = (bestStart + bestLength / 2) % count;
            }

            SpriteRenderer renderer = GetRenderer(used++, lineMaterial);
            renderer.sprite = sprite;
            renderer.color = new Color(1f, 1f, 1f, Opacity);
            float scale = HeightFraction * Screen.height * worldPerPixel / sprite.bounds.size.y;
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
            renderer.transform.position = points[best];
            renderer.enabled = true;
        }

        public void End()
        {
            for (int i = used; i < pool.Count; i++)
            {
                if (pool[i] != null)
                    pool[i].enabled = false;
            }
        }

        public void HideAll()
        {
            used = 0;
            End();
        }

        private Sprite GetSprite(RingIcon icon)
        {
            if (sprites.TryGetValue(icon, out Sprite cached) && cached != null)
                return cached;

            Texture2D texture = FindAtlas();
            if (texture == null || !SourceRects.TryGetValue(icon, out Rect source))
                return null;

            // Unity 纹理坐标以左下角为原点。
            var rect = new Rect(source.x, texture.height - source.y - source.height, source.width, source.height);
            Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "RangeDisplay_" + icon;
            Object.DontDestroyOnLoad(sprite);
            sprites[icon] = sprite;
            return sprite;
        }

        private Texture2D FindAtlas()
        {
            if (atlas != null)
                return atlas;
            if (Time.unscaledTime < nextAtlasSearch)
                return null;

            nextAtlasSearch = Time.unscaledTime + 5f;
            foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (texture != null && texture.name == AtlasName)
                {
                    atlas = texture;
                    RangeDisplayPlugin.Log?.LogInfo($"Using {AtlasName} ({texture.width}x{texture.height}) for tower preview icons.");
                    return atlas;
                }
            }

            if (!loggedMissingAtlas)
            {
                loggedMissingAtlas = true;
                RangeDisplayPlugin.Log?.LogWarning($"{AtlasName} not found; tower preview icons are skipped.");
            }
            return null;
        }

        private SpriteRenderer GetRenderer(int index, Material lineMaterial)
        {
            if (root == null)
            {
                pool.Clear();
                root = new GameObject("RangeDisplay_RingIcons");
                Object.DontDestroyOnLoad(root);
            }

            if (material == null && lineMaterial != null)
            {
                material = new Material(lineMaterial.shader) { name = "RangeDisplay_Icon" };
                Object.DontDestroyOnLoad(material);
            }

            while (pool.Count <= index)
            {
                var go = new GameObject("RingIcon_" + pool.Count);
                go.transform.SetParent(root.transform, false);
                SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
                if (material != null)
                    renderer.sharedMaterial = material;
                SortingLayer[] layers = SortingLayer.layers;
                renderer.sortingLayerID = layers.Length > 0 ? layers[layers.Length - 1].id : 0;
                renderer.sortingOrder = SortingOrder;
                renderer.enabled = false;
                pool.Add(renderer);
            }

            return pool[index];
        }
    }
}
