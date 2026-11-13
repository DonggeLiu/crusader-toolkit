using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;

namespace TargetPriority
{
    /// <summary>
    /// 测试用探针（[Debug] ProbeThreat）：记录每次近战伤害和每支飞行物的发射，
    /// 用来核实“当前攻击对象”在哪个字段、量各兵种的攻击间隔。正式功能不依赖它。
    /// </summary>
    internal sealed unsafe class ThreatProbe : IDisposable
    {
        private readonly Action<string> log;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        /// <summary>测试用：打开时所有单位都不受伤（在伤害事件 Pre 阶段跳过原函数），用来让部队安全进场。</summary>
        public bool Invulnerable { get; set; }

        /// <summary>测试用：开打后这一方仍然不掉血（单向射击场：只量另一方杀得多快）。0 = 不用。</summary>
        public int ShieldedSide { get; set; }
        private bool Shielded(int unit) => ShieldedSide > 0 &&
            GameUnitManagerAPI.Instance.TryGetUnitById(unit, out GameUnit* u) && u != null && *(short*)((byte*)u - 0x65C + 0x6EE) == ShieldedSide;
        // 每方击杀的时刻（开打后的游戏刻）。
        private readonly List<int>[] killTimes = { null, new List<int>(), new List<int>() };

        // 每个玩家射出的箭：打中瞄准目标 / 打中别人 的次数（开打时清零）。
        public readonly int[] HitIntended = new int[16], HitOther = new int[16];

        // 落点测量：每支新箭记下射手、目标位置和瞄准点；打中时换算成“沿射击方向前后 / 左右”的格数。
        private sealed class Shot
        {
            public float Sx, Sy, Tx, Ty; public int Side, TargetHeight, Depth, Target; public bool Hit, HitTarget;
            // 逐箭记录（CSV）用：射手、出手时刻、射手高度、瞄准点、目标前方窄带人数、飞行中最后的位置与高度、打中谁。
            public string Near = "";
            // 时机诊断：决定瞄准的时刻（-1 = 不知道）、出手时目标血量、出手时空中已有多少支同方的箭飞向同一目标。
            public int Decided = -1, TargetHpAtRelease, InFlightAtRelease;
            public int Shooter, ShooterZ, Spawn, AimX, AimY, Front, LastX, LastY, Last56, Last6C, HitUnit, HitX, HitY, HitZ;
        }
        // 逐箭 CSV：每局一个编号；开打时另记一份站位。
        private System.IO.StreamWriter shotCsv, layoutCsv;
        private int duelNumber;
        public static int CurrentDuel;
        private System.IO.StreamWriter deathCsv;
        private readonly float[] centerX = new float[3], centerY = new float[3]; // 开打时每方单位的中心，用来算死者离对方多远
        private string duelMode = "";
        // 射空：射出后没打中任何人的箭；其中原定目标在箭落地前已死的单独记。
        private readonly int[] missed = new int[16], missedDeadTarget = new int[16], hitOtherDeadTarget = new int[16];
        // 伤害溢出：致命一箭打出的伤害超过目标剩余血量的部分。非致命一箭的实际掉血用来估算一箭伤害。
        private readonly int[] kills = new int[16], nonLethalHits = new int[16];
        private readonly long[] killRemaining = new long[16], nonLethalDamage = new long[16];
        private readonly long[] lethalHits = new long[16];
        // 本帧被箭打中的单位：打之前的血量（取第一箭前的）、这一帧挨了几箭、最后一箭是谁射的。下一帧再看死没死。
        private sealed class Pending { public int Before, Hits, Side, Owner, X, Y; }
        private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        private readonly HashSet<int> counted = new HashSet<int>();
        private readonly Dictionary<int, int> deathTime = new Dictionary<int, int>();
        // 落空分类（只算目标已死的落空）：出手时空中的箭已够打死目标 / 出手时不够、飞行中目标被打死。另记“决定 → 出手”间隔。
        private readonly int[] wastedOverCommitted = new int[16], wastedDiedInFlight = new int[16];
        private readonly long[] releaseDelaySum = new long[16];
        private readonly int[] releaseDelayCount = new int[16], releaseDelayLong = new int[16];
        // 按目标纵深（目标比射手看到的最近同高敌人远几格）统计：每方 [纵深] → 射出、打中人、打中目标。
        private readonly SortedDictionary<int, int[]>[] depthStats = { new SortedDictionary<int, int[]>(), new SortedDictionary<int, int[]>(), new SortedDictionary<int, int[]>() };
        private readonly List<int> unitIds = new List<int>();
        private readonly Dictionary<int, Shot> shots = new Dictionary<int, Shot>();
        private HashSet<int> seen = new HashSet<int>(), current = new HashSet<int>(); // 上一帧 / 这一帧在空中的箭
        private readonly List<int> ids = new List<int>();
        public readonly int[] Fired = new int[16];
        // 射击间隔：每个射手上一箭的时刻；每方间隔的总和与次数、开打 15 秒内射出的箭数。
        private readonly Dictionary<int, int> lastShot = new Dictionary<int, int>();
        private readonly long[] intervalSum = new long[16];
        private readonly int[] intervalCount = new int[16], earlyFired = new int[16];
        private int startTime;
        // 走动统计：每个游戏刻采样一次，每方“射手·刻”总数、其中在走路（动作 101）的，以及开始走路的次数。
        private readonly long[] shooterTicks = new long[16], walkingTicks = new long[16];
        private readonly int[] walkStarts = new int[16];
        private readonly Dictionary<int, short> lastAction = new Dictionary<int, short>();
        private readonly Dictionary<int, int> lastPosition = new Dictionary<int, int>();
        private readonly SortedDictionary<int, long>[] actionTicks = { null, new SortedDictionary<int, long>(), new SortedDictionary<int, long>() };
        private int lastSample = -1;
        // 集中程度：每刻每方弓手瞄着几个不同的目标（+0x99C），以及在瞄的弓手数；击杀时刻（开打后 15 / 30 秒内的击杀数）。
        private readonly long[] distinctTargets = new long[16], aimingShooters = new long[16], samples = new long[16];
        private readonly int[] kills15 = new int[16], kills30 = new int[16];
        private readonly HashSet<int>[] aimed = { null, new HashSet<int>(), new HashSet<int>() };
        private readonly SortedDictionary<string, int> aimOffsets = new SortedDictionary<string, int>(), hitOffsets = new SortedDictionary<string, int>();
        private int rawSamples;

        public ThreatProbe(Action<string> log)
        {
            this.log = log;
            subscriptions.Add(UnitR3EventHooks.OnUnitTakeMeleeDamage.Observable.Subscribe(e =>
            {
                if (e.Phase != EventHookPhase.Pre) return;
                if (Invulnerable) { e.SkipOriginalFunction = true; return; }
                try { log($"[probe melee] t={GameTime} {Describe(e.AttackingUnitId)} -> {Describe(e.DamagedUnitId)} dmg {e.Damage}"); }
                catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
            }));
            subscriptions.Add(UnitR3EventHooks.OnUnitTakeProjectileDamage.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre && (Invulnerable || Shielded(e.AttackedUnitId))) e.SkipOriginalFunction = true;
            }));
            // 统计箭实际打中的是不是它瞄准的目标（飞行物 +0xB4）。用 Ex 事件：只在真正结算伤害时触发一次。
            subscriptions.Add(UnitR3EventHooks.OnUnitTakeProjectileDamageEx.Observable.Subscribe(e =>
            {
                if (Invulnerable) return;
                try
                {
                    if (!GameProjectileManagerAPI.Instance.TryGetProjectileById(e.ProjectileId, out GameProjectile* q) || q == null) return;
                    int owner = (int)q->r_PlayerSourceId;
                    if (owner < 0 || owner >= 16) return;
                    if (e.Phase != EventHookPhase.Pre) return;
                    int unit = e.AttackedUnitId;
                    if (!pending.TryGetValue(unit, out Pending entry))
                    {
                        int hp = Health(unit);
                        if (hp <= 0) return;
                        pending[unit] = entry = new Pending { Before = hp };
                        if (GameUnitManagerAPI.Instance.TryGetUnitById(unit, out GameUnit* pu) && pu != null)
                        {
                            byte* pe = (byte*)pu - 0x65C;
                            entry.Owner = *(short*)(pe + 0x6EE);
                            entry.X = *(short*)(pe + 0x70E);
                            entry.Y = *(short*)(pe + 0x710);
                        }
                    }
                    entry.Hits++;
                    entry.Side = owner;
                }
                catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
            }));
            subscriptions.Add(UnitR3EventHooks.OnUnitTakeProjectileDamageEx.Observable.Subscribe(e =>
            {
                if (e.Phase != EventHookPhase.Pre || Invulnerable) return;
                try
                {
                    if (!GameProjectileManagerAPI.Instance.TryGetProjectileById(e.ProjectileId, out GameProjectile* p) || p == null) return;
                    int side = (int)p->r_PlayerSourceId;
                    if (side < 0 || side >= 16) return;
                    if (p->r_TargetUnidId == e.AttackedUnitId) HitIntended[side]++;
                    else HitOther[side]++;
                    if (shots.TryGetValue(e.ProjectileId, out Shot hitShot) && hitShot.Side == side)
                    {
                        if (!hitShot.Hit && GameUnitManagerAPI.Instance.TryGetUnitById(e.AttackedUnitId, out GameUnit* au) && au != null)
                        {
                            byte* a = (byte*)au - 0x65C;
                            hitShot.HitUnit = e.AttackedUnitId;
                            hitShot.HitX = *(short*)(a + 0x70E);
                            hitShot.HitY = *(short*)(a + 0x710);
                            hitShot.HitZ = *(short*)(a + 0x712) + *(short*)(a + 0x714);
                        }
                        hitShot.Hit = true;
                        if (p->r_TargetUnidId == e.AttackedUnitId) hitShot.HitTarget = true;
                    }
                    if (shots.TryGetValue(e.ProjectileId, out Shot s) && s.Side == side &&
                        GameUnitManagerAPI.Instance.TryGetUnitById(e.AttackedUnitId, out GameUnit* hu) && hu != null)
                    {
                        byte* h = (byte*)hu - 0x65C;
                        int height = *(short*)(h + 0x712) + *(short*)(h + 0x714);
                        string where = Math.Abs(height - s.TargetHeight) > 30 ? (height < s.TargetHeight ? " lower" : " higher") : "";
                        Count(hitOffsets, (p->r_TargetUnidId == e.AttackedUnitId ? "T " : "  ") +
                              Offset(s, *(short*)(h + 0x70E), *(short*)(h + 0x710)) + where);
                    }
                }
                catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
            }));
            subscriptions.Add(ProjectileR3EventHooks.OnProjectileSpawn.Observable.Subscribe(e =>
            {
                if (e.Phase != EventHookPhase.Post) return;
                try { log($"[probe shot] t={GameTime} {Describe(e.SourceUnitId)} type {e.ProjectileType}"); }
                catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
            }));
        }

        /// <summary>测试用：每帧扫描飞行物，记下新射出的箭（塔上射手不触发发射事件，只能这样数）。</summary>
        public void Scan()
        {
            SampleWalking();
            SettleDamage();
            GameProjectileManagerAPI projectiles = GameProjectileManagerAPI.Instance;
            ids.Clear();
            projectiles.GetAllProjectiles(ids, AliveState.IsAlive, null);
            current.Clear();
            foreach (int id in ids)
            {
                current.Add(id);
                if (seen.Contains(id))
                {
                    if (shots.TryGetValue(id, out Shot live) && projectiles.TryGetProjectileById(id, out GameProjectile* lp) && lp != null)
                    {
                        live.LastX = lp->r_SourceWorldTileX;
                        live.LastY = lp->r_SourceWorldTileY;
                        live.Last56 = *(short*)((byte*)lp + 0x56);
                        live.Last6C = *(short*)((byte*)lp + 0x6C);
                    }
                    continue;
                }
                shots.Remove(id); // 编号被重用：旧记录作废
                if (!projectiles.TryGetProjectileById(id, out GameProjectile* p) || p == null) continue;
                if (p->r_ProjectileType != ProjectileType.ArcherArrow && p->r_ProjectileType != ProjectileType.CrossbowBolt) continue;
                int side = (int)p->r_PlayerSourceId;
                if (side <= 0 || side >= 16) continue;
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(p->r_TargetUnidId, out GameUnit* tu) || tu == null) continue;
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(p->r_SourceUnitId, out GameUnit* su) || su == null) continue;
                byte* t = (byte*)tu - 0x65C, src = (byte*)su - 0x65C;
                var shot = new Shot
                {
                    Sx = *(short*)(src + 0x70E), Sy = *(short*)(src + 0x710),
                    Tx = *(short*)(t + 0x70E), Ty = *(short*)(t + 0x710),
                    Side = side, TargetHeight = *(short*)(t + 0x712) + *(short*)(t + 0x714), Target = p->r_TargetUnidId,
                };
                shot.Depth = Depth(shot, *(short*)(t + 0x6EE));
                shot.Shooter = p->r_SourceUnitId;
                shot.ShooterZ = *(short*)(src + 0x712) + *(short*)(src + 0x714);
                shot.Spawn = GameTime - startTime;
                shot.AimX = p->r_TargetWorldTileX;
                shot.AimY = p->r_TargetWorldTileY;
                shot.LastX = p->r_SourceWorldTileX;
                shot.LastY = p->r_SourceWorldTileY;
                shot.Front = FrontCount(shot, *(short*)(t + 0x6EE));
                shot.TargetHpAtRelease = *(int*)(t + 0xA20);
                foreach (var other in shots.Values)
                    if (other.Side == side && other.Target == shot.Target && !other.Hit) shot.InFlightAtRelease++;
                if (FireAllocation.LastDecision.TryGetValue(p->r_SourceUnitId, out var decision) && decision.Target == shot.Target)
                    shot.Decided = decision.Time - startTime;
                shot.Near = Nearby(shot, *(short*)(t + 0x6EE));
                shots[id] = shot;
                Fired[side]++;
                int now = GameTime;
                if (now - startTime <= 600) earlyFired[side]++;
                int source = p->r_SourceUnitId;
                if (lastShot.TryGetValue(source, out int previous) && now - previous < 1000)
                {
                    intervalSum[side] += now - previous;
                    intervalCount[side]++;
                }
                lastShot[source] = now;
                Count(aimOffsets, Offset(shot, p->r_TargetWorldTileX, p->r_TargetWorldTileY));
                if (rawSamples < 5)
                {
                    rawSamples++;
                    log($"[probe aim] shooter ({shot.Sx},{shot.Sy}) target ({shot.Tx},{shot.Ty}) h{shot.TargetHeight} aim ({p->r_TargetWorldTileX},{p->r_TargetWorldTileY})");
                }
            }
            // 消失的箭：记入纵深统计。
            foreach (int id in seen)
                if (!current.Contains(id) && shots.TryGetValue(id, out Shot gone))
                {
                    Finish(gone);
                    shots.Remove(id);
                }
            var t2 = seen; seen = current; current = t2;
        }

        private void SampleWalking()
        {
            int now = GameTime;
            if (now == lastSample) return;
            lastSample = now;
            unitIds.Clear();
            GameUnitManagerAPI.Instance.GetAllUnits(unitIds, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            aimed[1].Clear();
            aimed[2].Clear();
            foreach (int id in unitIds)
            {
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) || u == null) continue;
                byte* e = (byte*)u - 0x65C;
                int type = *(short*)(e + 0x6E6), owner = *(short*)(e + 0x6EE);
                if ((type != 22 && type != 23) || owner < 1 || owner > 2 || *(int*)(e + 0xA20) <= 0) continue;
                short aim = *(short*)(e + 0x99C);
                if (aim > 0 && Health(aim) > 0) { aimed[owner].Add(aim); aimingShooters[owner]++; }
                short action = *(short*)(e + 0x918);
                shooterTicks[owner]++;
                actionTicks[owner].TryGetValue(action, out long n);
                actionTicks[owner][action] = n + 1;
                // 移动：世界坐标与上一刻不同。
                int position = *(short*)(e + 0x70E) * 65536 + *(short*)(e + 0x710);
                bool moving = lastPosition.TryGetValue(id, out int before) && before != position;
                lastPosition[id] = position;
                if (moving) walkingTicks[owner]++;
                bool wasMoving = lastAction.TryGetValue(id, out short flag) && flag == 1;
                if (moving && !wasMoving) walkStarts[owner]++;
                lastAction[id] = (short)(moving ? 1 : 0);
            }
            for (int side = 1; side <= 2; side++)
            {
                distinctTargets[side] += aimed[side].Count;
                samples[side]++;
            }
        }

        private void SettleDamage()
        {
            foreach (var kv in pending)
            {
                Pending entry = kv.Value;
                int after = Health(kv.Key);
                if (after <= 0)
                {
                    if (!counted.Add(kv.Key)) continue;
                    deathTime[kv.Key] = GameTime - startTime;
                    kills[entry.Side]++;
                    try
                    {
                        int enemy = entry.Owner == 1 ? 2 : entry.Owner == 2 ? 1 : 0;
                        double away = enemy > 0 ? Math.Sqrt((entry.X - centerX[enemy]) * (entry.X - centerX[enemy]) + (entry.Y - centerY[enemy]) * (entry.Y - centerY[enemy])) / 8 : 0;
                        deathCsv?.WriteLine(string.Join(",", duelNumber, duelMode, GameTime - startTime, kv.Key, entry.Owner, entry.X, entry.Y, away.ToString("F1"), entry.Hits));
                    }
                    catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
                    if (entry.Side >= 1 && entry.Side <= 2) killTimes[entry.Side].Add(GameTime - startTime);
                    int elapsed = GameTime - startTime;
                    if (elapsed <= 600) kills15[entry.Side]++;
                    if (elapsed <= 1200) kills30[entry.Side]++;
                    killRemaining[entry.Side] += entry.Before;
                    lethalHits[entry.Side] += entry.Hits;
                }
                else if (entry.Hits == 1 && after < entry.Before)
                {
                    nonLethalHits[entry.Side]++;
                    nonLethalDamage[entry.Side] += entry.Before - after;
                }
            }
            pending.Clear();
        }

        private static int Health(int id) =>
            GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) && u != null ? *(int*)((byte*)u - 0x65C + 0xA20) : 0;

        private void Finish(Shot shot)
        {
            if (shot.Side < 1 || shot.Side > 2) return;
            bool targetDead = Health(shot.Target) <= 0;
            try
            {
                shotCsv?.WriteLine(string.Join(",", duelNumber, duelMode, shot.Side, shot.Spawn, GameTime - startTime, shot.Shooter,
                    shot.Sx, shot.Sy, shot.ShooterZ, shot.Target, shot.Tx, shot.Ty, shot.TargetHeight, shot.AimX, shot.AimY,
                    shot.Depth, shot.Front, shot.HitTarget ? "T" : shot.Hit ? "O" : "M", shot.HitUnit, shot.HitX, shot.HitY, shot.HitZ,
                    shot.LastX, shot.LastY, shot.Last56, shot.Last6C, targetDead ? 0 : 1, shot.Near,
                    shot.Decided, shot.TargetHpAtRelease, shot.InFlightAtRelease, deathTime.TryGetValue(shot.Target, out int died) ? died : -1));
            }
            catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
            if (shot.Decided >= 0)
            {
                int delay = shot.Spawn - shot.Decided;
                releaseDelaySum[shot.Side] += delay;
                releaseDelayCount[shot.Side]++;
                if (delay > 40) releaseDelayLong[shot.Side]++;
            }
            if (!shot.Hit && targetDead)
            {
                // 一箭约 2000：出手时空中已有的箭够打死目标就算“出手时已饱和”。
                if (shot.InFlightAtRelease * 2000 >= shot.TargetHpAtRelease) wastedOverCommitted[shot.Side]++;
                else wastedDiedInFlight[shot.Side]++;
            }
            if (!shot.Hit)
            {
                missed[shot.Side]++;
                if (targetDead) missedDeadTarget[shot.Side]++;
            }
            else if (!shot.HitTarget && targetDead) hitOtherDeadTarget[shot.Side]++;
            var table = depthStats[shot.Side];
            if (!table.TryGetValue(shot.Depth, out int[] row)) table[shot.Depth] = row = new int[3];
            row[0]++;
            if (shot.Hit) row[1]++;
            if (shot.HitTarget) row[2]++;
        }

        /// <summary>目标比射手看到的最近同伙（同一高度）远几格。</summary>
        private int Depth(Shot shot, int owner)
        {
            unitIds.Clear();
            GameUnitManagerAPI.Instance.GetAllUnits(unitIds, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            float target = (float)Math.Sqrt((shot.Tx - shot.Sx) * (shot.Tx - shot.Sx) + (shot.Ty - shot.Sy) * (shot.Ty - shot.Sy));
            float nearest = target;
            foreach (int id in unitIds)
            {
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) || u == null) continue;
                byte* e = (byte*)u - 0x65C;
                if (*(short*)(e + 0x6EE) != owner || *(int*)(e + 0xA20) <= 0) continue;
                if (Math.Abs(*(short*)(e + 0x712) + *(short*)(e + 0x714) - shot.TargetHeight) > 30) continue;
                float dx = *(short*)(e + 0x70E) - shot.Sx, dy = *(short*)(e + 0x710) - shot.Sy;
                nearest = Math.Min(nearest, (float)Math.Sqrt(dx * dx + dy * dy));
            }
            return (int)Math.Round((target - nearest) / 8);
        }

        /// <summary>相对目标的偏移：前后（正 = 目标身后，负 = 靠射手一侧）和左右，单位格（8 世界单位），四舍五入。</summary>
        private static string Offset(Shot s, float x, float y)
        {
            float dx = s.Tx - s.Sx, dy = s.Ty - s.Sy, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return "f? l?";
            float ox = x - s.Tx, oy = y - s.Ty;
            int forward = (int)Math.Round((ox * dx + oy * dy) / len / 8), lateral = (int)Math.Round((ox * dy - oy * dx) / len / 8);
            return $"f{forward:+0;-0;0} l{lateral:+0;-0;0}";
        }

        private static void Count(SortedDictionary<string, int> table, string key)
        {
            table.TryGetValue(key, out int n);
            table[key] = n + 1;
        }

        /// <summary>出手时目标周围 6 格内、同一高度的同伙（含目标）：编号:前后:左右（格，前后正 = 目标身后），分号分隔。</summary>
        private string Nearby(Shot shot, int owner)
        {
            float dx = shot.Tx - shot.Sx, dy = shot.Ty - shot.Sy, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return "";
            dx /= len; dy /= len;
            var sb = new System.Text.StringBuilder();
            foreach (int id in unitIds)
            {
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) || u == null) continue;
                byte* e = (byte*)u - 0x65C;
                if (*(short*)(e + 0x6EE) != owner || *(int*)(e + 0xA20) <= 0) continue;
                if (Math.Abs(*(short*)(e + 0x712) + *(short*)(e + 0x714) - shot.TargetHeight) > 30) continue;
                float ox = *(short*)(e + 0x70E) - shot.Tx, oy = *(short*)(e + 0x710) - shot.Ty;
                float forward = (ox * dx + oy * dy) / 8, lateral = (ox * dy - oy * dx) / 8;
                if (Math.Abs(forward) > 6 || Math.Abs(lateral) > 6) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append($"{id}:{forward:F1}:{lateral:F1}:{*(int*)(e + 0xA20)}");
            }
            return sb.ToString();
        }

        /// <summary>目标前方（朝射手）1–4 格、左右 1 格内，与目标同伙、同一高度的人数。</summary>
        private int FrontCount(Shot shot, int owner)
        {
            float dx = shot.Sx - shot.Tx, dy = shot.Sy - shot.Ty, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return 0;
            dx /= len; dy /= len;
            int n = 0;
            foreach (int id in unitIds)
            {
                if (id == shot.Target || !GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) || u == null) continue;
                byte* e = (byte*)u - 0x65C;
                if (*(short*)(e + 0x6EE) != owner || *(int*)(e + 0xA20) <= 0) continue;
                if (Math.Abs(*(short*)(e + 0x712) + *(short*)(e + 0x714) - shot.TargetHeight) > 30) continue;
                float ox = *(short*)(e + 0x70E) - shot.Tx, oy = *(short*)(e + 0x710) - shot.Ty;
                float forward = (ox * dx + oy * dy) / 8, lateral = (ox * dy - oy * dx) / 8;
                if (forward >= 0.5f && forward <= 4.5f && Math.Abs(lateral) <= 1.5f) n++;
            }
            return n;
        }

        public void ResetShots(string mode)
        {
            duelNumber++;
            duelMode = mode;
            try
            {
                string folder = BepInEx.Paths.BepInExRootPath;
                string shotPath = System.IO.Path.Combine(folder, "TargetPriority-shots.csv");
                string layoutPath = System.IO.Path.Combine(folder, "TargetPriority-layout.csv");
                bool newShots = !System.IO.File.Exists(shotPath), newLayout = !System.IO.File.Exists(layoutPath);
                shotCsv?.Dispose();
                layoutCsv?.Dispose();
                shotCsv = new System.IO.StreamWriter(shotPath, true) { AutoFlush = true };
                layoutCsv = new System.IO.StreamWriter(layoutPath, true) { AutoFlush = true };
                if (newShots)
                    shotCsv.WriteLine("duel,mode,side,spawn,end,shooter,sx,sy,sz,target,tx,ty,tz,aimx,aimy,depth,front,result,hitunit,hitx,hity,hitz,lastx,lasty,last56,last6c,target_alive,near,decided,target_hp_at_release,inflight_at_release,target_death");
                if (newLayout)
                    layoutCsv.WriteLine("duel,mode,unit,owner,type,x,y,z,hp");
                string stamp = DateTime.Now.ToString("HHmmss");
                duelNumber = int.Parse(stamp); // 每局唯一编号（时分秒）
                CurrentDuel = duelNumber;
                string deathPath = System.IO.Path.Combine(folder, "TargetPriority-deaths.csv");
                bool newDeaths = !System.IO.File.Exists(deathPath);
                deathCsv?.Dispose();
                deathCsv = new System.IO.StreamWriter(deathPath, true) { AutoFlush = true };
                if (newDeaths) deathCsv.WriteLine("duel,mode,time,unit,owner,x,y,tiles_from_enemy_center,hits_this_frame");
                Array.Clear(centerX, 0, 3);
                Array.Clear(centerY, 0, 3);
                var counts = new int[3];
                unitIds.Clear();
                GameUnitManagerAPI.Instance.GetAllUnits(unitIds, AliveState.IsAlive, null, PlayerRelationship.Any, null);
                foreach (int id in unitIds)
                {
                    if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) || u == null) continue;
                    byte* e = (byte*)u - 0x65C;
                    int owner = *(short*)(e + 0x6EE);
                    if (owner < 1 || owner > 2) continue;
                    centerX[owner] += *(short*)(e + 0x70E);
                    centerY[owner] += *(short*)(e + 0x710);
                    counts[owner]++;
                    layoutCsv.WriteLine(string.Join(",", duelNumber, mode, id, owner, *(short*)(e + 0x6E6), *(short*)(e + 0x70E), *(short*)(e + 0x710),
                        *(short*)(e + 0x712) + *(short*)(e + 0x714), *(int*)(e + 0xA20)));
                }
                for (int side = 1; side <= 2; side++)
                    if (counts[side] > 0) { centerX[side] /= counts[side]; centerY[side] /= counts[side]; }
            }
            catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }

            shots.Clear(); seen.Clear(); depthStats[1].Clear(); depthStats[2].Clear(); aimOffsets.Clear(); hitOffsets.Clear(); rawSamples = 0;
            Array.Clear(Fired, 0, 16);
            lastShot.Clear();
            Array.Clear(intervalSum, 0, 16);
            Array.Clear(intervalCount, 0, 16);
            Array.Clear(earlyFired, 0, 16);
            startTime = GameTime;
            foreach (var a in new[] { missed, missedDeadTarget, hitOtherDeadTarget, kills, nonLethalHits }) Array.Clear(a, 0, 16);
            Array.Clear(killRemaining, 0, 16);
            Array.Clear(nonLethalDamage, 0, 16);
            pending.Clear();
            counted.Clear();
            deathTime.Clear();
            FireAllocation.LastDecision.Clear();
            foreach (var a in new[] { wastedOverCommitted, wastedDiedInFlight, releaseDelayCount, releaseDelayLong }) Array.Clear(a, 0, 16);
            Array.Clear(releaseDelaySum, 0, 16);
            foreach (var a in new[] { distinctTargets, aimingShooters, samples }) Array.Clear(a, 0, 16);
            Array.Clear(kills15, 0, 16);
            killTimes[1].Clear();
            killTimes[2].Clear();
            Array.Clear(kills30, 0, 16);
            Array.Clear(lethalHits, 0, 16);
            Array.Clear(shooterTicks, 0, 16);
            Array.Clear(walkingTicks, 0, 16);
            Array.Clear(walkStarts, 0, 16);
            lastAction.Clear();
            lastPosition.Clear();
            actionTicks[1].Clear();
            actionTicks[2].Clear();
            lastSample = -1;
            // 开打前已在空中的箭不算。
            ids.Clear();
            GameProjectileManagerAPI.Instance.GetAllProjectiles(ids, AliveState.IsAlive, null);
            foreach (int id in ids) seen.Add(id);
        }

        public string SummarizeShots()
        {
            foreach (var shot in shots.Values) Finish(shot);
            shots.Clear();
            SettleDamage();
            var sb = new System.Text.StringBuilder();
            for (int side = 1; side <= 2; side++)
            {
                float perHit = nonLethalHits[side] > 0 ? (float)nonLethalDamage[side] / nonLethalHits[side] : 0;
                float overkill = lethalHits[side] * perHit - killRemaining[side];
                sb.Append($"p{side} missed {missed[side]} (target already dead {missedDeadTarget[side]}), hit someone else after target died {hitOtherDeadTarget[side]}, " +
                          $"kills {kills[side]} (by 15 s {kills15[side]}, by 30 s {kills30[side]}), " +
                          $"aiming at {(samples[side] > 0 ? (double)distinctTargets[side] / samples[side] : 0):F1} distinct targets with {(samples[side] > 0 ? (double)aimingShooters[side] / samples[side] : 0):F1} shooters, damage per hit {perHit:F0}, overkill {overkill:F0} ({(kills[side] > 0 ? overkill / kills[side] : 0):F0} per kill); ");
            }
            for (int side = 1; side <= 2; side++)
            {
                var times = killTimes[side];
                sb.Append($"p{side} kill times (s) at 5/10/15/20 kills: ");
                foreach (int n in new[] { 5, 10, 15, 20 }) sb.Append(times.Count >= n ? $"{times[n - 1] / 40.0:F1} " : "- ");
                sb.Append("; ");
            }
            sb.Append($"p1 wasted on dead targets: over-committed at release {wastedOverCommitted[1]}, target died in flight {wastedDiedInFlight[1]}; " +
                      $"p1 decision-to-release {(releaseDelayCount[1] > 0 ? releaseDelaySum[1] / (double)releaseDelayCount[1] : 0):F0} ticks avg over {releaseDelayCount[1]} shots, {releaseDelayLong[1]} over 1 s; ");
            sb.Append("depth fired/hit/target: ");
            for (int side = 1; side <= 2; side++)
            {
                sb.Append($"p{side} ");
                foreach (var kv in depthStats[side]) sb.Append($"d{kv.Key} {kv.Value[0]}/{kv.Value[1]}/{kv.Value[2]}, ");
                sb.Append("; ");
            }
            for (int side = 1; side <= 2; side++)
            {
                sb.Append($"p{side} moving {(shooterTicks[side] > 0 ? 100.0 * walkingTicks[side] / shooterTicks[side] : 0):F1}% of shooter-ticks, {walkStarts[side]} move starts, actions ");
                foreach (var kv in actionTicks[side]) sb.Append($"{kv.Key}:{kv.Value} ");
                sb.Append("; ");
            }
            sb.Append($"fired p1 {Fired[1]}, p2 {Fired[2]}; first 15 s p1 {earlyFired[1]}, p2 {earlyFired[2]}; " +
                $"mean interval p1 {(intervalCount[1] > 0 ? intervalSum[1] / intervalCount[1] : 0)} ticks, p2 {(intervalCount[2] > 0 ? intervalSum[2] / intervalCount[2] : 0)} ticks; aim offsets: ");
            foreach (var kv in aimOffsets) sb.Append($"[{kv.Key}]={kv.Value} ");
            sb.Append("; hit offsets: ");
            foreach (var kv in hitOffsets) sb.Append($"[{kv.Key}]={kv.Value} ");
            return sb.ToString();
        }

        private static string Describe(int id)
        {
            if (id <= 0 || !GameUnitManagerAPI.Instance.TryGetUnitById(id, out GameUnit* u) || u == null) return $"#{id}";
            byte* e = (byte*)u - 0x65C;
            string type = ((eChimps)(ushort)*(short*)(e + 0x6E6)).ToString().Replace("CHIMP_TYPE_", "");
            return $"{type}#{id} p{*(short*)(e + 0x6EE)} ({*(short*)(e + 0x70E) >> 3},{*(short*)(e + 0x710) >> 3}) z{*(short*)(e + 0x712)}+{*(short*)(e + 0x714)} " +
                   $"act {*(short*)(e + 0x918)} 996={*(short*)(e + 0x996)} 9F6={*(short*)(e + 0x9F6)} A36={*(short*)(e + 0xA36)} 99C={*(short*)(e + 0x99C)}";
        }

        private static int GameTime => GameData.Instance?.lastGameState?.game_time ?? 0;

        public void Dispose()
        {
            foreach (var s in subscriptions) s.Dispose();
            subscriptions.Clear();
        }
    }
}
