using System;
using System.Collections.Generic;

namespace ResourceLayout
{
    internal struct Placement
    {
        public int Type; // 在本次规划类型数组里的下标
        public int X;
        public int Y;
    }

    /// <summary>
    /// 一片资源区：从鼠标所在处出发，沿“至少一种所选建筑能以此格为左上角合法放置”的格子扩展，
    /// 得到这片连通区域内各类型的全部合法候选位置。只在主线程构建（要调用原版校验）。
    /// </summary>
    internal sealed class Region
    {
        private const int MaxRadius = 80;
        private const int MaxOrigins = 30000;

        public LayoutType[] Types;
        public readonly List<Placement> Candidates = new List<Placement>();
        public readonly HashSet<long> Origins = new HashSet<long>();
        public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue;

        /// <summary>包围盒外扩 2 格范围内已有建筑或地图外的格（算入口能否走到外面时用）。</summary>
        public readonly HashSet<long> Blocked = new HashSet<long>();

        public static long Key(int x, int y) => ((long)x << 20) | (uint)y;

        /// <summary>鼠标所在格是否被这片区域的某个候选覆盖（用来判断移动鼠标后要不要重算）。</summary>
        public bool Covers(int x, int y)
        {
            int s = 0;
            foreach (LayoutType t in Types)
                s = Math.Max(s, t.Size);
            for (int dx = 0; dx < s; dx++)
            for (int dy = 0; dy < s; dy++)
                if (Origins.Contains(Key(x - dx, y - dy)))
                    return true;
            return false;
        }

        public static Region Build(LayoutType[] types, int hx, int hy)
        {
            var region = new Region { Types = types };
            var valid = new Dictionary<long, int>(); // 格 → 合法类型位掩码
            var queue = new Queue<long>();

            int MaskAt(int x, int y)
            {
                long k = Key(x, y);
                if (valid.TryGetValue(k, out int m))
                    return m;
                m = 0;
                if (x >= 0 && y >= 0 && x < 800 && y < 800 && Math.Abs(x - hx) <= MaxRadius && Math.Abs(y - hy) <= MaxRadius)
                {
                    for (int i = 0; i < types.Length; i++)
                        if (Fits(types[i], x, y))
                            m |= 1 << i;
                }
                valid[k] = m;
                return m;
            }

            // 起点：占地覆盖鼠标格的合法位置。
            int maxSize = 0;
            foreach (LayoutType t in types)
                maxSize = Math.Max(maxSize, t.Size);
            for (int dx = 0; dx < maxSize; dx++)
            for (int dy = 0; dy < maxSize; dy++)
            {
                int x = hx - dx, y = hy - dy;
                if (MaskAt(x, y) != 0 && region.Origins.Add(Key(x, y)))
                    queue.Enqueue(Key(x, y));
            }

            // 资源建筑（采石场、铁矿、沥青坑）：资源常常是几块挨着的小片（如沼泽里的几处沥青），
            // 间隔不到一个建筑宽的也连成同一片一起规划；农场按相邻格扩展。
            int reach = 1;
            foreach (LayoutType t in types)
                if (t.NeedsAccess)
                    reach = Math.Max(reach, t.Size);

            while (queue.Count > 0 && region.Origins.Count < MaxOrigins)
            {
                long k = queue.Dequeue();
                int cx = (int)(k >> 20), cy = (int)(k & 0xFFFFF);
                for (int dx = -reach; dx <= reach; dx++)
                for (int dy = -reach; dy <= reach; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (MaskAt(x, y) != 0 && region.Origins.Add(Key(x, y)))
                        queue.Enqueue(Key(x, y));
                }
            }

            foreach (long k in region.Origins)
            {
                int x = (int)(k >> 20), y = (int)(k & 0xFFFFF);
                int m = valid[k];
                for (int i = 0; i < types.Length; i++)
                {
                    if ((m & (1 << i)) == 0)
                        continue;
                    region.Candidates.Add(new Placement { Type = i, X = x, Y = y });
                    region.MinX = Math.Min(region.MinX, x);
                    region.MinY = Math.Min(region.MinY, y);
                    int ext = Extent(types[i]);
                    region.MaxX = Math.Max(region.MaxX, x + ext - 1);
                    region.MaxY = Math.Max(region.MaxY, y + types[i].Size - 1);
                }
            }

            if (region.Candidates.Count > 0)
            {
                for (int x = region.MinX - 2; x <= region.MaxX + 2; x++)
                for (int y = region.MinY - 2; y <= region.MaxY + 3; y++)
                    if (GameGrid.HasStructure(x, y) || (GameGrid.Logic(x, y) & 0x30) != 0)
                        region.Blocked.Add(Key(x, y));
            }

            return region;
        }

        /// <summary>原版校验通过，且采石场的石料堆位置空着。</summary>
        private static bool Fits(LayoutType type, int x, int y)
        {
            // 原版校验在地图边缘外会误报可放，占地碰到地图外（逻辑层 0x10 / 0x20）的一律排除。
            for (int dx = 0; dx < Extent(type); dx++)
            for (int dy = 0; dy < type.Size; dy++)
                if ((GameGrid.Logic(x + dx, y + dy) & 0x30) != 0)
                    return false;
            if (!GameGrid.CanPlace(type, x, y))
                return false;
            if (type.HasQuarryPile)
            {
                for (int dx = 0; dx < LayoutType.PileSize; dx++)
                for (int dy = 0; dy < LayoutType.PileSize; dy++)
                    if (GameGrid.HasStructure(x + LayoutType.PileDx + dx, y + LayoutType.PileDy + dy))
                        return false;
            }
            return true;
        }

        /// <summary>占地在 x 方向的总宽度（采石场包括石料堆）。</summary>
        public static int Extent(LayoutType type) =>
            type.HasQuarryPile ? LayoutType.PileDx + LayoutType.PileSize : type.Size;

        /// <summary>候选占用的全部格（采石场：6×6 本体 + 中间一列空隙 + 2×2 石料堆，空隙也预留以免挡住石料堆的路）。</summary>
        public static IEnumerable<(int X, int Y)> Cells(LayoutType type, int x, int y)
        {
            for (int dx = 0; dx < type.Size; dx++)
            for (int dy = 0; dy < type.Size; dy++)
                yield return (x + dx, y + dy);
            if (type.HasQuarryPile)
            {
                for (int dy = LayoutType.PileDy; dy < LayoutType.PileDy + LayoutType.PileSize; dy++)
                    yield return (x + type.Size, y + dy);
                for (int dx = 0; dx < LayoutType.PileSize; dx++)
                for (int dy = 0; dy < LayoutType.PileSize; dy++)
                    yield return (x + LayoutType.PileDx + dx, y + LayoutType.PileDy + dy);
            }
        }
    }
}
