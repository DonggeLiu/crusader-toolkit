using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ResourceLayout
{
    /// <summary>
    /// 分支定界求最优布局。
    ///
    /// 目标（字典序）：先满足每种类型的最少个数，再最大化“超出最少个数的部分”中最小的那个（没设最少个数时就是
    /// 数量最少的类型的数量，避免只放面积小的一种），最后最大化总数。只有这片区域能放的类型参与。
    ///
    /// 枚举：按行优先顺序逐格决定，当前第一个还可能被用到的格，要么作为某个候选的左上角放下它，要么留空。
    /// 每个候选的左上角恰好是它占用格中行优先最靠前的一格，所以不重不漏。
    ///
    /// 上界：维护每格“还有几个可行候选覆盖它”，只计被覆盖的空闲格；还能放的个数 ≤ 剩余面积 ÷ 最小占地，
    /// 且 ≤ 格点数（任何边长 ≥ p 的正方形至少包含周期 p 格点阵中的一个点，取所有偏移中的最小值）。
    /// 名额优先补给超出量最少的类型，得到目标的上界。
    ///
    /// 工人进出：记录更好的解时，先去掉进不去的建筑（AccessCheck），按去掉后的结果比较。
    /// 去掉只会让数量变少，上界依然成立，所以结果仍是精确最优。
    ///
    /// 用显式栈迭代；超时返回已找到的最好解并标明未证明最优。纯计算，可在后台线程运行。
    /// </summary>
    internal sealed class Optimizer
    {
        public sealed class Result
        {
            public List<Placement> Placements = new List<Placement>();
            public int[] Counts;
            public bool Optimal;
            public long Nodes;
            public double Milliseconds;
            /// <summary>这片区域里能放下的类型。</summary>
            public bool[] Present;
            /// <summary>根节点的总数上界。</summary>
            public int UpperBoundTotal;
            /// <summary>是否满足全部最少个数。</summary>
            public bool MinimumsMet;
        }

        private readonly int typeCount;
        private readonly bool[] present;
        private readonly int[] mins;
        private readonly int minArea;

        private readonly int n;
        private readonly int[][] anchored;
        private readonly int[][] candCells;
        private readonly int[][] cellCands;
        private readonly int[] candType;
        private readonly Placement[] cands;

        private readonly bool[] used;
        private readonly bool[] alive;
        private readonly int[] cover;
        private int usable;

        private readonly int period;
        private readonly int[] cellOff;
        private readonly int[] offCount;

        private int[] trail = new int[1 << 16];
        private int trailTop;

        private readonly int[] counts;
        private int total;
        private readonly List<int> chosen = new List<int>();

        // 进出约束（剪枝）：需要进出的建筑，占地四邻的格 → 可用格下标；四邻里只要有一格不在任何候选里且不是已有建筑，
        // 它就永远有出口（alwaysOpen）。已放建筑的四邻全被建筑占地盖住时，这条分支被“不放它”的分支支配，直接跳过。
        private readonly int[][] perim;
        private readonly bool[] alwaysOpen;
        private readonly bool[] fp;
        private readonly int[] owner;

        private readonly AccessCheck access;
        private readonly bool checkAccess;

        private int[] bestCounts;
        private List<Placement> best = new List<Placement>();
        private int bestMin = int.MinValue, bestTotal = -1;
        private long nodes;
        private bool timedOut;
        private readonly Stopwatch watch = new Stopwatch();
        private readonly int timeLimitMs;
        private readonly CancellationToken cancel;

        public Optimizer(Region region, int[] minimums, int timeLimitMs, CancellationToken cancel)
        {
            this.timeLimitMs = timeLimitMs;
            this.cancel = cancel;
            typeCount = region.Types.Length;
            present = new bool[typeCount];
            counts = new int[typeCount];
            bestCounts = new int[typeCount];
            mins = minimums ?? new int[typeCount];
            int[] area = new int[typeCount];

            int w = region.MaxX - region.MinX + 1;
            var cellsPerCand = new List<long[]>();
            var allCells = new SortedSet<long>();
            foreach (Placement p in region.Candidates)
            {
                var list = new List<long>();
                foreach ((int x, int y) in Region.Cells(region.Types[p.Type], p.X, p.Y))
                {
                    long order = (long)(y - region.MinY) * w + (x - region.MinX);
                    list.Add(order);
                    allCells.Add(order);
                }
                cellsPerCand.Add(list.ToArray());
                area[p.Type] = list.Count;
                present[p.Type] = true;
            }

            var index = new Dictionary<long, int>();
            foreach (long c in allCells)
                index[c] = index.Count;
            n = index.Count;

            cands = region.Candidates.ToArray();
            candType = new int[cands.Length];
            candCells = new int[cands.Length][];
            var anchorLists = new List<int>[n];
            var coverLists = new List<int>[n];
            for (int i = 0; i < cands.Length; i++)
            {
                candType[i] = cands[i].Type;
                long[] cs = cellsPerCand[i];
                candCells[i] = new int[cs.Length];
                int anchor = int.MaxValue;
                for (int j = 0; j < cs.Length; j++)
                {
                    int cell = index[cs[j]];
                    candCells[i][j] = cell;
                    anchor = Math.Min(anchor, cell);
                    (coverLists[cell] ?? (coverLists[cell] = new List<int>())).Add(i);
                }
                (anchorLists[anchor] ?? (anchorLists[anchor] = new List<int>())).Add(i);
            }
            anchored = new int[n][];
            cellCands = new int[n][];
            for (int i = 0; i < n; i++)
            {
                anchored[i] = anchorLists[i]?.ToArray() ?? Array.Empty<int>();
                cellCands[i] = coverLists[i]?.ToArray() ?? Array.Empty<int>();
            }

            minArea = int.MaxValue;
            period = int.MaxValue;
            for (int t = 0; t < typeCount; t++)
            {
                if (!present[t])
                    continue;
                minArea = Math.Min(minArea, area[t]);
                period = Math.Min(period, region.Types[t].Size);
            }
            if (period == int.MaxValue)
                period = 1;

            cellOff = new int[n];
            offCount = new int[period * period];
            foreach (KeyValuePair<long, int> kv in index)
            {
                int gx = (int)(kv.Key % w) + region.MinX;
                int gy = (int)(kv.Key / w) + region.MinY;
                cellOff[kv.Value] = (gx % period) * period + (gy % period);
            }

            used = new bool[n];
            alive = new bool[cands.Length];
            cover = new int[n];
            for (int c = 0; c < cands.Length; c++)
            {
                alive[c] = true;
                foreach (int cell in candCells[c])
                    cover[cell]++;
            }
            usable = n;
            foreach (int off in cellOff)
                offCount[off]++;

            access = new AccessCheck(region);
            checkAccess = access.Needed;

            fp = new bool[n];
            owner = new int[n];
            for (int i = 0; i < n; i++)
                owner[i] = -1;
            perim = new int[cands.Length][];
            alwaysOpen = new bool[cands.Length];
            for (int i = 0; i < cands.Length; i++)
            {
                LayoutType t = region.Types[cands[i].Type];
                if (!t.NeedsAccess)
                {
                    alwaysOpen[i] = true;
                    perim[i] = Array.Empty<int>();
                    continue;
                }
                var list = new List<int>();
                int px = cands[i].X, py = cands[i].Y, sz = t.Size;
                for (int k = 0; k < sz && !alwaysOpen[i]; k++)
                {
                    foreach ((int x, int y) in new[] { (px + k, py - 1), (px + k, py + sz), (px - 1, py + k), (px + sz, py + k) })
                    {
                        long order = (long)(y - region.MinY) * w + (x - region.MinX);
                        bool inBox = x >= region.MinX && x <= region.MaxX && y >= region.MinY && y <= region.MaxY;
                        if (inBox && index.TryGetValue(order, out int cell))
                            list.Add(cell);
                        else if (!region.Blocked.Contains(Region.Key(x, y)))
                        {
                            alwaysOpen[i] = true;
                            break;
                        }
                    }
                }
                perim[i] = list.ToArray();
            }
        }

        private sealed class Frame
        {
            public int Cell;
            public int[] Options;
            public int Next;
            public int Mark;
            public int Placed = -1;
        }

        public Result Solve()
        {
            watch.Start();
            int rootUb = n > 0 && minArea != int.MaxValue ? Math.Min(n / minArea, LatticeBound()) : 0;

            var stack = new Stack<Frame>();
            Frame root = MakeFrame(-1);
            if (root != null)
                stack.Push(root);

            while (stack.Count > 0)
            {
                if ((++nodes & 1023) == 0 && (watch.ElapsedMilliseconds > timeLimitMs || cancel.IsCancellationRequested))
                {
                    timedOut = true;
                    break;
                }

                Frame f = stack.Peek();
                Undo(f);

                if (f.Next < f.Options.Length)
                {
                    int c = f.Options[f.Next++];
                    if (!alive[c])
                        continue;
                    Place(c);
                    f.Placed = c;
                    if (checkAccess && Enclosed(c))
                        continue; // 下一轮循环开头的 Undo 会撤销这次放置
                }
                else if (f.Next == f.Options.Length)
                {
                    f.Next++;
                    UseCell(f.Cell);
                }
                else
                {
                    stack.Pop();
                    continue;
                }

                Frame child = MakeFrame(f.Cell);
                if (child != null)
                    stack.Push(child);
            }

            watch.Stop();
            return new Result
            {
                Counts = bestCounts,
                Optimal = !timedOut && !cancel.IsCancellationRequested,
                Nodes = nodes,
                Milliseconds = watch.Elapsed.TotalMilliseconds,
                Present = present,
                UpperBoundTotal = rootUb,
                MinimumsMet = bestMin >= 0,
                Placements = best,
            };
        }

        private Frame MakeFrame(int after)
        {
            Record();

            int i = after + 1;
            while (i < n && (used[i] || cover[i] == 0))
                i++;
            if (i >= n)
                return null;

            int extra = Math.Min(usable / minArea, LatticeBound());
            int ubTotal = total + extra;
            int ubMin = WaterFill(extra);
            if (ubMin < bestMin || (ubMin == bestMin && ubTotal <= bestTotal))
                return null;

            var opts = new List<int>();
            foreach (int c in anchored[i])
                if (alive[c])
                    opts.Add(c);
            // 超出量少的类型优先。
            opts.Sort((a, b) => (counts[candType[a]] - mins[candType[a]]).CompareTo(counts[candType[b]] - mins[candType[b]]));
            return new Frame { Cell = i, Options = opts.ToArray(), Mark = trailTop };
        }

        // 当前布局比已知最好的好时，去掉进不去的建筑后再比较。
        private void Record()
        {
            int rawMin = MinExcess(counts);
            if (!(rawMin > bestMin || (rawMin == bestMin && total > bestTotal)))
                return;

            var list = new List<Placement>(chosen.Count);
            foreach (int c in chosen)
                list.Add(cands[c]);
            int[] cnt = (int[])counts.Clone();
            int tot = total;
            if (checkAccess && access.Repair(list) > 0)
            {
                Array.Clear(cnt, 0, cnt.Length);
                foreach (Placement p in list)
                    cnt[p.Type]++;
                tot = list.Count;
            }

            int m = MinExcess(cnt);
            if (m > bestMin || (m == bestMin && tot > bestTotal))
            {
                bestMin = m;
                bestTotal = tot;
                bestCounts = cnt;
                best = list;
            }
        }

        private int MinExcess(int[] c)
        {
            int m = int.MaxValue;
            for (int t = 0; t < typeCount; t++)
                if (present[t])
                    m = Math.Min(m, c[t] - mins[t]);
            return m == int.MaxValue ? 0 : m;
        }

        private void Undo(Frame f)
        {
            while (trailTop > f.Mark)
            {
                int op = trail[--trailTop];
                if (op > 0)
                {
                    int c = op - 1;
                    alive[c] = true;
                    foreach (int cell in candCells[c])
                    {
                        if (cover[cell]++ == 0 && !used[cell])
                            Gain(cell);
                    }
                }
                else
                {
                    int cell = -op - 1;
                    used[cell] = false;
                    if (cover[cell] > 0)
                        Gain(cell);
                }
            }

            if (f.Placed >= 0)
            {
                foreach (int cell in candCells[f.Placed])
                {
                    fp[cell] = false;
                    owner[cell] = -1;
                }
                counts[candType[f.Placed]]--;
                total--;
                chosen.RemoveAt(chosen.Count - 1);
                f.Placed = -1;
            }
        }

        private void Place(int c)
        {
            counts[candType[c]]++;
            total++;
            chosen.Add(c);
            foreach (int cell in candCells[c])
            {
                UseCell(cell);
                fp[cell] = true;
                owner[cell] = c;
            }
        }

        // 放下 c 后，c 自己或与它相邻的已放建筑是否被建筑占地四面围死。
        private bool Enclosed(int c)
        {
            if (Surrounded(c))
                return true;
            foreach (int cell in perim[c])
            {
                int b = owner[cell];
                if (b >= 0 && b != c && Surrounded(b))
                    return true;
            }
            return false;
        }

        private bool Surrounded(int b)
        {
            if (alwaysOpen[b])
                return false;
            foreach (int cell in perim[b])
                if (!fp[cell])
                    return false;
            return true;
        }

        private void UseCell(int cell)
        {
            if (used[cell])
                return;
            used[cell] = true;
            if (cover[cell] > 0)
                Lose(cell);
            Push(-(cell + 1));
            foreach (int c in cellCands[cell])
            {
                if (!alive[c])
                    continue;
                alive[c] = false;
                Push(c + 1);
                foreach (int x in candCells[c])
                {
                    if (--cover[x] == 0 && !used[x])
                        Lose(x);
                }
            }
        }

        private void Lose(int cell)
        {
            usable--;
            offCount[cellOff[cell]]--;
        }

        private void Gain(int cell)
        {
            usable++;
            offCount[cellOff[cell]]++;
        }

        private int LatticeBound()
        {
            int m = int.MaxValue;
            foreach (int c in offCount)
                if (c < m)
                    m = c;
            return m;
        }

        private void Push(int op)
        {
            if (trailTop == trail.Length)
                Array.Resize(ref trail, trail.Length * 2);
            trail[trailTop++] = op;
        }

        /// <summary>把 extra 个名额优先补给超出量最少的类型后，最小超出量的上界。</summary>
        private int WaterFill(int extra)
        {
            var c = new List<int>();
            for (int t = 0; t < typeCount; t++)
                if (present[t])
                    c.Add(counts[t] - mins[t]);
            if (c.Count == 0)
                return 0;
            c.Sort();
            int level = c[0];
            int k = 1;
            while (k < c.Count)
            {
                long need = (long)(c[k] - level) * k;
                if (need > extra)
                    break;
                extra -= (int)need;
                level = c[k];
                k++;
            }
            return level + extra / k;
        }
    }
}
