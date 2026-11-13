using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HealthBarColors
{
    internal enum Side
    {
        None,
        Self,
        Ally,
        Enemy
    }

    /// <summary>着色模式：Side = 己方绿、盟友蓝、敌方红；Shield = 己方绿，其他玩家用各自的盾牌颜色。</summary>
    public enum ColorMode
    {
        Side,
        Shield
    }

    /// <summary>血条要显示成什么样：颜色，以及是不是领主（领主血条放大并在上方戴皇冠）。</summary>
    internal readonly struct BarStyle : IEquatable<BarStyle>
    {
        public readonly Color32 Color;
        public readonly bool Lord;

        public BarStyle(Color color, bool lord)
        {
            Color = color;
            Lord = lord;
        }

        public bool Equals(BarStyle other) =>
            Color.r == other.Color.r && Color.g == other.Color.g && Color.b == other.Color.b && Lord == other.Lord;

        public override bool Equals(object obj) => obj is BarStyle other && Equals(other);

        public override int GetHashCode() => (Color.r << 17) ^ (Color.g << 9) ^ (Color.b << 1) ^ (Lord ? 1 : 0);
    }

    /// <summary>
    /// 为血条精灵生成并缓存按颜色着色的副本，并记住副本对应的原图以便还原。
    /// </summary>
    internal sealed class HealthBarSprites
    {
        // 每档血量一帧 × 玩家颜色 × 是否领主，超过这个数说明遇到了意料之外的精灵，停止生成。
        private const int MaxVariants = 2048;

        // 饱和度低于此值的像素（边框、阴影、透明区）保持原样。
        private const int MinSaturation = 24;

        // 领主血条放大倍数。
        private const float LordScale = 1.2f;
        // 皇冠戴在血条正上方：高度 = 血条可见高度 × CrownToBar，底边压住血条顶边 CrownOverlap 像素（按原图尺寸）。
        // 放上方而不是左侧：血条宽度不变，密集时不会盖住旁边单位的血条（2026-10-09 对比后选定）。
        private const float CrownToBar = 2.2f;
        private const int MinCrownHeight = 10;
        private const int CrownOverlap = 1;

        // 皇冠图案取自游戏自带的头像徽章（运行时从玩家的游戏目录读取，不随模组分发）：
        // Emblem_MASK_CROWN 是白色剪影，Emblem_Outline_CROWN 是带细节的描边图。读不到时退回下面的像素画。
        private const string CrownMaskFile = "Emblem_MASK_CROWN.png";
        private const string CrownDetailFile = "Emblem_Outline_CROWN.png";
        private static readonly Color CrownTop = new Color32(255, 216, 72, 255);
        private static readonly Color CrownBottom = new Color32(210, 130, 18, 255);
        private static readonly Color CrownOutline = new Color32(45, 28, 5, 255);
        private const float CrownDetailStrength = 0.35f;

        // 备用皇冠像素画（9 × 7，从上到下）：X = 金色，. = 透明；描边自动加。
        private static readonly string[] FallbackCrown =
        {
            "X..X.X..X",
            "XX.XXX.XX",
            "XXXXXXXXX",
            "XXXXXXXXX",
            "XXXXXXXXX",
            "XXXXXXXXX",
            "XXXXXXXXX",
        };

        // 高分辨率皇冠（金色渐变 + 原生细节，未加描边，像素行自下而上），首次需要时生成。
        private static Color[] crownSource;
        private static int crownSourceWidth, crownSourceHeight;
        private static bool crownLoaded;

        private readonly Dictionary<(Sprite, BarStyle), Sprite> variants = new Dictionary<(Sprite, BarStyle), Sprite>();
        private readonly Dictionary<Sprite, (Sprite Original, BarStyle Style)> replacements = new Dictionary<Sprite, (Sprite, BarStyle)>();
        private readonly HashSet<Sprite> rejected = new HashSet<Sprite>();

        public int VariantCount => variants.Count;

        public event Action<string> Warning;

        /// <summary>让渲染器显示 style 对应的血条；style 为 null 时还原原图。</summary>
        public void Apply(SpriteRenderer renderer, BarStyle? style)
        {
            if (renderer == null)
                return;

            Sprite current = renderer.sprite;
            if (current == null)
                return;

            Sprite original = current;
            if (replacements.TryGetValue(current, out var info))
            {
                // 已是当前样式的最新副本则无需处理；颜色配置改过后旧副本会被换掉。
                if (style.HasValue && info.Style.Equals(style.Value) &&
                    variants.TryGetValue((info.Original, style.Value), out Sprite latest) && latest == current)
                    return;
                original = info.Original;
            }

            if (!style.HasValue || original == null)
            {
                if (original != current)
                    renderer.sprite = original;
                return;
            }

            Sprite variant = GetVariant(original, style.Value);
            if (variant != null && variant != current)
                renderer.sprite = variant;
        }

        public void Restore(SpriteRenderer renderer)
        {
            Apply(renderer, null);
        }

        /// <summary>
        /// 颜色配置改变后丢弃缓存，渲染器在下一次更新时换成新颜色。
        /// 旧副本可能仍在显示，所以不销毁，只保留“副本→原图”的记录以便还原。
        /// </summary>
        public void Clear()
        {
            variants.Clear();
            rejected.Clear();
        }

        private Sprite GetVariant(Sprite original, BarStyle style)
        {
            if (variants.TryGetValue((original, style), out Sprite variant) && variant != null)
                return variant;

            if (rejected.Contains(original) || variants.Count >= MaxVariants)
                return null;

            try
            {
                variant = CreateVariant(original, style);
            }
            catch (Exception ex)
            {
                rejected.Add(original);
                Warning?.Invoke($"Health bar sprite '{original.name}' left unchanged: {ex.Message}");
                return null;
            }

            variants[(original, style)] = variant;
            replacements[variant] = (original, style);
            return variant;
        }

        private static Sprite CreateVariant(Sprite source, BarStyle style)
        {
            if (source.packed && source.packingRotation != SpritePackingRotation.None)
                throw new NotSupportedException("rotated atlas sprite");

            Rect rect = source.rect;
            int width = Mathf.RoundToInt(rect.width);
            int height = Mathf.RoundToInt(rect.height);
            if (width <= 0 || height <= 0 || width > 1024 || height > 256)
                throw new NotSupportedException($"unexpected size {width}x{height}");

            Color32[] pixels = ReadPixels(source, width, height);
            Recolor(pixels, style.Color);

            Vector2 pivot = source.pivot;
            float pixelsPerUnit = source.pixelsPerUnit;
            if (style.Lord)
            {
                // 血条可见部分的范围；皇冠居中戴在它上面，画布向上加高，中心点不变，所以血条位置不变。
                int left = width, right = -1, top = -1, bottom = height;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (pixels[y * width + x].a > 0)
                        {
                            if (x < left) left = x;
                            if (x > right) right = x;
                            if (y > top) top = y;
                            if (y < bottom) bottom = y;
                        }
                if (right < 0) { left = 0; right = width - 1; top = height - 1; bottom = 0; }

                int crownHeight = Math.Max(MinCrownHeight, Mathf.RoundToInt((top - bottom + 1) * CrownToBar));
                Color32[] crown = BuildCrown(crownHeight, out int crownWidth);
                int crownX = (left + right + 1 - crownWidth) / 2;
                int crownY = top + 1 - CrownOverlap;
                int padLeft = Math.Max(0, -crownX), padRight = Math.Max(0, crownX + crownWidth - width);
                int newWidth = padLeft + width + padRight, newHeight = Math.Max(height, crownY + crownHeight);
                var canvas = new Color32[newWidth * newHeight];
                for (int y = 0; y < height; y++)
                    Array.Copy(pixels, y * width, canvas, y * newWidth + padLeft, width);
                for (int y = 0; y < crownHeight; y++)
                    for (int x = 0; x < crownWidth; x++)
                    {
                        Color32 c = crown[y * crownWidth + x];
                        if (c.a == 0) continue;
                        int i = (crownY + y) * newWidth + padLeft + crownX + x;
                        canvas[i] = c.a == 255 ? c : Blend(canvas[i], c);
                    }
                pixels = canvas;
                pivot += new Vector2(padLeft, 0);
                width = newWidth;
                height = newHeight;
                pixelsPerUnit /= LordScale; // 同样像素占更大的世界尺寸：整体放大
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "HealthBarColors/" + source.name + (style.Lord ? "/lord" : ""),
                filterMode = source.texture.filterMode,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(pivot.x / width, pivot.y / height),
                pixelsPerUnit, 0, SpriteMeshType.FullRect, style.Lord ? Vector4.zero : source.border);
            sprite.name = texture.name;
            return sprite;
        }

        private static Color32 Blend(Color32 under, Color32 over)
        {
            float a = over.a / 255f, ua = under.a / 255f;
            float outA = a + ua * (1 - a);
            if (outA <= 0) return new Color32(0, 0, 0, 0);
            byte Mix(byte o, byte u) => (byte)Mathf.Clamp((o * a + u * ua * (1 - a)) / outA, 0, 255);
            return new Color32(Mix(over.r, under.r), Mix(over.g, under.g), Mix(over.b, under.b), (byte)Mathf.Clamp(outA * 255, 0, 255));
        }

        /// <summary>生成指定高度的皇冠（像素行自下而上），带 1 像素深色描边。</summary>
        private static Color32[] BuildCrown(int height, out int width)
        {
            LoadCrownSource();
            int inner = Math.Max(4, height - 2); // 上下各留 1 像素描边
            Color[] body;
            int bodyWidth;
            if (crownSource != null)
            {
                bodyWidth = Math.Max(4, Mathf.RoundToInt((float)crownSourceWidth * inner / crownSourceHeight));
                body = Downsample(crownSource, crownSourceWidth, crownSourceHeight, bodyWidth, inner);
            }
            else
            {
                body = PixelArtCrown(inner, out bodyWidth);
            }

            width = bodyWidth + 2;
            var result = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // 描边：周围 3×3 内本体覆盖度的最大值。
                    float around = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int bx = x - 1 + dx, by = y - 1 + dy;
                            if (bx < 0 || by < 0 || bx >= bodyWidth || by >= inner) continue;
                            around = Math.Max(around, body[by * bodyWidth + bx].a);
                        }
                    Color c = CrownOutline;
                    int sx = x - 1, sy = y - 1;
                    if (sx >= 0 && sy >= 0 && sx < bodyWidth && sy < inner)
                    {
                        Color b = body[sy * bodyWidth + sx];
                        c = Color.Lerp(c, b, b.a);
                    }
                    c.a = around;
                    result[y * width + x] = c;
                }
            return result;
        }

        // 读入游戏自带的皇冠剪影和细节图，合成金色皇冠（裁掉透明边）。只尝试一次。
        private static void LoadCrownSource()
        {
            if (crownLoaded) return;
            crownLoaded = true;
            Texture2D mask = null, detail = null;
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "Assets", "GUI", "Avatars");
                mask = LoadPng(System.IO.Path.Combine(dir, CrownMaskFile));
                detail = LoadPng(System.IO.Path.Combine(dir, CrownDetailFile));
                if (mask == null) return;
                int w = mask.width, h = mask.height;
                Color[] m = mask.GetPixels();
                Color[] d = detail != null && detail.width == w && detail.height == h ? detail.GetPixels() : null;

                int minX = w, maxX = -1, minY = h, maxY = -1;
                var coverage = new float[w * h];
                for (int i = 0; i < m.Length; i++)
                {
                    float cov = Mathf.Max(m[i].grayscale * m[i].a, d != null ? d[i].a : 0f);
                    coverage[i] = cov;
                    if (cov < 0.25f) continue;
                    int x = i % w, y = i / w;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                if (maxX < 0) return;

                int cw = maxX - minX + 1, ch = maxY - minY + 1;
                var src = new Color[cw * ch];
                for (int y = 0; y < ch; y++)
                {
                    Color gold = Color.Lerp(CrownBottom, CrownTop, (float)y / Math.Max(1, ch - 1)); // GetPixels 自下而上
                    for (int x = 0; x < cw; x++)
                    {
                        int i = (y + minY) * w + x + minX;
                        Color c = gold;
                        if (d != null && d[i].a > 0)
                            c = Color.Lerp(c, new Color(d[i].r, d[i].g, d[i].b), d[i].a * CrownDetailStrength);
                        c.a = coverage[i];
                        src[y * cw + x] = c;
                    }
                }
                crownSource = src;
                crownSourceWidth = cw;
                crownSourceHeight = ch;
            }
            catch (Exception)
            {
                crownSource = null;
            }
            finally
            {
                if (mask != null) Object.Destroy(mask);
                if (detail != null) Object.Destroy(detail);
            }
        }

        private static Texture2D LoadPng(string path)
        {
            if (!System.IO.File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path))) return tex;
            Object.Destroy(tex);
            return null;
        }

        // 面积平均缩小（颜色按覆盖度加权），小尺寸下边缘仍平滑。
        private static Color[] Downsample(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            Color c = src[yy * sw + xx];
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                        }
                    int n = (x1 - x0) * (y1 - y0);
                    dst[y * dw + x] = a > 0 ? new Color(r / a, g / a, b / a, Mathf.Clamp01(a / n * 1.15f)) : new Color(0, 0, 0, 0);
                }
            }
            return dst;
        }

        private static Color[] PixelArtCrown(int height, out int width)
        {
            int scale = Math.Max(1, height / FallbackCrown.Length);
            width = FallbackCrown[0].Length * scale;
            int h = FallbackCrown.Length * scale;
            var body = new Color[width * height];
            for (int row = 0; row < FallbackCrown.Length; row++)
                for (int col = 0; col < FallbackCrown[row].Length; col++)
                {
                    if (FallbackCrown[row][col] == '.') continue;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int y = (FallbackCrown.Length - 1 - row) * scale + dy;
                            if (y >= height) continue;
                            body[y * width + col * scale + dx] = Color.Lerp(CrownBottom, CrownTop, (float)y / Math.Max(1, h - 1));
                        }
                }
            return body;
        }

        // 图集纹理通常不可读，先用 GPU 把精灵所在区域拷到临时 RenderTexture 再读回。
        private static Color32[] ReadPixels(Sprite source, int width, int height)
        {
            Texture atlas = source.texture;
            Rect texRect = source.textureRect;
            Vector2 texOffset = source.textureRectOffset;
            int texWidth = Mathf.RoundToInt(texRect.width);
            int texHeight = Mathf.RoundToInt(texRect.height);

            var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.SetPixels32(new Color32[width * height]);

            RenderTexture previous = RenderTexture.active;
            RenderTexture temp = RenderTexture.GetTemporary(texWidth, texHeight, 0, RenderTextureFormat.ARGB32);
            try
            {
                temp.filterMode = FilterMode.Point;
                Graphics.Blit(atlas, temp,
                    new Vector2(texRect.width / atlas.width, texRect.height / atlas.height),
                    new Vector2(texRect.x / atlas.width, texRect.y / atlas.height));
                RenderTexture.active = temp;
                // 紧凑打包的精灵在图集里去掉了透明边，textureRectOffset 把它放回原矩形中的位置。
                readable.ReadPixels(new Rect(0, 0, texWidth, texHeight),
                    Mathf.RoundToInt(texOffset.x), Mathf.RoundToInt(texOffset.y), false);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temp);
                Object.Destroy(readable);
            }
        }

        // 换成目标颜色，连同目标颜色的亮度：每个像素的明暗按它相对整条血条最亮处的比例缩放目标色，
        // 原图的高光（最暗通道）按比例掺一点白，保留立体感。只换色相会把灰色变成近白、浅蓝和盟友蓝分不开。
        private static void Recolor(Color32[] pixels, Color32 target)
        {
            int brightest = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.a == 0) continue;
                int max = Math.Max(p.r, Math.Max(p.g, p.b));
                int min = Math.Min(p.r, Math.Min(p.g, p.b));
                if (max - min >= MinSaturation && max > brightest) brightest = max;
            }
            if (brightest == 0)
                return;

            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.a == 0)
                    continue;

                int max = Math.Max(p.r, Math.Max(p.g, p.b));
                int min = Math.Min(p.r, Math.Min(p.g, p.b));
                if (max - min < MinSaturation)
                    continue;

                float shade = (float)max / brightest;
                float highlight = 0.35f * min / 255f;
                p.r = (byte)Mathf.Clamp(target.r * shade * (1 - highlight) + 255 * highlight, 0, 255);
                p.g = (byte)Mathf.Clamp(target.g * shade * (1 - highlight) + 255 * highlight, 0, 255);
                p.b = (byte)Mathf.Clamp(target.b * shade * (1 - highlight) + 255 * highlight, 0, 255);
                pixels[i] = p;
            }
        }
    }
}
