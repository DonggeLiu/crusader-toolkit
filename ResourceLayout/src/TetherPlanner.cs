using System;
using System.Collections.Generic;

namespace ResourceLayout
{
    /// <summary>
    /// 采石场布局确定后，给每个采石场配系牛桩。
    ///
    /// 原版逻辑（CrusaderDE.dll 反汇编 + 实测）：
    /// - 系牛桩和采石场没有固定配对。系牛桩的搬运工每次从系牛桩出发，挑“曼哈顿距离最近、且堆上至少有 1 块石头”的石料堆
    ///   （得分 800 − 距离；最近白跑过一趟的搬运工会额外偏向 ≥8 块的堆）。
    /// - 搬运工一次只搬 1 块回系牛桩，攒满 8 块后陪牛把 8 块（加成后 12 块）送到有空位的库房，再回来。
    /// - 所以一个系牛桩运 8 块的周期 ≈ 8 ×（2 × 到石料堆的路程 × 每格用时 + 停顿）+（2 × 到库房的路程 × 每格用时 + 卸货）。
    /// - 采石场大约每 240 tick 产 1 块。所需系牛桩数 = 周期 ÷（8 × 240），向上取整。
    ///
    /// 放置：到石料堆的距离要走 16 次、到库房只走 2 次，所以按代价 16 × d(系牛桩, 石料堆) + 2 × d(系牛桩, 库房) 贪心挑位置，
    /// 各采石场轮流各放一个，保证每个石料堆旁边都是“离它最近”的系牛桩。放下后检查所有建筑（采石场、石料堆、系牛桩）
    /// 四周仍有工人能走到的空地。
    /// </summary>
    internal static class TetherPlanner
    {
        public const float TicksPerTile = 19f;   // 实测直线约 16–17，绕路取 19
        public const float PausePerStone = 32f;  // 石料堆、系牛桩各停一下
        public const float UnloadTicks = 100f;
        public const float QuarryTicksPerStone = 240f;
        private const int MaxPerQuarry = 4;
        private const int SearchMargin = 14;

        public static readonly LayoutType Tether =
            new LayoutType("系牛桩", SHCDESE.Interop.eMappers.MAPPER_OXENBASE, 2, 4, new UnityEngine.Color(1f, 0.7f, 0.3f), "UI-Buildings D007");

        public sealed class Result
        {
            public List<Placement> Tethers = new List<Placement>();
            public int[] PerQuarry;
            public int[] Needed;
            public bool HasStockpile;
            public int Shortfall;
        }

        private struct Rect
        {
            public int X, Y, S;
            public bool NeedsAccess;
        }

        /// <summary>只在主线程调用（要用原版放置校验）。quarries 为已选定的采石场（Region 里的 Placement）。</summary>
        public static Result Plan(Region region, List<Placement> quarries)
        {
            var result = new Result { PerQuarry = new int[quarries.Count], Needed = new int[quarries.Count] };
            if (quarries.Count == 0)
                return result;

            // 已占用的格：已有建筑由校验判断；本次规划的采石场（含石料堆和中间一列）这里记下。
            var planned = new HashSet<long>();
            var rects = new List<Rect>();
            foreach (Placement q in quarries)
            {
                LayoutType t = region.Types[q.Type];
                foreach ((int x, int y) in Region.Cells(t, q.X, q.Y))
                    planned.Add(Region.Key(x, y));
                rects.Add(new Rect { X = q.X, Y = q.Y, S = t.Size, NeedsAccess = true });
                rects.Add(new Rect { X = q.X + LayoutType.PileDx, Y = q.Y + LayoutType.PileDy, S = LayoutType.PileSize, NeedsAccess = true });
            }

            // 最近的库房（按区域中心）。没有库房时只按离石料堆近来放，个数按 3。
            (int sx, int sy) = (0, 0);
            result.HasStockpile = NearestStockpile((region.MinX + region.MaxX) / 2, (region.MinY + region.MaxY) / 2, out sx, out sy);

            // 各采石场所需个数。
            var pileDoor = new (int X, int Y)[quarries.Count];
            for (int i = 0; i < quarries.Count; i++)
            {
                Placement q = quarries[i];
                pileDoor[i] = (q.X + LayoutType.PileDx + 1, q.Y + LayoutType.PileDy + LayoutType.PileSize);
                if (!result.HasStockpile)
                {
                    result.Needed[i] = 3;
                    continue;
                }
                // 先按系牛桩就在石料堆旁（约 3 格）估算。
                float cycle = 8f * (2f * 3f * TicksPerTile + PausePerStone) +
                              2f * Octile(pileDoor[i].X, pileDoor[i].Y, sx, sy) * TicksPerTile + UnloadTicks;
                result.Needed[i] = Math.Max(1, Math.Min(MaxPerQuarry, (int)Math.Ceiling(cycle / (8f * QuarryTicksPerStone))));
            }

            // 候选位置：区域外扩一圈内所有能放系牛桩、且不压规划建筑的左上角。
            var candidates = new List<(int X, int Y)>();
            for (int x = region.MinX - SearchMargin; x <= region.MaxX + SearchMargin; x++)
            for (int y = region.MinY - SearchMargin; y <= region.MaxY + SearchMargin; y++)
            {
                if (Overlaps(planned, x, y, 2))
                    continue;
                if ((GameGrid.Logic(x, y) & 0x30) != 0 || (GameGrid.Logic(x + 1, y + 1) & 0x30) != 0)
                    continue;
                if (GameGrid.CanPlace(Tether, x, y))
                    candidates.Add((x, y));
            }

            // 轮流给每个采石场放一个，直到都满足或没位置。
            var used = new HashSet<long>(planned);
            int round = 0;
            bool progress = true;
            while (progress && round < MaxPerQuarry)
            {
                progress = false;
                for (int i = 0; i < quarries.Count; i++)
                {
                    if (result.PerQuarry[i] >= result.Needed[i] || round >= result.Needed[i])
                        continue;
                    var ranked = new List<(float Cost, int Index)>();
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        (int cx, int cy) = candidates[c];
                        if (Overlaps(used, cx, cy, 2))
                            continue;
                        int dx = cx + 1, dy = cy + 1;
                        float cost = 16f * Octile(dx, dy, pileDoor[i].X, pileDoor[i].Y) +
                                     (result.HasStockpile ? 2f * Octile(dx, dy, sx, sy) : 0f);
                        // 必须是离这个石料堆最近的（否则搬运工会跑去别的堆）。
                        if (!NearestPileIs(i, dx, dy, pileDoor))
                            cost += 1000f;
                        ranked.Add((cost, c));
                    }
                    ranked.Sort((a, b) => a.Cost.CompareTo(b.Cost));

                    // 放下后所有建筑仍要能进出：按代价从低到高找第一个满足的（最多试 40 个）。
                    int best = -1;
                    for (int r = 0; r < ranked.Count && r < 40; r++)
                    {
                        (int cx, int cy) = candidates[ranked[r].Index];
                        var trial = new List<Rect>(rects) { new Rect { X = cx, Y = cy, S = 2, NeedsAccess = true } };
                        if (AllReachable(trial, region))
                        {
                            best = ranked[r].Index;
                            break;
                        }
                    }
                    if (best < 0)
                        continue;
                    (int bx, int by) = candidates[best];
                    for (int ox = 0; ox < 2; ox++)
                    for (int oy = 0; oy < 2; oy++)
                        used.Add(Region.Key(bx + ox, by + oy));
                    rects.Add(new Rect { X = bx, Y = by, S = 2, NeedsAccess = true });
                    result.Tethers.Add(new Placement { Type = -1, X = bx, Y = by });
                    result.PerQuarry[i]++;
                    progress = true;
                }
                round++;
            }

            for (int i = 0; i < quarries.Count; i++)
                result.Shortfall += Math.Max(0, result.Needed[i] - result.PerQuarry[i]);
            return result;
        }

        private static bool NearestPileIs(int i, int x, int y, (int X, int Y)[] doors)
        {
            int mine = Math.Abs(x - doors[i].X) + Math.Abs(y - doors[i].Y);
            for (int j = 0; j < doors.Length; j++)
                if (j != i && Math.Abs(x - doors[j].X) + Math.Abs(y - doors[j].Y) < mine)
                    return false;
            return true;
        }

        private static bool Overlaps(HashSet<long> set, int x, int y, int s)
        {
            for (int dx = 0; dx < s; dx++)
            for (int dy = 0; dy < s; dy++)
                if (set.Contains(Region.Key(x + dx, y + dy)))
                    return true;
            return false;
        }

        public static float Octile(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x0 - x1), dy = Math.Abs(y0 - y1);
            return Math.Max(dx, dy) + 0.414f * Math.Min(dx, dy);
        }

        private static unsafe bool NearestStockpile(int cx, int cy, out int sx, out int sy)
        {
            sx = sy = 0;
            int player = SHCDESE.API.GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            float best = float.MaxValue;
            foreach (int id in SHCDESE.API.GameBuildingManagerAPI.Instance.GetAllAliveBuildings())
            {
                if (!SHCDESE.API.GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out SHCDESE.Interop.GameBuilding* b) || b == null)
                    continue;
                if ((int)b->r_BuildingType != 10 || b->r_PlayerIdOwner != player)
                    continue;
                float d = Octile(b->r_TilePositionXBegin, b->r_TilePositionYBegin, cx, cy);
                if (d < best)
                {
                    best = d;
                    sx = b->r_TilePositionXBegin;
                    sy = b->r_TilePositionYBegin;
                }
            }
            return best < float.MaxValue;
        }

        // 所有建筑四周至少一格空地能从外圈走到（空地 = 没有已有建筑、不是地图外、不被这些矩形覆盖）。
        private static bool AllReachable(List<Rect> rects, Region region)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (Rect r in rects)
            {
                x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.S - 1); y1 = Math.Max(y1, r.Y + r.S - 1);
            }
            x0 -= 2; y0 -= 2; x1 += 2; y1 += 2;
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            var occ = new bool[w * h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int gx = x + x0, gy = y + y0;
                occ[y * w + x] = region.Blocked.Contains(Region.Key(gx, gy)) ||
                                 (!region.Blocked.Contains(Region.Key(gx, gy)) && Outside(region, gx, gy) &&
                                  (GameGrid.HasStructure(gx, gy) || (GameGrid.Logic(gx, gy) & 0x30) != 0));
            }
            foreach (Rect r in rects)
                for (int dx = 0; dx < r.S; dx++)
                for (int dy = 0; dy < r.S; dy++)
                    occ[(r.Y + dy - y0) * w + (r.X + dx - x0)] = true;

            var reach = new bool[w * h];
            var q = new Queue<int>();
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (x != 0 && y != 0 && x != w - 1 && y != h - 1)
                    continue;
                int i = y * w + x;
                if (!occ[i] && !reach[i]) { reach[i] = true; q.Enqueue(i); }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % w, y = i / w;
                foreach (int n in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                    if (n >= 0 && !occ[n] && !reach[n]) { reach[n] = true; q.Enqueue(n); }
            }

            foreach (Rect r in rects)
            {
                bool ok = false;
                for (int k = 0; k < r.S && !ok; k++)
                {
                    ok = At(reach, w, h, x0, y0, r.X + k, r.Y - 1) || At(reach, w, h, x0, y0, r.X + k, r.Y + r.S) ||
                         At(reach, w, h, x0, y0, r.X - 1, r.Y + k) || At(reach, w, h, x0, y0, r.X + r.S, r.Y + k);
                }
                if (!ok)
                    return false;
            }
            return true;
        }

        private static bool Outside(Region r, int x, int y) =>
            x < r.MinX - 2 || x > r.MaxX + 2 || y < r.MinY - 2 || y > r.MaxY + 3;

        private static bool At(bool[] reach, int w, int h, int x0, int y0, int x, int y)
        {
            int gx = x - x0, gy = y - y0;
            return gx >= 0 && gy >= 0 && gx < w && gy < h && reach[gy * w + gx];
        }
    }
}
