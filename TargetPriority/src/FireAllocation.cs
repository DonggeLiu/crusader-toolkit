using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TargetPriority
{
    /// <summary>
    /// 按打死目标所需的伤害分配火力。
    ///
    /// 每个游戏刻第一次选目标时，统计每个单位身上“已投入的伤害”：
    ///   空中的箭（飞行物 +0xB4 记着目标单位）+ 已瞄准它的射手每人下一箭，
    /// 每支按原版伤害规则（0x192750）× 估算命中率计。已投入伤害 ≥ 当前血量 × (1 + 余量) 的目标算“饱和”，
    /// 在惩罚表里给它加分，选目标时排到所有未饱和目标之后；全部饱和时选超出最少的。
    /// 同一刻里每个射手选完，立刻把它的下一箭记到新目标上，后面的射手就能看到。
    /// </summary>
    internal sealed unsafe class FireAllocation
    {
        public const int TableLength = 32768; // 惩罚表按单位编号（short）索引

        // 分数 = 距离 + 40 + 档位 × 15000（TargetTables.TierBand）+ 本表。原版分数 ≥ 400000 的目标永远不会被选（0x18E9CD）：
        // 10 档最多 150000 + 距离约 500 + 饱和 170000 + 超出 60000 = 380500，留有余量。
        // 饱和惩罚 170000 大于所有档位的跨度，所以未饱和的目标总在饱和的之前。
        private const int SaturatedPenalty = 170000, MaxSurplusPenalty = 60000;
        // 同档内按比值排序的分数跨度（大于射程内的距离差），打不动的再加 ToughPenalty 排到本档最后。
        private const float RatioRange = 1000f;
        private const int ToughPenalty = 2000;

        private const int UnitStride = 0x490;
        private const int Type = 0x6E6, Owner = 0x6EE, Z = 0x712, Building = 0x714, Action = 0x918,
            RangedTarget = 0x99C, Health = 0xA20, MaxHealth = 0xA24;
        private const int Walking = 101;

        // 伤害表（RVA，按目标兵种索引的 int）：普通箭、投石兵、贝都因长矛、其余（弩箭等）。
        private const int ArrowTable = 0x322990, StoneTable = 0x322B00, LanceTable = 0x322DE0, OtherTable = 0x322C70;
        private const int DamageTableLength = 0x5C; // 只信 0–91 号兵种
        // 玩家伤害系数：伤害 × (值 + 20) × 5 / 100（0x180560），值在玩家结构 +0x379CF04 + 玩家 × 0x583C。
        private const int PlayerDamageRva = 0x379CF04, PlayerStride = 0x583C;

        private readonly Settings settings;
        private readonly IntPtr penaltyTable = Marshal.AllocHGlobal(TableLength * 4); // 进程结束前一直保留
        private readonly float[] committed = new float[TableLength];
        private readonly List<int> touched = new List<int>();
        private readonly bool[] isTouched = new bool[TableLength];
        private readonly bool[] tough = new bool[TableLength];
        private readonly float[] threat = new float[TableLength], rate = new float[TableLength];
        private float maxRatio;
        private readonly bool[] engaged = new bool[TableLength];
        private bool engagedNow; // Threat() 的附带结果：这个近战兵正在打我方、已贴身，或预计几秒内接触
        private readonly int[] lastDistance = new int[TableLength], lastDistanceTime = new int[TableLength];
        private const float TicksPerSecond = 40f; // 实测约 40 刻/秒
        private readonly Dictionary<int, int> meleeDamage = new Dictionary<int, int>();
        private readonly List<int> ownShooters = new List<int>();
        // 每个射手当前记在哪个目标上、记了多少，重选目标时先撤掉。
        private readonly int[] shooterTarget = new int[TableLength];
        private readonly float[] shooterDamage = new float[TableLength];
        private readonly List<int> shooters = new List<int>();
        private readonly List<int> ids = new List<int>();
        // 射手兵种 → 它射出的飞行物类型（运行中从空中的飞行物学来；没见过的按普通箭算）。
        private readonly Dictionary<int, ProjectileType> projectileOf = new Dictionary<int, ProjectileType>();
        private readonly bool[,] enemies = new bool[16, 16];

        // 塔上射偏落到前排：射偏的箭大多落在目标正前方（朝射手）1–4 格、
        // 左右 1 格内的人身上。front = 这条窄带里离目标最近、同一高度的同伙；spill = 瞄这个目标时我方对 front 的每刻期望伤害。
        private readonly int[] front = new int[TableLength];
        private readonly float[] spill = new float[TableLength];
        private readonly int[] shooterFront = new int[TableLength];
        private readonly float[] shooterFrontDamage = new float[TableLength];
        private readonly Dictionary<int, int> tileUnit = new Dictionary<int, int>();
        private readonly List<int> projectileIds = new List<int>();
        // 射偏的箭（概率 = 射偏值 / 300）落在哪里（两塔对射 1087 箭实测折算）：
        // 前面有人时 22% 落在前排、3% 仍打中目标；前面没人时 17% 打中目标。
        private const float FrontShare = 0.22f, TargetShareWithFront = 0.03f, TargetShareAlone = 0.17f;
        private const int LaneTiles = 4, SameHeight = 30;
        // 射偏大的目标（塔上）不集火：箭本来就散，集火只会让同一条窄带里的人很快死光、后面的箭落空。
        // 对这类目标恢复原版的“每多一个瞄它的射手 +50 分”，前面有人挡着的再减 FrontBonus 分（优先瞄后排）。
        private const float ElevatedDeviation = 0.5f;
        private const int SpreadPerAttacker = 50, FrontBonus = 0;
        private readonly bool[] elevated = new bool[TableLength];
        private readonly int[] attackers = new int[TableLength];

        // 规划射击：射偏大的目标（塔上）按“期望击杀增量”打分。
        // 一箭瞄 aim，落到 u 身上的概率由打中核给出；u 预计还会挨的箭数 planHits[u] = 我方空中的箭与同伴下一箭落到 u 的概率之和。
        // 价值 = Σ_u P(落到 u) × P(u 恰好还差一箭)（泊松）× u 的威胁。每个射手选目标前按它自己的位置重算，写进惩罚表。
        private readonly float[] planHits = new float[TableLength];
        private readonly float[] planVariance = new float[TableLength]; // 每支箭贡献 p(1-p)：箭箭必中时方差为 0（平地），塔上接近均值
        private readonly List<KeyValuePair<int, float>>[] shooterPlan = new List<KeyValuePair<int, float>>[TableLength];
        private readonly List<int> enemyUnits = new List<int>();
        private readonly Dictionary<int, List<int>> blocks = new Dictionary<int, List<int>>(); // 每 8×8 格一块，打中核只查目标附近
        private readonly List<KeyValuePair<int, float>> kernel = new List<KeyValuePair<int, float>>();
        private readonly List<KeyValuePair<float, int>> lineUnits = new List<KeyValuePair<float, int>>();
        private const float LineFirst = 0.30f, LineSecond = 0.06f, TargetBlocked = 0.02f, TargetClear = 0.14f;
        private const float PlanPerPercent = 1000f, PlanCap = 14000f;
        public static int PlanSkippedSparse;
        public static int PlannedPicks, PlanFollowed, PlanOverridden, PlanOverrideLogs, PlanBlocked;
        /// <summary>原版的弹道检查（射手单位、目标单位）：规划只在射得到的目标里挑，否则原函数会把规划的最佳目标排除掉。</summary>
        public Func<IntPtr, IntPtr, bool> CanSee;
        /// <summary>这次选目标用了规划射击（有射偏大的候选）。只有这时才跳过原版“优先还没人瞄的目标”：
        /// 平地实测保留原版这一步更好（2026-10-09：跳过后 +4、+5，保留时 +10 到 +13）。</summary>
        public bool PlannedThisSelect;
        private int lastPlanShooter, lastPlanBest, previousTarget;
        public static int OverrideKeptPrevious;
        /// <summary>诊断：上一次被改掉的规划最佳目标（0 = 没有），供 TargetHook 复查。</summary>
        public int LastOverrideBest, LastOverrideChosen;
        private static System.IO.StreamWriter planCsv;

        private byte* image;
        private int lastTime = int.MinValue;

        // 调试统计
        public static int Rebuilds, SaturatedPicks, InFlightCounted, InFlightWithoutTarget;

        public FireAllocation(Settings settings)
        {
            this.settings = settings;
            for (int i = 0; i < TableLength; i++) Marshal.WriteInt32(penaltyTable, i * 4, 0);
        }

        public IntPtr PenaltyTable => penaltyTable;

        public void SetImage(IntPtr module) => image = (byte*)module;

        /// <summary>原函数调用前：本刻第一次时重新统计；把这个射手原来记的那一箭撤掉。</summary>
        public void BeforeSelect(IntPtr units, int shooter)
        {
            int now = GameTime;
            if (now != lastTime)
            {
                lastTime = now;
                Rebuild(units);
            }
            previousTarget = *(short*)(Unit(units, shooter) + RangedTarget);
            Withdraw(units, shooter);
            PlannedThisSelect = false;
            if (settings.PlanTowerShots) Plan(units, shooter);
        }

        /// <summary>原函数返回后：把这个射手的下一箭记到新目标上。</summary>
        /// <summary>诊断：每个射手最近一次选目标的时刻（游戏刻）和目标，探针用来算“决定 → 出手”的间隔。</summary>
        public static readonly Dictionary<int, (int Time, int Target)> LastDecision = new Dictionary<int, (int, int)>();

        public void AfterSelect(IntPtr units, int shooter)
        {
            byte* s = Unit(units, shooter);
            int target = *(short*)(s + RangedTarget);
            if (target <= 0 || target >= TableLength) return;
            LastDecision[shooter] = (GameTime, target);
            // 诊断：实际选中的是不是规划的最佳目标；不是的话记下两者的惩罚分和距离。
            if (lastPlanShooter == shooter && lastPlanBest > 0)
            {
                if (target == lastPlanBest) PlanFollowed++;
                else
                {
                    PlanOverridden++;
                    LastOverrideBest = lastPlanBest;
                    LastOverrideChosen = target;
                    if (target == previousTarget) OverrideKeptPrevious++;
                    if (settings.LogTargets && PlanOverrideLogs++ < 200)
                    {
                        byte* chosen = Unit(units, target), best = Unit(units, lastPlanBest);
                        int inPlan = planValues.FindIndex(kv => kv.Key == target);
                        TargetPriorityPlugin.Log?.LogInfo($"[plan override] shooter {shooter}: chose {target} (penalty {Marshal.ReadInt32(penaltyTable, target * 4)}, dist {Distance(s, chosen)}, in plan {inPlan >= 0}, " +
                            $"dev {Deviation(s, chosen):F2}, previous target {previousTarget}) instead of {lastPlanBest} (penalty {Marshal.ReadInt32(penaltyTable, lastPlanBest * 4)}, dist {Distance(s, best)}, hp {*(int*)(best + Health)})");
                    }
                }
                lastPlanShooter = 0;
            }
            if (Marshal.ReadInt32(penaltyTable, target * 4) > 0) SaturatedPicks++;
            if (!WillShoot(s)) return;
            Commit(units, shooter, s, target, recompute: true);
        }

        /// <summary>记下射手 s 的下一箭：按落点分配，一部分记在目标上，一部分记在目标前排。</summary>
        private void Commit(IntPtr units, int shooter, byte* s, int target, bool recompute)
        {
            byte* t = Unit(units, target);
            float shot = ShotBase(s, t, ProjectileFor(*(short*)(s + Type)));
            Split(s, t, target, out float onTarget, out float onFront);
            shooterTarget[shooter] = target;
            attackers[target]++;
            shooterDamage[shooter] = shot * onTarget;
            shooterFront[shooter] = onFront > 0 ? front[target] : 0;
            shooterFrontDamage[shooter] = shot * onFront;
            shooters.Add(shooter);
            Add(units, target, shot * onTarget, recompute);
            if (onFront > 0) Add(units, front[target], shot * onFront, recompute);
            if (settings.PlanTowerShots && Deviation(s, t) >= PlanThreshold)
            {
                Kernel(units, s, target);
                var plan = shooterPlan[shooter] ?? (shooterPlan[shooter] = new List<KeyValuePair<int, float>>());
                plan.Clear();
                foreach (var kv in kernel)
                {
                    planHits[kv.Key] += kv.Value;
                    planVariance[kv.Key] += kv.Value * (1f - kv.Value);
                    plan.Add(kv);
                }
            }
        }

        /// <summary>
        /// 打中核（tools/towersim/kernel.py，在 1927 箭上校准）：射手 s 瞄 aim，aim 同伙中每个人被打中的概率。
        /// 射击线（aim 正前方 0.4–4.5 格、左右 &lt; 0.5 格）上离射手最近的人 30%、第二个 6%，此时 aim 本人 2%；线上没人则 aim 14%。
        /// 线外的人按位置给 0.8–6%。结果放在 kernel 里。
        /// </summary>
        private void Kernel(IntPtr units, byte* s, int aim)
        {
            kernel.Clear();
            lineUnits.Clear();
            byte* a = Unit(units, aim);
            float ax = *(short*)(a + X), ay = *(short*)(a + Y);
            float dx = ax - *(short*)(s + X), dy = ay - *(short*)(s + Y), len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) { kernel.Add(new KeyValuePair<int, float>(aim, 1f)); return; }
            dx /= len; dy /= len;
            int owner = *(short*)(a + Owner), height = *(short*)(a + Z) + *(short*)(a + Building);
            int bx = (int)ax >> 6, by = (int)ay >> 6;
            float total = 0;
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    if (!blocks.TryGetValue((bx + i) * 4096 + (by + j), out var members)) continue;
                    foreach (int u in members)
                    {
                        if (u == aim) continue;
                        byte* e = Unit(units, u);
                        if (*(short*)(e + Owner) != owner || *(int*)(e + Health) <= 0) continue;
                        if (Math.Abs(*(short*)(e + Z) + *(short*)(e + Building) - height) > SameHeight) continue;
                        float ox = *(short*)(e + X) - ax, oy = *(short*)(e + Y) - ay;
                        float f = (ox * dx + oy * dy) / TileWorld, l = Math.Abs(ox * dy - oy * dx) / TileWorld;
                        if (f >= -4.5f && f <= -0.4f && l < 0.5f) { lineUnits.Add(new KeyValuePair<float, int>(f, u)); continue; }
                        float w = f >= -4.5f && f <= -0.4f && l < 1f ? 0.06f
                            : f >= -4.5f && f <= 0.5f && l < 1.5f ? 0.015f
                            : f >= -4.5f && f <= 1.5f && l < 2.5f ? 0.008f : 0f;
                        if (w > 0) { kernel.Add(new KeyValuePair<int, float>(u, w)); total += w; }
                    }
                }
            if (lineUnits.Count > 0 && settings.PlanAliveAware)
            {
                // 挡箭的人可能在这支箭落地前就被打死（他预计还会挨 planHits 箭）：按他还活着的概率接箭，
                // 死了就顺延给线上下一个；线上的人都死了，箭飞向瞄准的人（前方没人时的 14%）。
                lineUnits.Sort((p, q) => p.Key.CompareTo(q.Key));
                // none = 前面一个活人都没有的概率，one = 前面恰好一个活人的概率。
                // 第一个活着的人接 LineFirst，第二个活着的人接 LineSecond。
                float none = 1f, one = 0f;
                foreach (var line in lineUnits)
                {
                    float alive = AliveChance(units, s, line.Value);
                    float share = (LineFirst * none + LineSecond * one) * alive;
                    if (share > 0) { kernel.Add(new KeyValuePair<int, float>(line.Value, share)); total += share; }
                    one = one * (1f - alive) + none * alive;
                    none *= 1f - alive;
                }
                float noneAlive = none;
                float onAim = TargetBlocked * (1f - noneAlive) + TargetClear * noneAlive;
                kernel.Add(new KeyValuePair<int, float>(aim, onAim));
                total += onAim;
            }
            else if (lineUnits.Count > 0)
            {
                lineUnits.Sort((p, q) => p.Key.CompareTo(q.Key));
                kernel.Add(new KeyValuePair<int, float>(lineUnits[0].Value, LineFirst));
                if (lineUnits.Count > 1) kernel.Add(new KeyValuePair<int, float>(lineUnits[1].Value, LineSecond));
                kernel.Add(new KeyValuePair<int, float>(aim, TargetBlocked));
                total += LineFirst + TargetBlocked + (lineUnits.Count > 1 ? LineSecond : 0);
            }
            else
            {
                kernel.Add(new KeyValuePair<int, float>(aim, TargetClear));
                total += TargetClear;
            }
            if (total > 0.9f)
                for (int i = 0; i < kernel.Count; i++) kernel[i] = new KeyValuePair<int, float>(kernel[i].Key, kernel[i].Value * 0.9f / total);
            // 打中核是在射偏概率约 0.93 的塔对塔上测的；射偏概率 q 更低时，按 q / 0.93 的比例用它，其余的箭直接打中瞄准的人。
            float mix = Math.Min(1f, Deviation(s, a) / KernelDeviation);
            if (mix < 1f)
            {
                for (int i = 0; i < kernel.Count; i++)
                    kernel[i] = new KeyValuePair<int, float>(kernel[i].Key, kernel[i].Value * mix + (kernel[i].Key == aim ? 1f - mix : 0f));
            }
        }

        private const float KernelDeviation = 0.93f;
        /// <summary>按规划射击处理的射偏下限：平常只用于塔上（≥ 0.5）；“平地也规划”时所有目标都用。</summary>
        private float PlanThreshold => settings.PlanOnFlat ? -1f : ElevatedDeviation;

        /// <summary>规划射击：给这个射手射偏大的候选目标按“期望击杀增量”写惩罚（最好的为 0，越差分越高）。</summary>
        private void Plan(IntPtr units, int shooter)
        {
            byte* s = Unit(units, shooter);
            if (IsEnemy(settings.LocalPlayer, *(short*)(s + Owner))) return;
            ProjectileType type = ProjectileFor(*(short*)(s + Type));
            float best = 0, second = 0;
            int bestAim = 0, candidates = 0, bestRecipient = 0;
            bool anyBlocked = false;
            float bestRecipientP = 0, bestRecipientContribution = -1;
            planValues.Clear();
            foreach (int aim in enemyUnits)
            {
                byte* t = Unit(units, aim);
                if (*(int*)(t + Health) <= 0 || Deviation(s, t) < PlanThreshold || Distance(s, t) > Range(s)) continue;
                if (CanSee != null && !CanSee((IntPtr)s, (IntPtr)t)) { PlanBlocked++; continue; }
                Kernel(units, s, aim);
                // 打得准的目标（平地也规划时）不算“稀疏”：规划照样有用。
                if (lineUnits.Count > 0 || Deviation(s, t) < ElevatedDeviation) anyBlocked = true;
                float value = 0, topContribution = -1, topP = 0;
                int top = 0;
                foreach (var kv in kernel)
                {
                    byte* u = Unit(units, kv.Key);
                    float damage = ShotBase(s, u, type);
                    if (damage <= 0) continue;
                    int needed = (int)Math.Ceiling(*(int*)(u + Health) / damage);
                    float weight = threat[kv.Key] > 0 ? threat[kv.Key] : 0.01f;
                    // 箭箭必中的人（平地）：按“还差几箭”计进度，够打死后为 0；只算“恰好补死”的概率时开局人人为 0，规划开不了头。
                    float contribution = kv.Value >= 0.5f
                        ? (planHits[kv.Key] + kv.Value <= needed + 0.01f ? kv.Value * weight / Math.Max(0.5f, needed - planHits[kv.Key]) : 0f)
                        : kv.Value * HitsEqual(needed - 1, kv.Key) * weight;
                    value += contribution;
                    if (contribution > topContribution) { topContribution = contribution; top = kv.Key; topP = kv.Value; }
                }
                planValues.Add(new KeyValuePair<int, float>(aim, value));
                candidates++;
                if (value > best) { second = best; best = value; bestAim = aim; bestRecipient = top; bestRecipientP = topP; }
                else if (value > second) second = value;
            }
            // 人不够密集：所有候选前面都没人挡，规划只能直接瞄人（14%），不如原有的分散规则（收尾阶段实测）。
            if (settings.PlanNeedsBlockers && !anyBlocked)
            {
                foreach (var kv in planValues) Recompute(units, kv.Key);
                PlanSkippedSparse++;
                return;
            }
            foreach (var kv in planValues)
            {
                // 按“比最好的低百分之几”计分：每低 1% 加 PlanPerPercent 分（压过射程内的距离差），封顶低于一个档位的跨度。
                int penalty = best > 0 ? (int)Math.Min(PlanCap, (best - kv.Value) / best * 100f * PlanPerPercent) : SpreadPerAttacker * attackers[kv.Key];
                // 保留集火里的两条规则：打不动的排到本档最后；正在砍我方或快贴身的近战兵按第 1 档算。
                byte* c = Unit(units, kv.Key);
                if (tough[kv.Key]) penalty += ToughPenalty;
                if (engaged[kv.Key])
                {
                    int candidateType = *(short*)(c + Type);
                    int tier = candidateType >= 0 && candidateType < 89 ? settings.TierFor(candidateType) : TargetTables.TierCount;
                    penalty -= (tier - 1) * TargetTables.TierBand;
                }
                Marshal.WriteInt32(penaltyTable, kv.Key * 4, penalty);
                if (!isTouched[kv.Key]) { isTouched[kv.Key] = true; touched.Add(kv.Key); }
            }
            if (candidates == 0) return;
            PlannedThisSelect = true;
            PlannedPicks++;
            lastPlanShooter = shooter;
            lastPlanBest = bestAim;
            if (!settings.LogTargets) return;
            try
            {
                if (planCsv == null)
                {
                    string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "TargetPriority-plans.csv");
                    bool fresh = !System.IO.File.Exists(path);
                    planCsv = new System.IO.StreamWriter(path, true) { AutoFlush = true };
                    if (fresh) planCsv.WriteLine("duel,time,shooter,candidates,best_aim,best_value,second_value,recipient,recipient_p,recipient_expected_hits,recipient_hits_needed");
                }
                int needed = 0;
                if (bestRecipient > 0)
                {
                    float damage = ShotBase(s, Unit(units, bestRecipient), type);
                    if (damage > 0) needed = (int)Math.Ceiling(*(int*)(Unit(units, bestRecipient) + Health) / damage);
                }
                planCsv.WriteLine(string.Join(",", ThreatProbe.CurrentDuel, GameTime, shooter, candidates, bestAim, best.ToString("G4"), second.ToString("G4"),
                    bestRecipient, bestRecipientP.ToString("F3"), (bestRecipient > 0 ? planHits[bestRecipient] : 0).ToString("F2"), needed));
            }
            catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
        }

        private readonly List<KeyValuePair<int, float>> planValues = new List<KeyValuePair<int, float>>();

        /// <summary>u 在这支箭落地时还活着的概率：预计还会挨的箭数（泊松，均值 planHits[u]）少于打死他所需的箭数。</summary>
        private float AliveChance(IntPtr units, byte* s, int u)
        {
            byte* e = Unit(units, u);
            float damage = ShotBase(s, e, ProjectileFor(*(short*)(s + Type)));
            if (damage <= 0) return 1f;
            int needed = (int)Math.Ceiling(*(int*)(e + Health) / damage);
            float alive = 0;
            for (int k = 0; k < needed; k++) alive += HitsEqual(k, u);
            return Math.Min(1f, alive);
        }

        /// <summary>
        /// u 还会挨的箭数恰好为 k 的概率。均值 planHits[u]、方差 planVariance[u]：方差接近均值（塔上，每箭命中率低）用泊松；
        /// 方差明显小于均值（平地，箭箭必中）用离散化的正态分布，否则泊松会以为“已有 6 支箭也可能只中 4 箭”而继续堆箭。
        /// </summary>
        private float HitsEqual(int k, int u)
        {
            float mean = planHits[u], variance = planVariance[u];
            if (mean <= 0 || variance >= 0.7f * mean) return PoissonEquals(k, mean);
            double sigma = Math.Sqrt(variance + 0.05);
            return (float)(NormalCdf((k + 0.5 - mean) / sigma) - NormalCdf((k - 0.5 - mean) / sigma));
        }

        private static double NormalCdf(double x)
        {
            // Abramowitz–Stegun 7.1.26
            double t = 1 / (1 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2));
            double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x / 2);
            return x >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
        }

        /// <summary>泊松分布 P(N = k)，均值 lambda。</summary>
        private static float PoissonEquals(int k, float lambda)
        {
            if (k < 0) return 0;
            if (lambda <= 0) return k == 0 ? 1f : 0f;
            double log = -lambda + k * Math.Log(lambda);
            for (int i = 2; i <= k; i++) log -= Math.Log(i);
            return (float)Math.Exp(log);
        }

        private static int BlockKey(int x, int y) => (x >> 6) * 4096 + (y >> 6);

        public string Describe(IntPtr units, int target)
        {
            if (target <= 0 || target >= TableLength) return "";
            int hp = *(int*)(Unit(units, target) + Health);
            return $" [committed {committed[target]:F0}/{hp} hp, threat {threat[target]:F1}/tick, our rate {rate[target]:F1}/tick, front {front[target]} spill {spill[target]:F1}/tick, penalty {Marshal.ReadInt32(penaltyTable, target * 4)}{(tough[target] ? ", tough" : "")}{(engaged[target] ? ", engaged" : "")}]";
        }

        private void Rebuild(IntPtr units)
        {
            Rebuilds++;
            foreach (int t in touched)
            {
                committed[t] = 0;
                isTouched[t] = false;
                tough[t] = false;
                Marshal.WriteInt32(penaltyTable, t * 4, 0);
            }
            touched.Clear();
            foreach (int s in shooters)
            {
                attackers[shooterTarget[s]] = 0;
                shooterTarget[s] = shooterFront[s] = 0;
                shooterPlan[s]?.Clear();
            }
            shooters.Clear();
            BuildRelations();

            // 所有单位：我方远程兵、每格站着谁，再给每个敌人找前排。
            GameUnitManagerAPI manager = GameUnitManagerAPI.Instance;
            ids.Clear();
            manager.GetAllUnits(ids, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            int local = settings.LocalPlayer;
            ownShooters.Clear();
            tileUnit.Clear();
            enemyUnits.Clear();
            foreach (var list in blocks.Values) list.Clear();
            foreach (int id in ids)
            {
                if (id <= 0 || id >= TableLength) continue;
                byte* s = Unit(units, id);
                front[id] = 0;
                elevated[id] = false;
                planHits[id] = 0;
                planVariance[id] = 0;
                if (*(int*)(s + Health) > 0)
                {
                    int block = BlockKey(*(short*)(s + X), *(short*)(s + Y));
                    if (!blocks.TryGetValue(block, out var members)) blocks[block] = members = new List<int>();
                    members.Add(id);
                    if (IsEnemy(local, *(short*)(s + Owner))) enemyUnits.Add(id);
                }
                int owner = *(short*)(s + Owner);
                if (IsShooter(*(short*)(s + Type)) && owner > 0 && !IsEnemy(local, owner)) ownShooters.Add(id);
                int key = TileKey(*(short*)(s + X), *(short*)(s + Y));
                if (*(int*)(s + Health) > 0 && !tileUnit.ContainsKey(key)) tileUnit[key] = id;
            }
            if (ownShooters.Count > 0)
                foreach (int id in ids)
                {
                    if (id <= 0 || id >= TableLength) continue;
                    byte* t = Unit(units, id);
                    if (*(int*)(t + Health) <= 0 || !IsEnemy(local, *(short*)(t + Owner))) continue;
                    byte* nearest = Nearest(units, t, out _);
                    front[id] = FindFront(units, id, t, nearest);
                    elevated[id] = nearest != null && Deviation(nearest, t) >= ElevatedDeviation;
                    if (elevated[id]) Add(units, id, 0, recompute: false);
                }

            // 空中的箭：飞行物记着目标单位（+0xB4）。
            GameProjectileManagerAPI projectiles = GameProjectileManagerAPI.Instance;
            projectileIds.Clear();
            projectiles.GetAllProjectiles(projectileIds, AliveState.IsAlive, null);
            foreach (int id in projectileIds)
            {
                if (!projectiles.TryGetProjectileById(id, out GameProjectile* p) || p == null) continue;
                ProjectileType type = p->r_ProjectileType;
                if (!Damaging(type)) continue;
                int target = p->r_TargetUnidId, source = p->r_SourceUnitId;
                if (target <= 0 || target >= TableLength) { InFlightWithoutTarget++; continue; }
                InFlightCounted++;
                byte* t = Unit(units, target);
                if (*(int*)(t + Health) <= 0 || !IsEnemy((int)p->r_PlayerSourceId, *(short*)(t + Owner))) continue;
                if (source > 0 && source < TableLength) projectileOf[*(short*)(Unit(units, source) + Type)] = type;
                // 仰射减伤用飞行物自己记的高度（0x192D06：+0x6C 低于 +0x56 − 30 时减伤）。
                bool upward = *(short*)((byte*)p + 0x6C) < *(short*)((byte*)p + 0x56) - 30;
                float damage = BaseDamage(type, (int)p->r_PlayerSourceId, t, upward);
                float onTarget = 1, onFront = 0;
                if (source > 0 && source < TableLength) Split(Unit(units, source), t, target, out onTarget, out onFront);
                // 规划射击：我方空中的箭按打中核摊到目标附近的人身上。
                if (settings.PlanTowerShots && source > 0 && source < TableLength && !IsEnemy(local, (int)p->r_PlayerSourceId))
                {
                    byte* src = Unit(units, source);
                    if (*(int*)(src + Health) > 0 && Deviation(src, t) >= PlanThreshold)
                    {
                        Kernel(units, src, target);
                        foreach (var kv in kernel) { planHits[kv.Key] += kv.Value; planVariance[kv.Key] += kv.Value * (1f - kv.Value); }
                    }
                }
                Add(units, target, damage * onTarget, recompute: false);
                if (onFront > 0) Add(units, front[target], damage * onFront, recompute: false);
            }

            // 已瞄准目标、还没射出的下一箭。
            foreach (int id in ids)
            {
                if (id <= 0 || id >= TableLength) continue;
                byte* s = Unit(units, id);
                // 受了伤的单位也要算集中火力的加分，即使现在没人瞄它。
                int hp = *(int*)(s + Health);
                if (hp > 0 && hp < *(int*)(s + MaxHealth)) Add(units, id, 0, recompute: false);
                int type = *(short*)(s + Type);
                if (!IsShooter(type)) continue;
                // 走路中的射手不放箭（行进中照样会选目标），不算它的下一箭，否则会挡住别人去打它瞄的目标。
                if (!WillShoot(s)) continue;
                int target = *(short*)(s + RangedTarget);
                if (target <= 0 || target >= TableLength) continue;
                byte* t = Unit(units, target);
                if (*(int*)(t + Health) <= 0 || !IsEnemy(*(short*)(s + Owner), *(short*)(t + Owner))) continue;
                Commit(units, id, s, target, recompute: false);
            }

            // 每个敌人：它对我方每刻的威胁，我方射程内的远程兵对它每刻的有效伤害，以及“打不动”。
            maxRatio = 0;
            if (ownShooters.Count > 0)
                foreach (int id in ids)
                {
                    if (id <= 0 || id >= TableLength) continue;
                    byte* t = Unit(units, id);
                    int hp = *(int*)(t + Health);
                    threat[id] = rate[id] = spill[id] = 0;
                    engaged[id] = false;
                    if (hp <= 0 || !IsEnemy(local, *(short*)(t + Owner))) continue;
                    float volley = 0, kill = 0, spilled = 0;
                    byte* nearest = null;
                    int nearestDistance = int.MaxValue;
                    foreach (int s in ownShooters)
                    {
                        byte* sp = Unit(units, s);
                        float shot = ShotBase(sp, t, ProjectileFor(*(short*)(sp + Type)));
                        Split(sp, t, id, out float onTarget, out float onFront);
                        volley += shot * (onTarget + onFront); // 整轮落到它和前排的伤害
                        int d = Distance(sp, t);
                        if (d <= Range(sp))
                        {
                            kill += shot * onTarget / Interval(*(short*)(sp + Type));
                            spilled += shot * onFront / Interval(*(short*)(sp + Type));
                        }
                        if (d < nearestDistance) { nearestDistance = d; nearest = sp; }
                    }
                    rate[id] = kill;
                    spill[id] = spilled;
                    engagedNow = false;
                    threat[id] = Threat(units, t, nearest, nearestDistance, local);
                    engaged[id] = engagedNow;
                    if (engagedNow) Add(units, id, 0, recompute: false);
                    tough[id] = volley < hp * settings.ToughVolleyFraction;
                    if (threat[id] > 0 || tough[id]) Add(units, id, 0, recompute: false);
                }
            foreach (int id in ids)
                if (id > 0 && id < TableLength && *(int*)(Unit(units, id) + Health) > 0 && IsEnemy(local, *(short*)(Unit(units, id) + Owner)))
                    maxRatio = Math.Max(maxRatio, Value(units, id, afterCommitted: false));

            foreach (int t in touched) Recompute(units, t);
        }

        private void Withdraw(IntPtr units, int shooter)
        {
            if (shooter <= 0 || shooter >= TableLength) return;
            var plan = shooterPlan[shooter];
            if (plan != null)
            {
                foreach (var kv in plan)
                {
                    planHits[kv.Key] = Math.Max(0, planHits[kv.Key] - kv.Value);
                    planVariance[kv.Key] = Math.Max(0, planVariance[kv.Key] - kv.Value * (1f - kv.Value));
                }
                plan.Clear();
            }
            int target = shooterTarget[shooter];
            if (target <= 0) return;
            shooterTarget[shooter] = 0;
            if (attackers[target] > 0) attackers[target]--;
            committed[target] -= shooterDamage[shooter];
            if (committed[target] < 0) committed[target] = 0;
            Recompute(units, target);
            int f = shooterFront[shooter];
            if (f <= 0) return;
            shooterFront[shooter] = 0;
            committed[f] -= shooterFrontDamage[shooter];
            if (committed[f] < 0) committed[f] = 0;
            Recompute(units, f);
        }

        /// <summary>
        /// 打这个目标的收益：威胁 × 我方对它的每刻伤害 ÷ 剩余血量（= 威胁 ÷ 打死它要的时间），
        /// 加上射偏落到前排的部分（前排的威胁 × 落到它身上的每刻伤害 ÷ 它的剩余血量）。
        /// </summary>
        private float Value(IntPtr units, int target, bool afterCommitted)
        {
            float value = threat[target] * rate[target] / Left(units, target, afterCommitted);
            int f = front[target];
            if (f > 0 && spill[target] > 0) value += threat[f] * spill[target] / Left(units, f, afterCommitted);
            return value;
        }

        private float Left(IntPtr units, int target, bool afterCommitted)
        {
            int hp = *(int*)(Unit(units, target) + Health);
            return Math.Max(1f, afterCommitted ? hp - committed[target] : hp);
        }

        private void Add(IntPtr units, int target, float damage, bool recompute = true)
        {
            if (!isTouched[target])
            {
                isTouched[target] = true;
                touched.Add(target);
            }
            committed[target] += damage;
            if (recompute) Recompute(units, target);
        }

        private void Recompute(IntPtr units, int target)
        {
            byte* t = Unit(units, target);
            int hp = *(int*)(t + Health), max = *(int*)(t + MaxHealth);
            float need = hp * (1f + settings.FireMargin);
            int penalty = 0;
            if (hp > 0 && elevated[target])
                penalty = SpreadPerAttacker * attackers[target] - (front[target] > 0 ? FrontBonus : 0);
            else if (hp > 0 && committed[target] >= need)
            {
                float surplus = committed[target] / need - 1f; // 超出部分，越少越先选
                penalty = SaturatedPenalty + (int)Math.Min(MaxSurplusPenalty, surplus * 50000f);
            }
            else if (hp > 0)
            {
                // 同档内：威胁 × 我方对它每刻的伤害 ÷ 剩余血量（= 威胁 ÷ 打死它要的时间），越高越先打。
                // 最高的减 RatioRange 分，压过距离（射程内最多约 470）；比值为 0 的按距离排。
                float ratio = Value(units, target, afterCommitted: true);
                if (maxRatio > 0 && ratio > 0) penalty = -(int)(RatioRange * Math.Min(1f, ratio / maxRatio));
                if (tough[target]) penalty += ToughPenalty;
                // 正在砍我方或已贴身的近战兵：不管原来几档，按第 1 档算（打分代码加的是 档位 × TierBand）。
                if (engaged[target])
                {
                    int type = *(short*)(t + Type);
                    int tier = type >= 0 && type < 89 ? settings.TierFor(type) : TargetTables.TierCount;
                    penalty -= (tier - 1) * TargetTables.TierBand;
                }
            }
            Marshal.WriteInt32(penaltyTable, target * 4, penalty);
        }

        /// <summary>射手对目标的一箭打中时的伤害（未乘命中率）。</summary>
        private float ShotBase(byte* s, byte* t, ProjectileType type)
        {
            int launch = *(short*)(s + Z) + *(short*)(s + Building) + 30;
            int aim = *(short*)(t + Z) + *(short*)(t + Building) + 30;
            return BaseDamage(type, *(short*)(s + Owner), t, launch < aim - 30);
        }

        /// <summary>射手 s 瞄 target 的一箭：打中目标的概率、落到目标前排的概率。</summary>
        private void Split(byte* s, byte* t, int target, out float onTarget, out float onFront)
        {
            float q = Deviation(s, t);
            bool hasFront = front[target] > 0;
            onTarget = 1f - q + q * (hasFront ? TargetShareWithFront : TargetShareAlone);
            onFront = hasFront ? q * FrontShare : 0;
            if (*(short*)(t + Action) == Walking) onTarget *= settings.MovingHitFactor;
        }

        /// <summary>目标前排：从目标朝 toward 方向 1–4 格、左右 1 格内，第一个和目标同伙、同一高度的单位。</summary>
        private int FindFront(IntPtr units, int id, byte* t, byte* toward)
        {
            if (toward == null) return 0;
            float dx = *(short*)(toward + X) - *(short*)(t + X), dy = *(short*)(toward + Y) - *(short*)(t + Y);
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return 0;
            dx /= len; dy /= len;
            int owner = *(short*)(t + Owner), height = *(short*)(t + Z) + *(short*)(t + Building);
            for (int k = 1; k <= LaneTiles; k++)
                for (int side = 0; side < 3; side++)
                {
                    int lateral = side == 0 ? 0 : side == 1 ? -1 : 1;
                    int x = (int)Math.Round(*(short*)(t + X) + (dx * k - dy * lateral) * TileWorld);
                    int y = (int)Math.Round(*(short*)(t + Y) + (dy * k + dx * lateral) * TileWorld);
                    if (!tileUnit.TryGetValue(TileKey(x, y), out int u) || u == id) continue;
                    byte* f = Unit(units, u);
                    int fOwner = *(short*)(f + Owner);
                    if (fOwner <= 0 || IsEnemy(owner, fOwner) || *(int*)(f + Health) <= 0) continue;
                    if (Math.Abs(*(short*)(f + Z) + *(short*)(f + Building) - height) > SameHeight) continue;
                    return u;
                }
            return 0;
        }

        private byte* Nearest(IntPtr units, byte* t, out int distance)
        {
            byte* nearest = null;
            distance = int.MaxValue;
            foreach (int s in ownShooters)
            {
                byte* sp = Unit(units, s);
                int d = Distance(sp, t);
                if (d < distance) { distance = d; nearest = sp; }
            }
            return nearest;
        }

        private static int TileKey(int x, int y) => (x >> 3) * 4096 + (y >> 3);

        /// <summary>按原版 0x192750：查伤害表 → 玩家系数 → 器械减半 → 仰射减伤。少数特殊情况（梯兵、部分兵种的设置系数）不计。</summary>
        private float BaseDamage(ProjectileType type, int owner, byte* t, bool upward)
        {
            int targetType = *(short*)(t + Type);
            if (image == null || targetType < 0 || targetType >= DamageTableLength) return 0;
            int table = type == ProjectileType.ArcherArrow ? ArrowTable
                : type == ProjectileType.SlingerStone ? StoneTable
                : type == ProjectileType.BedouinLance ? LanceTable : OtherTable;
            long damage = *(int*)(image + table + targetType * 4);
            if (owner >= 0 && owner < 16)
                damage = (*(int*)(image + PlayerDamageRva + owner * PlayerStride) + 20) * damage * 5 / 100;
            if (IsSiege(targetType)) damage /= 2;
            if (upward) damage = type == ProjectileType.BallistaBolt ? damage / 2 : damage / 3;
            return damage;
        }

        /// <summary>
        /// 射偏概率：射偏值 = 目标的（地面高 + 建筑高）− 射手的地面高
        /// （射手所在建筑不算；> 75 再加一半，上限 280），概率 = 射偏值 / 300。
        /// </summary>
        private static float Deviation(byte* s, byte* t)
        {
            int diff = *(short*)(t + Z) + *(short*)(t + Building) - *(short*)(s + Z);
            if (diff <= 0) return 0;
            int deviation = diff > 75 ? diff + diff / 2 : diff;
            if (deviation > 280) deviation = 280;
            return deviation / 300f;
        }

        /// <summary>敌人打我方的命中率（不看前排）：没射偏的全中，射偏的有 TargetShareAlone 仍打中。目标在走动时再乘可配置系数。</summary>
        private float HitChance(byte* s, byte* t)
        {
            float q = Deviation(s, t);
            float hit = 1f - q + q * TargetShareAlone;
            if (*(short*)(t + Action) == Walking) hit *= settings.MovingHitFactor;
            return hit;
        }

        /// <summary>
        /// 敌人 t 对我方每刻的威胁（期望伤害）。r 是离它最近的我方远程兵，distance 是两者的切比雪夫距离（世界单位）。
        /// 远程：r 在它射程内才算，按它对 r 的一发伤害 × 命中率 ÷ 发射间隔。散射机按每发打死一个弓手估。
        /// 近战：正在打我方（+0x996 指向我方单位）或已贴身（2 格内、同在地面）算满额；正朝我方走、15 格内按距离打折；其余为 0。
        /// </summary>
        private float Threat(IntPtr units, byte* t, byte* r, int distance, int local)
        {
            if (r == null) return 0;
            int type = *(short*)(t + Type);
            if (type == Mangonel) return distance <= Range(t) + 160 ? 10000f / 137f : 0;
            if (IsShooter(type) || IsSiegeWeapon(type))
            {
                if (distance > Range(t)) return 0;
                ProjectileType p = ProjectileFor(type);
                int launch = *(short*)(t + Z) + *(short*)(t + Building) + 30;
                int aim = *(short*)(r + Z) + *(short*)(r + Building) + 30;
                return BaseDamage(p, *(short*)(t + Owner), r, launch < aim - 30) * HitChance(t, r) / Interval(type);
            }
            int victim = *(short*)(t + MeleeTarget);
            bool engaged = victim > 0 && victim < TableLength && IsEnemy(*(short*)(t + Owner), *(short*)(Unit(units, victim) + Owner));
            bool grounded = *(short*)(t + Building) == 0 && *(short*)(r + Building) == 0;
            float full = Melee(type, *(short*)(r + Type));
            if (engaged || (grounded && distance <= 2 * TileWorld))
            {
                engagedNow = true;
                return full;
            }
            // 预计几秒内接触：按两次统计之间离最近我方射手的距离变化估逼近速度。
            int id = (int)(((byte*)t - (byte*)units) / UnitStride);
            float closing = 0;
            if (lastDistanceTime[id] != 0 && lastTime > lastDistanceTime[id])
                closing = (lastDistance[id] - distance) / (float)(lastTime - lastDistanceTime[id]);
            lastDistance[id] = distance;
            lastDistanceTime[id] = lastTime;
            if (grounded && closing > 0 && (distance - 2 * TileWorld) / closing <= settings.MeleeWarnSeconds * TicksPerSecond)
            {
                engagedNow = true;
                return full;
            }
            if (grounded && *(short*)(t + Action) == Walking && distance <= ApproachTiles * TileWorld)
                return full * (1f - (float)distance / (ApproachTiles * TileWorld));
            return 0;
        }

        private float Melee(int attacker, int victim)
        {
            int key = attacker * 256 + victim;
            if (!meleeDamage.TryGetValue(key, out int damage))
            {
                try { damage = GameUnitManagerAPI.Instance.GetMeleeDamageFromTo((eChimps)(ushort)attacker, (eChimps)(ushort)victim); }
                catch { damage = 0; }
                meleeDamage[key] = damage;
            }
            return damage; // 近战每刻结算（伤害事件每刻触发）
        }

        // 发射间隔（刻，2026-10-07 实测）：弓手 129、弩手 228、散射机 137；其余暂按 150。
        private static float Interval(int type) => type == 22 || type == 70 || type == 74 ? 129f : type == 23 ? 228f : type == Mangonel ? 137f : 150f;

        // 射程（世界单位）：432，站在建筑上加建筑高度（塔上弓手能打到 550 以外）。
        private static int Range(byte* u) => 432 + *(short*)(u + Building);

        private static int Distance(byte* a, byte* b) =>
            Math.Max(Math.Abs(*(short*)(a + X) - *(short*)(b + X)), Math.Abs(*(short*)(a + Y) - *(short*)(b + Y)));

        private const int X = 0x70E, Y = 0x710, MeleeTarget = 0x996, TileWorld = 8, ApproachTiles = 15, Mangonel = 41;

        // 会自己射击的器械（投石车、投石机、散射机、弩炮、巨火弩）。
        private static bool IsSiegeWeapon(int type) => type == 39 || type == 40 || type == 41 || type == 61 || type == 77;

        // 站着（动作 0、1、4）或正在拉弓、瞄准（5、6，2026-10-09 实测弓手对射时大部分时间在 6）才放箭；
        // 弓骑兵边走边射，不论动作都算。原来漏了 5、6：射手决定目标后约 2.5 秒才放箭，这段时间他的预留在重算时消失，
        // 别人看不到这支箭，平地 40 对 40 有 26 支箭在出手时目标已够死。
        private static bool WillShoot(byte* s)
        {
            short action = *(short*)(s + Action);
            return action == 0 || action == 1 || action == 4 || action == 5 || action == 6 || *(short*)(s + Type) == HorseArcher;
        }
        private const int HorseArcher = 74;

        private ProjectileType ProjectileFor(int shooterType) =>
            projectileOf.TryGetValue(shooterType, out ProjectileType type) ? type
            : shooterType == 23 ? ProjectileType.CrossbowBolt
            : shooterType == 72 ? ProjectileType.SlingerStone
            : ProjectileType.ArcherArrow;

        private void BuildRelations()
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            for (int a = 0; a < 16; a++)
                for (int b = 0; b < 16; b++)
                    enemies[a, b] = a != b && a > 0 && b > 0 && players.IsPlayerIdValid(a) && players.IsPlayerIdValid(b)
                                    && !players.IsPlayerAlliedTo(a, b);
        }

        private bool IsEnemy(int a, int b) => a >= 0 && a < 16 && b >= 0 && b < 16 && enemies[a, b];

        // 自动选目标的远程部队（与 Settings 里“远程部队”同一组）。
        private static bool IsShooter(int type) =>
            type == 22 || type == 23 || type == 70 || type == 72 || type == 74 || type == 76 || type == 81 || type == 82 || type == 83;

        // 伤害减半的器械类目标（0x1929EA 起的判断）。
        private static bool IsSiege(int type) =>
            type == 0x27 || type == 0x28 || type == 0x29 || type == 0x3A || type == 0x3B || type == 0x3C || type == 0x3D || type == 0x4D;

        private static bool Damaging(ProjectileType type)
        {
            switch (type)
            {
                case ProjectileType.ArcherArrow:
                case ProjectileType.CrossbowBolt:
                case ProjectileType.SlingerStone:
                case ProjectileType.BedouinLance:
                case ProjectileType.ArabGrenadierOrBedouinAmbusherOrDiseaseCloud:
                case ProjectileType.GrenadierGrenade:
                case ProjectileType.UnkJavelin39:
                    return true;
                default:
                    return false;
            }
        }

        private static byte* Unit(IntPtr units, int index) => (byte*)units + (long)index * UnitStride;

        private static int GameTime => GameData.Instance?.lastGameState?.game_time ?? 0;
    }
}
