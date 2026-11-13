using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;

namespace BuildingProduction
{
    /// <summary>
    /// 按建筑统计每月的实际消耗和产出。
    /// 工人每把一份货物放进仓库（库房、粮仓、武器库、采石场的石料堆）或从仓库取走一份原料，
    /// 扩展器都会触发一次工人事件（只带 UnitId），同一帧里仓库本身触发一次 OnGoodsyardAddGood（±1，带物资）。
    /// 把两者在同一帧内配对：工人 → 所属生产建筑，仓库增减 → 物资和数量。
    /// 生产建筑自己的小仓库按“捆”计数（伐木工一趟 3 捆 = 18 块木材），不能直接用。
    /// 事件可能来自游戏线程，所以内部加锁；帧号由主线程每帧推进。
    /// </summary>
    internal sealed class ProductionLedger
    {
        // 库房、武器库、粮仓、采石场石料堆（石料堆只有牛取走的那一侧参与配对）。
        private static readonly int[] StorageTypes = { 10, 11, 19, ProductionGoods.QuarryPileType };

        public Action<string> Log;

        /// <summary>调试日志用的游戏时间（PlayState.game_time）。</summary>
        public Func<int> GameTime = () => 0;

        private readonly object sync = new object();
        private int frame;

        public const int AverageMonths = 12;

        /// <summary>统计结果里消耗（负数）记在这个键下，产出记在物资编号本身。</summary>
        public static int ConsumedKey(int good) => 1000 + good;

        private sealed class Record
        {
            public int Type;
            // 月份编号（year * 12 + month）→ 物资编号 → 数量（消耗为负）。
            public Dictionary<int, Dictionary<int, int>> ByMonth = new Dictionary<int, Dictionary<int, int>>();
        }

        private readonly Dictionary<int, Record> records = new Dictionary<int, Record>();

        // 统计开始的月份（这个月不完整）和当前月份；对局中新建的建筑记下建成的月份（那个月也不完整）。
        private int startMonth = -1;
        private int currentMonth = -1;
        private readonly Dictionary<int, int> builtMonth = new Dictionary<int, int>();

        // 石料堆的容量（建筑本身不记，取自 OnGoodsyardAddGood 的 Capacity）。
        private readonly Dictionary<int, int> pileCapacity = new Dictionary<int, int>();

        public int PileCapacity(int pileId)
        {
            lock (sync)
                return pileCapacity.TryGetValue(pileId, out int capacity) ? capacity : 0;
        }

        private struct PendingWorker
        {
            public int BuildingId;
            public int Type;
            public string Event;
            public bool Used;
        }

        private struct PendingStorage
        {
            public int Good;
            public int Amount;
            public bool Used;
        }

        // 本帧里还没配对的工人事件和仓库增减。
        private readonly List<PendingWorker> workers = new List<PendingWorker>();
        private readonly List<PendingStorage> storages = new List<PendingStorage>();

        public int Frame => frame;

        /// <summary>本地玩家某物资的实时库存（r_TotalGoods 数组按物资编号排列），仅用于调试日志。</summary>
        public static unsafe int LiveTotal(int good)
        {
            if (good < 0 || good > 24 ||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(GamePlayerManagerAPI.Instance.GetLocalPlayerId(),
                    out GamePlayerResources* resources) || resources == null)
                return -1;
            return (int)(&resources->r_TotalGoodsNull)[good];
        }

        public void NextFrame()
        {
            lock (sync)
            {
                if (Log != null)
                {
                    foreach (PendingWorker w in workers)
                        if (!w.Used)
                            Log($"[f{frame}] unpaired {w.Event} building={w.BuildingId} type={w.Type}");
                    foreach (PendingStorage st in storages)
                        if (!st.Used)
                            Log($"[f{frame}] unpaired storage good={st.Good} amount={st.Amount}");
                }

                frame++;
                workers.Clear();
                storages.Clear();
            }
        }

        public void Reset(int month)
        {
            lock (sync)
            {
                records.Clear();
                builtMonth.Clear();
                herds.Clear();
                startMonth = month;
                currentMonth = month;
            }
        }

        public void SetMonth(int month)
        {
            lock (sync)
            {
                currentMonth = month;
                // 只保留平均所需的几个月。
                foreach (Record record in records.Values)
                {
                    var old = new List<int>();
                    foreach (int key in record.ByMonth.Keys)
                        if (key < month - AverageMonths)
                            old.Add(key);
                    foreach (int key in old)
                        record.ByMonth.Remove(key);
                }
            }
        }

        /// <summary>新建筑建成（OnBuildingSpawn）：建成的那个月不算完整月。</summary>
        public void OnBuildingBuilt(int buildingId)
        {
            lock (sync)
            {
                if (currentMonth >= 0)
                    builtMonth[buildingId] = currentMonth;
                records.Remove(buildingId);
            }
        }

        /// <summary>
        /// 前几个完整月的月平均（最多 AverageMonths 个月，不足按实际月数；还没有完整月时为 null）和本月至今。
        /// 键为物资编号，消耗为负数。
        /// </summary>
        public (Dictionary<int, double> Average, Dictionary<int, int> Current) Snapshot(int buildingId, int type)
        {
            lock (sync)
            {
                int first = startMonth + 1;
                if (builtMonth.TryGetValue(buildingId, out int built))
                    first = Math.Max(first, built + 1);
                first = Math.Max(first, currentMonth - AverageMonths);
                int months = currentMonth - first;

                records.TryGetValue(buildingId, out Record record);
                if (record != null && record.Type != type)
                    record = null;

                var current = new Dictionary<int, int>();
                if (record != null && record.ByMonth.TryGetValue(currentMonth, out Dictionary<int, int> now))
                    current = new Dictionary<int, int>(now);

                if (months <= 0)
                    return (null, current);

                var average = new Dictionary<int, double>();
                if (record != null)
                {
                    for (int month = first; month < currentMonth; month++)
                    {
                        if (!record.ByMonth.TryGetValue(month, out Dictionary<int, int> totals))
                            continue;
                        foreach (KeyValuePair<int, int> entry in totals)
                        {
                            average.TryGetValue(entry.Key, out double sum);
                            average[entry.Key] = sum + entry.Value;
                        }
                    }
                }

                var keys = new List<int>(average.Keys);
                foreach (int good in keys)
                    average[good] /= months;
                return (average, current);
            }
        }

        /// <summary>
        /// 工人事件（取货、送货等，只带 UnitId）。扩展器里部分事件名与实际动作对不上（例如铁矿工人送货触发的是 PickUp），
        /// 所以不看事件名，只看同一帧仓库增减的方向：原料减少为消耗，产品增加为产出。
        /// </summary>
        public void OnWorkerEvent(string name, int unitId)
        {
            if (!TryGetWorkerBuilding(unitId, out int buildingId, out int type, out int owner) ||
                owner != GamePlayerManagerAPI.Instance.GetLocalPlayerId() ||
                !ProductionGoods.TryGet(type, out ProductionGoods goods))
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

                workers.Add(new PendingWorker { BuildingId = buildingId, Type = type, Event = name });
            }
        }

        /// <summary>OnGoodsyardAddGood（Pre）：amount 为负表示取走。</summary>
        public void OnGoodsyardAdd(int buildingId, int good, int amount, int capacity)
        {
            if (amount == 0 || !TryGetBuilding(buildingId, out int type, out int owner) ||
                owner != GamePlayerManagerAPI.Instance.GetLocalPlayerId())
                return;

            lock (sync)
            {
                // 石匠把石块放上石料堆没有工人事件：直接记在石料堆上，采石场面板读它的石料堆。
                if (type == ProductionGoods.QuarryPileType && capacity > 0)
                    pileCapacity[buildingId] = capacity;
                if (type == ProductionGoods.QuarryPileType && amount > 0)
                {
                    Credit(buildingId, type, good, amount);
                    return;
                }

                // 皮革作坊牵来一头牛，本地记 3 张牛皮（做 3 件皮甲）：按牛数记为消耗。奶牛场一侧见 PollDairies。
                if (good == ProductionGoods.Cows)
                {
                    Log?.Invoke($"[f{frame}] cows building={buildingId} type={type} amount={amount}");
                    if (type == ProductionGoods.TannerType && amount > 0)
                        Credit(buildingId, type, good, -Math.Max(1, amount / ProductionGoods.HidesPerCow));
                    return;
                }

                if (Array.IndexOf(StorageTypes, type) < 0)
                {
                    // 调试：生产建筑自己的小仓库变化（用来测生产周期）。
                    if (ProductionGoods.TryGet(type, out _))
                        Log?.Invoke($"[t{GameTime()}] local building={buildingId} type={type} good={good} amount={amount}");
                    return;
                }

                for (int i = workers.Count - 1; i >= 0; i--)
                {
                    PendingWorker w = workers[i];
                    if (w.Used || !ProductionGoods.TryGet(w.Type, out ProductionGoods goods) ||
                        !Matches(goods, good, amount))
                        continue;

                    w.Used = true;
                    workers[i] = w;
                    Credit(w.BuildingId, w.Type, good, amount);
                    return;
                }

                storages.Add(new PendingStorage { Good = good, Amount = amount });
            }
        }

        // 仓库里该建筑的原料减少或产品增加。
        private static bool Matches(ProductionGoods goods, int good, int amount)
        {
            return amount < 0
                ? Array.IndexOf(goods.Inputs, good) >= 0
                : Array.IndexOf(goods.Outputs, good) >= 0;
        }

        private void Credit(int buildingId, int type, int good, int amount)
        {
            if (!records.TryGetValue(buildingId, out Record record) || record.Type != type)
            {
                // 建筑编号会被新建筑复用：类型不同就重新开始。
                record = new Record { Type = type };
                records[buildingId] = record;
            }

            if (!record.ByMonth.TryGetValue(currentMonth, out Dictionary<int, int> totals))
            {
                totals = new Dictionary<int, int>();
                record.ByMonth[currentMonth] = totals;
            }

            // 消耗和产出分开记：系牛桩的原料和产品都是石头，合在一起会互相抵消。
            int key = amount < 0 ? ConsumedKey(good) : good;
            totals.TryGetValue(key, out int total);
            totals[key] = total + amount;
            Log?.Invoke($"[t{GameTime()}] credit building={buildingId} type={type} good={good} amount={amount} month={totals[key]}");
        }

        private static unsafe bool TryGetWorkerBuilding(int unitId, out int buildingId, out int type, out int owner)
        {
            buildingId = 0;
            type = 0;
            owner = 0;

            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            if (!units.IsValidId(unitId) || !units.TryGetUnitById(unitId, out GameUnit* unit) || unit == null)
                return false;

            buildingId = unit->r_LinkedProductionBuildingId;
            return buildingId > 0 && TryGetBuilding(buildingId, out type, out owner);
        }

        // 各奶牛场上次看到的牛数。
        private readonly Dictionary<int, int> herds = new Dictionary<int, int>();
        private readonly List<int> cowIds = new List<int>();

        /// <summary>
        /// 奶牛场的牛是单位（CHIMP_TYPE_COW，所属建筑为奶牛场），被皮革作坊牵走时没有仓库事件。
        /// 定时数每个本地奶牛场的牛，减少的数量记为奶牛场产出的牛；同时返回各奶牛场现有的牛数。
        /// </summary>
        public unsafe void PollDairies()
        {
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            var counts = new Dictionary<int, int>();
            cowIds.Clear();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(cowIds, AliveState.IsAlive, eChimps.CHIMP_TYPE_COW, PlayerRelationship.Any, null);
            foreach (int id in cowIds)
            {
                if (!units.TryGetUnitById(id, out GameUnit* cow) || cow == null)
                    continue;
                int dairy = cow->r_LinkedProductionBuildingId;
                if (dairy <= 0 || !TryGetBuilding(dairy, out int type, out int owner) ||
                    type != ProductionGoods.DairyType || owner != player)
                    continue;
                counts.TryGetValue(dairy, out int n);
                counts[dairy] = n + 1;
            }

            lock (sync)
            {
                foreach (KeyValuePair<int, int> entry in counts)
                {
                    if (herds.TryGetValue(entry.Key, out int last) && entry.Value < last)
                        Credit(entry.Key, ProductionGoods.DairyType, ProductionGoods.Cows, last - entry.Value);
                }

                // 牛全被牵走的奶牛场不在 counts 里。
                foreach (KeyValuePair<int, int> entry in herds)
                {
                    if (!counts.ContainsKey(entry.Key) && entry.Value > 0 &&
                        TryGetBuilding(entry.Key, out int type, out int owner) && type == ProductionGoods.DairyType)
                    {
                        Credit(entry.Key, ProductionGoods.DairyType, ProductionGoods.Cows, entry.Value);
                        counts[entry.Key] = 0;
                    }
                }

                herds.Clear();
                foreach (KeyValuePair<int, int> entry in counts)
                    herds[entry.Key] = entry.Value;
            }
        }

        /// <summary>当场数一个奶牛场现有的牛（面板显示存量条时用）。</summary>
        public unsafe int CountHerd(int dairyId)
        {
            cowIds.Clear();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(cowIds, AliveState.IsAlive, eChimps.CHIMP_TYPE_COW, PlayerRelationship.Any, null);
            int n = 0;
            foreach (int id in cowIds)
                if (units.TryGetUnitById(id, out GameUnit* cow) && cow != null && cow->r_LinkedProductionBuildingId == dairyId)
                    n++;
            return n;
        }

        /// <summary>采石场对应的石料堆编号。</summary>
        public static unsafe int QuarryPileId(int quarryId)
        {
            return GameBuildingManagerAPI.Instance.TryGetBuildingById(quarryId, out GameBuilding* quarry) && quarry != null
                ? quarry->r_StoneQuarry_StockPileBuildingId
                : 0;
        }

        /// <summary>建筑本地存放的某物资数量和本地存储上限（石料堆、奶牛场的牛）。</summary>
        public static unsafe (int Amount, int Max) LocalStock(int buildingId, int good)
        {
            if (buildingId <= 0 || !GameBuildingManagerAPI.Instance.TryGetBuildingById(buildingId, out GameBuilding* b) ||
                b == null || good < 0 || good > 24)
                return (0, 0);
            return ((int)(&b->r_NullAmount)[good], (int)b->r_MaxGoodStackAmount);
        }

        /// <summary>调试：建筑的存量相关字段。</summary>
        public static unsafe string DebugStock(int buildingId)
        {
            if (buildingId <= 0 || !GameBuildingManagerAPI.Instance.TryGetBuildingById(buildingId, out GameBuilding* b) || b == null)
                return "none";
            return $"type={b->r_BuildingType} stone={b->r_StoneBlocksAmount} hides={b->r_CowHidesAmount} " +
                   $"stack={b->r_CurrentGoodStackAmount}/{b->r_MaxGoodStackAmount} local={b->r_LocalStorageGoodType}";
        }

        public static bool TryGetBuildingType(int buildingId, out int type)
        {
            return TryGetBuilding(buildingId, out type, out int owner) &&
                   owner == GamePlayerManagerAPI.Instance.GetLocalPlayerId();
        }

        private static unsafe bool TryGetBuilding(int buildingId, out int type, out int owner)
        {
            type = 0;
            owner = 0;
            if (buildingId <= 0 || !GameBuildingManagerAPI.Instance.TryGetBuildingById(buildingId, out GameBuilding* building) ||
                building == null)
                return false;

            type = (int)building->r_BuildingType;
            owner = building->r_PlayerIdOwner;
            return true;
        }
    }
}
