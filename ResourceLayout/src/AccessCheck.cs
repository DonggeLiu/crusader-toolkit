using System;
using System.Collections.Generic;

namespace ResourceLayout
{
    /// <summary>
    /// 工人进出检查。原版建筑记录的是“候选入口”并会轮换（GameBuilding.r_AccessCandidateCursor），
    /// 工人可以从占地四周任一格进出：只要占地四邻的某一格是空地、且能从区域外走到，这个建筑就能用。
    /// 空地 = 不是已有建筑、不是地图外、不被本次布局的建筑占地覆盖。
    /// 所以最外圈的建筑只要朝外一侧没堵死就行；只有四面都被围住（例如 3×3 排列的正中间那个）才进不去。
    /// Repair 反复去掉进不去的建筑（去掉后可能让别的变得能进），直到全部能进。
    /// </summary>
    internal sealed class AccessCheck
    {
        private readonly int x0, y0, w, h;
        private readonly bool[] blocked;
        private readonly int[] occ;
        private readonly bool[] reach;
        private readonly int[] queue;
        private readonly Region region;

        public AccessCheck(Region region)
        {
            this.region = region;
            x0 = region.MinX - 2;
            y0 = region.MinY - 2;
            w = region.MaxX - region.MinX + 5;
            h = region.MaxY - region.MinY + 5;
            blocked = new bool[w * h];
            occ = new int[w * h];
            reach = new bool[w * h];
            queue = new int[w * h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                blocked[y * w + x] = region.Blocked.Contains(Region.Key(x + x0, y + y0));
        }

        public bool Needed
        {
            get
            {
                foreach (LayoutType t in region.Types)
                    if (t.NeedsAccess)
                        return true;
                return false;
            }
        }

        /// <summary>去掉进不去的建筑，返回去掉的个数（placements 原地修改）。</summary>
        public int Repair(List<Placement> placements)
        {
            int removed = 0;
            while (true)
            {
                Array.Clear(occ, 0, occ.Length);
                foreach (Placement p in placements)
                    Mark(p, 1);
                Flood();

                int before = placements.Count;
                placements.RemoveAll(p => region.Types[p.Type].NeedsAccess && !Reachable(p));
                int now = before - placements.Count;
                if (now == 0)
                    return removed;
                removed += now;
            }
        }

        private void Mark(Placement p, int v)
        {
            foreach ((int x, int y) in Region.Cells(region.Types[p.Type], p.X, p.Y))
            {
                int gx = x - x0, gy = y - y0;
                if (gx >= 0 && gy >= 0 && gx < w && gy < h)
                    occ[gy * w + gx] += v;
            }
        }

        private bool Free(int i) => !blocked[i] && occ[i] == 0;

        // 从包围盒外圈的空地出发，4 邻接扩展。
        private void Flood()
        {
            Array.Clear(reach, 0, reach.Length);
            int head = 0, tail = 0;
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (x != 0 && y != 0 && x != w - 1 && y != h - 1)
                    continue;
                int i = y * w + x;
                if (Free(i) && !reach[i])
                {
                    reach[i] = true;
                    queue[tail++] = i;
                }
            }

            while (head < tail)
            {
                int i = queue[head++];
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1, ref tail);
                if (x < w - 1) Visit(i + 1, ref tail);
                if (y > 0) Visit(i - w, ref tail);
                if (y < h - 1) Visit(i + w, ref tail);
            }
        }

        private void Visit(int i, ref int tail)
        {
            if (!reach[i] && Free(i))
            {
                reach[i] = true;
                queue[tail++] = i;
            }
        }

        // 主体占地四周有一格可达；采石场的石料堆（牛要来运）四周也要有一格可达。
        private bool Reachable(Placement p)
        {
            LayoutType t = region.Types[p.Type];
            if (!SideReachable(p.X, p.Y, t.Size))
                return false;
            return !t.HasQuarryPile ||
                   SideReachable(p.X + LayoutType.PileDx, p.Y + LayoutType.PileDy, LayoutType.PileSize);
        }

        private bool SideReachable(int x, int y, int s)
        {
            for (int k = 0; k < s; k++)
            {
                if (IsReach(x + k, y - 1) || IsReach(x + k, y + s) ||
                    IsReach(x - 1, y + k) || IsReach(x + s, y + k))
                    return true;
            }
            return false;
        }

        private bool IsReach(int x, int y)
        {
            int gx = x - x0, gy = y - y0;
            return gx >= 0 && gy >= 0 && gx < w && gy < h && reach[gy * w + gx];
        }
    }
}
