using SHCDESE.API;
using SHCDESE.Interop;
using System;
using System.Collections.Generic;

namespace GranaryOverview
{
    /// <summary>
    /// 统计食物链上每座建筑每月实际送进仓库的产品和取走的原料（做法同 Building Production）：
    /// 工人每送一份货或取一份原料，扩展器触发一次工人事件（只带 UnitId），同一帧仓库本身触发一次 OnGoodsyardAddGood。
    /// 两者在同一帧内配对：工人 → 所属生产建筑，仓库增减 → 物资和数量。
    /// 市场买入的食物没有工人事件，不会被算作产量。事件可能来自游戏线程，所以内部加锁。
    /// </summary>
    internal sealed class FoodLedger
    {
        public const int Wheat = 9, Bread = 10, Cheese = 11, Meat = 12, Apples = 13, Flour = 16;
        public const int HunterType = 7, WheatFarmType = 30, AppleFarmType = 32, DairyType = 33, MillType = 34, BakeryType = 17;

        // 库房、粮仓。
        private static readonly int[] StorageTypes = { 10, 19 };

        private static readonly Dictionary<int, (int[] Inputs, int[] Outputs)> Chain = new Dictionary<int, (int[], int[])>
        {
            [HunterType] = (new int[0], new[] { Meat }),
            [WheatFarmType] = (new int[0], new[] { Wheat }),
            [AppleFarmType] = (new int[0], new[] { Apples }),
            [DairyType] = (new int[0], new[] { Cheese }),
            [MillType] = (new[] { Wheat }, new[] { Flour }),
            [BakeryType] = (new[] { Flour }, new[] { Bread }),
        };

        /// <summary>产量按最近这么多个月（游戏刻）内的记录计算。</summary>
        public const int WindowMonths = 3;

        /// <summary>统计时长至少达到一个月的这个比例，估算才可靠。</summary>
        public const double MinFraction = 0.25;

        public Action<string> Log;

        /// <summary>当前游戏时间（PlayState.game_time）。</summary>
        public Func<int> GameTime = () => 0;

        private readonly object sync = new object();

        private struct Entry
        {
            public int Time, Good, Amount;
        }

        private sealed class Record
        {
            public int Type;
            public int Since; // 开始统计的时刻（对局中新建的建筑为建成时刻）
            public readonly List<Entry> Entries = new List<Entry>();
        }

        private readonly Dictionary<int, Record> records = new Dictionary<int, Record>();
        private readonly Dictionary<int, int> builtTime = new Dictionary<int, int>();
        private int startTime = -1;

        private struct PendingWorker
        {
            public int BuildingId, Type;
            public bool Used;
        }

        private struct PendingStorage
        {
            public int Good, Amount;
            public bool Used;
        }

        private readonly List<PendingWorker> workers = new List<PendingWorker>();
        private readonly List<PendingStorage> storages = new List<PendingStorage>();

        public void NextFrame()
        {
            lock (sync)
            {
                workers.Clear();
                storages.Clear();
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                records.Clear();
                builtTime.Clear();
                startTime = GameTime();
            }
        }

        public void OnBuildingBuilt(int buildingId)
        {
            lock (sync)
            {
                builtTime[buildingId] = GameTime();
                records.Remove(buildingId);
            }
        }

        public void OnWorkerEvent(int unitId)
        {
            if (!TryGetWorkerBuilding(unitId, out int buildingId, out int type) || !Chain.TryGetValue(type, out var goods))
                return;

            lock (sync)
            {
                for (int i = storages.Count - 1; i >= 0; i--)
                {
                    PendingStorage st = storages[i];
                    if (st.Used || !Matches(goods, st.Good, st.Amount))
                        continue;
                    st.Used = true;
                    storages[i] = st;
                    Credit(buildingId, type, st.Good, st.Amount);
                    return;
                }
                workers.Add(new PendingWorker { BuildingId = buildingId, Type = type });
            }
        }

        /// <summary>OnGoodsyardAddGood（Pre）：amount 为负表示取走。</summary>
        public void OnGoodsyardAdd(int buildingId, int good, int amount)
        {
            if (amount == 0 || !TryGetLocalBuilding(buildingId, out int type) || Array.IndexOf(StorageTypes, type) < 0)
                return;

            lock (sync)
            {
                for (int i = workers.Count - 1; i >= 0; i--)
                {
                    PendingWorker w = workers[i];
                    if (w.Used || !Matches(Chain[w.Type], good, amount))
                        continue;
                    w.Used = true;
                    workers[i] = w;
                    Credit(w.BuildingId, w.Type, good, amount);
                    return;
                }
                storages.Add(new PendingStorage { Good = good, Amount = amount });
            }
        }

        private static bool Matches((int[] Inputs, int[] Outputs) goods, int good, int amount)
        {
            return amount < 0 ? Array.IndexOf(goods.Inputs, good) >= 0 : Array.IndexOf(goods.Outputs, good) >= 0;
        }

        private void Credit(int buildingId, int type, int good, int amount)
        {
            int now = GameTime();
            if (!records.TryGetValue(buildingId, out Record record) || record.Type != type)
            {
                record = new Record { Type = type, Since = builtTime.TryGetValue(buildingId, out int built) ? built : startTime };
                records[buildingId] = record;
            }
            record.Entries.Add(new Entry { Time = now, Good = good, Amount = amount });
            Log?.Invoke($"credit t{now} building={buildingId} type={type} good={good} amount={amount}");
        }

        /// <summary>
        /// 某类建筑的月产出（物资编号 → 每月数量，原料为负）和其中有产出的建筑数：
        /// 最近 WindowMonths 个月（不足则从开始统计算起）内的记录 ÷ 时长 × 每月刻数。
        /// </summary>
        public (Dictionary<int, double> PerMonth, int Buildings) Rates(int type, double ticksPerMonth)
        {
            var perMonth = new Dictionary<int, double>();
            int buildings = 0;
            int now = GameTime();
            int window = (int)(WindowMonths * ticksPerMonth);
            lock (sync)
            {
                foreach (Record record in records.Values)
                {
                    if (record.Type != type)
                        continue;
                    int from = Math.Max(record.Since, now - window);
                    record.Entries.RemoveAll(e => e.Time < now - window);
                    double span = now - from;
                    if (span < MinFraction * ticksPerMonth)
                        continue;

                    var sums = new Dictionary<int, double>();
                    foreach (Entry e in record.Entries)
                        if (e.Time >= from)
                            sums[e.Good] = (sums.TryGetValue(e.Good, out double v) ? v : 0) + e.Amount;

                    bool produced = false;
                    foreach (KeyValuePair<int, double> t in sums)
                    {
                        double rate = t.Value / span * ticksPerMonth;
                        perMonth[t.Key] = (perMonth.TryGetValue(t.Key, out double p) ? p : 0) + rate;
                        produced |= rate > 0;
                    }
                    if (produced)
                        buildings++;
                }
            }
            return (perMonth, buildings);
        }

        /// <summary>开始统计后是否已过了足够长的时间。</summary>
        public bool HasData(double ticksPerMonth)
        {
            lock (sync)
                return startTime >= 0 && GameTime() - startTime >= MinFraction * ticksPerMonth;
        }

        public static unsafe int CountLocal(int type)
        {
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId(), n = 0;
            foreach (int id in GameBuildingManagerAPI.Instance.GetAllAliveBuildings())
                if (GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out GameBuilding* b) && b != null &&
                    (int)b->r_BuildingType == type && b->r_PlayerIdOwner == player)
                    n++;
            return n;
        }

        private static unsafe bool TryGetWorkerBuilding(int unitId, out int buildingId, out int type)
        {
            buildingId = 0;
            type = 0;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            if (!units.IsValidId(unitId) || !units.TryGetUnitById(unitId, out GameUnit* unit) || unit == null)
                return false;
            buildingId = unit->r_LinkedProductionBuildingId;
            return buildingId > 0 && TryGetLocalBuilding(buildingId, out type);
        }

        private static unsafe bool TryGetLocalBuilding(int buildingId, out int type)
        {
            type = 0;
            if (buildingId <= 0 || !GameBuildingManagerAPI.Instance.TryGetBuildingById(buildingId, out GameBuilding* b) || b == null)
                return false;
            type = (int)b->r_BuildingType;
            return b->r_PlayerIdOwner == GamePlayerManagerAPI.Instance.GetLocalPlayerId();
        }
    }
}
