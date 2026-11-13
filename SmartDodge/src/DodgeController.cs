using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SmartDodge
{
    /// <summary>
    /// 躲避逻辑。
    /// 实测：飞行物生成时已定好落点，敌方射手按目标“当前速度 × 飞行时间”预判；落地时离落点约 12 世界单位以内的单位才会中箭。
    /// 交战时弓骑兵平时站着射；某支已知飞行物会落到它身边（危险度 > 0.25，即落点 13.5 以内）时，走一段 2–3 格：
    /// 避开所有已知飞行物的落点和落地时刻、转向（让敌方的直线预判落空）、不离岗位太远、保持射程、不扎堆。途中有新箭就改道。
    /// 对塔测试中，这与连续游走的输出和损失相同，但移动命令少约 2/3。
    /// </summary>
    internal sealed class DodgeController
    {
        public const int WorldPerTile = 8;

        private sealed class Threat
        {
            public int Id;
            public Vector2 Source, Aim, Dir; // Dir：从发射点指向落点的单位向量
            public int Spawn;
            public float Speed;  // 地面速度（世界单位 / 刻），生成后按实际位置测量
            public bool Measured;
            public int Impact;   // 预计落地的游戏时刻
            public int Slack;    // 落地时间的不确定度（刻）
            public int FlightState = -1; // 飞行中的状态值；变了就是落地了
            public bool Landed;
            public int AtAimTicks;
            public int FirstSeenTick = -1; // 第一次读到位置的时刻和当时已飞的距离，用来测速
            public float FirstSeenDone;
        }

        /// <summary>哪些飞行物类型会伤到弓骑兵（由插件提供）。</summary>
        public Func<ProjectileType, bool> IsDamaging = _ => true;
        private readonly List<int> projectileIds = new List<int>();
        private readonly HashSet<int> ignoredProjectiles = new HashSet<int>();
        public int Discovered;

        /// <summary>
        /// 塔上射手的箭不经过扩展器的生成事件：每刻扫描飞行物表，把没登记过的敌方飞行物补上。
        /// 起点、落点、类型、所属玩家都直接读飞行物结构。
        /// </summary>
        private unsafe void DiscoverProjectiles(GameProjectileManagerAPI projectiles, int now)
        {
            projectileIds.Clear();
            projectiles.GetAllProjectiles(projectileIds, AliveState.IsAlive, null);
            if (ignoredProjectiles.Count > 4 * projectileIds.Count + 500) ignoredProjectiles.IntersectWith(projectileIds);
            int local = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            foreach (int id in projectileIds)
            {
                if (threats.ContainsKey(id) || ignoredProjectiles.Contains(id)) continue;
                if (!projectiles.TryGetProjectileById(id, out GameProjectile* p) || p == null) continue;
                var type = p->r_ProjectileType;
                int owner = (int)p->r_PlayerSourceId;
                if (!IsDamaging(type) || owner == local || owner <= 0) { ignoredProjectiles.Add(id); continue; }
                var source = new Vector2(p->r_SourceWorldTileX, p->r_SourceWorldTileY);
                var aim = new Vector2(p->r_TargetWorldTileX, p->r_TargetWorldTileY);
                OnProjectileSpawn(id, type, now, source, aim);
                Discovered++;
            }
        }

        private sealed class Dodger
        {
            public Vector2 LastPos;
            public int LastTime = -1;
            public Vector2 AnchorPos; // 位置只精确到地块（8），速度隔几刻再算
            public int AnchorTime = -1;
            public Vector2 Velocity;  // 世界单位 / 游戏刻
            public Vector2? Post;     // 岗位：交战时围绕这里游走
            public Vector2? Waypoint; // 当前这一段的终点
            public Vector2 Heading;   // 当前这一段的方向
            public int WaypointUntil; // 超过这个时刻还没到，视为被卡住
            public Vector2Int? Resume; // 玩家的移动目的地（地块）
            public int LastOrder = -1000;
            public int ResumeIssued = -1000;
            public int RestUntil;     // 站着休息到这一刻（有危险会提前走）
        }

        private struct Enemy { public int Id; public Vector2 Pos, Velocity; public bool Melee; }

        // 会远程攻击或不会主动近战的兵种：不需要风筝。
        private static readonly HashSet<int> NotMelee = new HashSet<int>
        {
            (int)eChimps.CHIMP_TYPE_ARCHER, (int)eChimps.CHIMP_TYPE_XBOWMAN, (int)eChimps.CHIMP_TYPE_ARAB_BOW,
            (int)eChimps.CHIMP_TYPE_ARAB_SLINGER, (int)eChimps.CHIMP_TYPE_ARAB_HORSEMAN, (int)eChimps.CHIMP_TYPE_ARAB_GRENADIER,
            (int)eChimps.CHIMP_TYPE_CATAPULT, (int)eChimps.CHIMP_TYPE_TREBUCHET, (int)eChimps.CHIMP_TYPE_MANGONEL,
            (int)eChimps.CHIMP_TYPE_BALLISTA, (int)eChimps.CHIMP_TYPE_ARAB_BALLISTA,
        };

        private readonly List<Enemy> enemies = new List<Enemy>();
        private readonly Dictionary<int, (Vector2 pos, int time)> enemyLast = new Dictionary<int, (Vector2, int)>();
        private readonly Dictionary<int, Vector2> enemyVelocity = new Dictionary<int, Vector2>();
        private readonly List<int> all = new List<int>();
        private readonly Dictionary<int, Threat> threats = new Dictionary<int, Threat>();
        private readonly Dictionary<int, Dodger> dodgers = new Dictionary<int, Dodger>();
        private readonly List<int> scratch = new List<int>();
        private readonly Action<string> log;

        public readonly HashSet<int> Enabled = new HashSet<int>();

        /// <summary>落地时离落点小于此值视为危险（实测 ≤12 会中）。</summary>
        public float DangerRadius = 18f;
        /// <summary>弓骑兵移动速度（世界单位 / 刻，实测约 1）和静止时接到命令后的起步刻数。</summary>
        public float HorseSpeed = 1.0f;
        public int StartDelay = 6;
        /// <summary>每段路的长度（地块）。飞行时间约 55 刻，一段约 20–30 刻，所以落地前一定已经转向。</summary>
        public int[] LegTiles = { 2, 3 };
        /// <summary>逃离近战时的段长：长一些才能真正跑起来拉开距离。</summary>
        public int[] KiteLegTiles = { 3, 5 };
        /// <summary>离路点这么近时就给下一段，保持连续奔跑。</summary>
        public float ReachDistance = WorldPerTile * 1.5f;
        /// <summary>围绕岗位游走的半径（世界单位）。</summary>
        public float LeashRadius = 5 * WorldPerTile;
        /// <summary>离玩家目的地这么近就算到了。</summary>
        public float ArrivedRadius = 3 * WorldPerTile;
        /// <summary>为了避开新来的箭而改道时，两次命令至少间隔的刻数。</summary>
        public int MinOrderGap = 6;
        /// <summary>当前路线的危险度超过这个值才改道（0.25 ≈ 会进入落点 13.5 以内）。</summary>
        public float ReplanDanger = 0.25f;
        /// <summary>每走完一段原地站多少刻（期间有危险会提前走）。Hold = 一直站到有危险；0 = 连续游走。</summary>
        public int RestTicks = Hold;
        public const int Hold = 1_000_000;
        /// <summary>站着（或按玩家命令行进）时，危险度超过这个值才走（0.25 ≈ 会进入落点 13.5 以内）。</summary>
        public float RestDanger = 0.25f;
        /// <summary>近战敌人在此距离内且在逼近时开始风筝。</summary>
        public float KiteTrigger = 7 * WorldPerTile;
        /// <summary>近战敌人在此距离内，不管是否逼近都要离开。</summary>
        public float KiteClose = 3 * WorldPerTile;
        /// <summary>希望与近战敌人保持的距离。</summary>
        public float KiteSafe = 8 * WorldPerTile;
        /// <summary>路线离近战敌人至少这么远，避免从旁边穿过被拖住。</summary>
        public float PathClearance = 2 * WorldPerTile;
        /// <summary>弓骑兵射程（世界单位）：保持至少一个敌人在射程内；0 = 不考虑。</summary>
        public float RangeWorld;
        /// <summary>与最近的敌方射手至少保持自身射程的这个比例，保证来箭有足够的飞行时间可躲。</summary>
        public float StandOffFraction = 0.75f;
        public bool KiteMelee = true;
        public bool Verbose;
        public int Dodges, Kites;

        /// <summary>自己下移动命令时置位，用来区分玩家命令。</summary>
        public bool IssuingOwnOrder { get; private set; }

        public DodgeController(Action<string> log) { this.log = log; }

        public void Clear()
        {
            threats.Clear();
            dodgers.Clear();
            Enabled.Clear();
            enemyLast.Clear();
            enemyVelocity.Clear();
            ignoredProjectiles.Clear();
        }

        /// <summary>关闭某单位的躲避：若游走打断了玩家的移动命令，把命令还给它。</summary>
        public void Disable(int id)
        {
            Enabled.Remove(id);
            if (!dodgers.TryGetValue(id, out Dodger d)) return;
            dodgers.Remove(id);
            if (d.Resume.HasValue && d.LastOrder > d.ResumeIssued && GameUnitManagerAPI.Instance.IsValid(id))
                Move(GameUnitManagerAPI.Instance, id, d.Resume.Value.x, d.Resume.Value.y);
        }

        /// <summary>预计飞行时间（游戏刻），生成后几刻内即改用实测。弓箭、弩箭实测约 0.12 × 距离 + 23。</summary>
        private static int FlightTicks(ProjectileType type, float distance) => Mathf.RoundToInt(0.12f * distance + 23f);

        public void OnProjectileSpawn(int id, ProjectileType type, int now, Vector2 source, Vector2 aim)
        {
            float distance = Vector2.Distance(source, aim);
            int flight = Mathf.Max(1, FlightTicks(type, distance));
            threats[id] = new Threat
            {
                Id = id, Source = source, Aim = aim, Spawn = now,
                Dir = distance > 0.01f ? (aim - source) / distance : Vector2.zero,
                Speed = distance / flight, Impact = now + flight, Slack = 20,
            };
        }

        public void OnProjectileGone(int id)
        {
            threats.Remove(id);
            ignoredProjectiles.Remove(id);
        }

        /// <summary>
        /// 按飞行物的实际位置（地块坐标）更新地面速度和落地时刻，误差约 1–2 刻。
        /// 塔上射出的箭飞得比公式久：只要还在飞，就一直跟踪，不按预计时刻丢弃；状态字段一变（落地）才结束。
        /// </summary>
        private unsafe void TrackThreats(int now)
        {
            GameProjectileManagerAPI projectiles = GameProjectileManagerAPI.Instance;
            DiscoverProjectiles(projectiles, now);
            foreach (Threat t in threats.Values)
            {
                if (t.Landed) continue;
                if (!projectiles.TryGetProjectileById(t.Id, out GameProjectile* p) || p == null) continue;
                int state = (int)p->r_ArrowState;
                if (t.FlightState < 0) t.FlightState = state;
                else if (state != t.FlightState)
                {
                    t.Landed = true;
                    t.Impact = now;
                    t.Slack = 2;
                    continue;
                }
                var pos = new Vector2(p->r_CurrentTileX * WorldPerTile + WorldPerTile / 2f, p->r_CurrentTileY * WorldPerTile + WorldPerTile / 2f);
                float done = Vector2.Dot(pos - t.Source, t.Dir);
                if (t.FirstSeenTick < 0) { t.FirstSeenTick = now; t.FirstSeenDone = done; continue; }
                int elapsed = now - t.FirstSeenTick;
                float progressed = done - t.FirstSeenDone;
                if (elapsed < 4 || progressed <= 4f) continue;
                t.Speed = progressed / elapsed;
                float remaining = Mathf.Max(0f, Vector2.Distance(t.Source, t.Aim) - done);
                // 位置已到落点附近还“飞”了 6 刻以上：视为已落地（插在地上的箭要过一阵才会被删除）。
                if (remaining <= WorldPerTile && ++t.AtAimTicks > 6)
                {
                    t.Landed = true;
                    t.Impact = now;
                    t.Slack = 2;
                    continue;
                }
                t.Impact = now + Mathf.Max(1, Mathf.RoundToInt(remaining / t.Speed));
                t.Slack = 4;
                t.Measured = true;
            }
        }

        /// <summary>玩家给某单位下了移动命令：游走时朝这里推进，到了之后以此为岗位。</summary>
        public void OnPlayerMoveOrder(int unitId, int tileX, int tileY)
        {
            if (!Enabled.Contains(unitId)) return;
            Dodger d = Get(unitId);
            d.Resume = new Vector2Int(tileX, tileY);
            d.Post = (Vector2)d.Resume.Value * WorldPerTile;
            d.Waypoint = null;
            d.ResumeIssued = d.LastTime;
        }

        /// <summary>玩家给一支部队下了移动命令：部队里开了躲避的弓骑兵都以此为目的地。返回受影响的数量。</summary>
        public int OnPlayerTribeMoveOrder(int tribeId, int tileX, int tileY)
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int n = 0;
            foreach (int id in Enabled)
            {
                if (!units.IsValid(id) || units.GetTribe(id) != tribeId) continue;
                OnPlayerMoveOrder(id, tileX, tileY);
                n++;
            }
            return n;
        }

        private Dodger Get(int id)
        {
            if (!dodgers.TryGetValue(id, out Dodger d)) dodgers[id] = d = new Dodger();
            return d;
        }

        /// <summary>每个游戏刻调用一次。</summary>
        public void Tick(int now)
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;

            scratch.Clear();
            TrackThreats(now);
            // 只丢弃已落地的；还在飞的（落地时间一直在更新）保留。飞太久（> 400 刻）的视为异常丢弃。
            foreach (Threat t in threats.Values)
                if ((t.Landed && t.Impact + t.Slack < now) || now - t.Spawn > 400) scratch.Add(t.Id);
            foreach (int id in scratch) threats.Remove(id);

            if (Enabled.Count > 0) SnapshotEnemies(units, now);

            scratch.Clear();
            foreach (int id in Enabled)
            {
                if (!units.IsValid(id) || units.GetCurrentHealth(id) <= 0) { scratch.Add(id); continue; }
                Step(units, id, now);
            }
            foreach (int id in scratch) { Enabled.Remove(id); dodgers.Remove(id); }
        }

        private void Step(GameUnitManagerAPI units, int id, int now)
        {
            var p = units.GetCurrentWorldTilePosition(id);
            var pos = new Vector2(p.X, p.Y);
            Dodger d = Get(id);
            if (d.AnchorTime < 0 || now - d.AnchorTime > 30) { d.AnchorPos = pos; d.AnchorTime = now; }
            else if (now - d.AnchorTime >= 8)
            {
                d.Velocity = (pos - d.AnchorPos) / (now - d.AnchorTime);
                d.AnchorPos = pos;
                d.AnchorTime = now;
            }
            d.LastPos = pos;
            d.LastTime = now;
            if (!d.Post.HasValue) d.Post = pos;

            // 到了玩家的目的地：以后围绕这里游走。
            if (d.Resume.HasValue && Vector2.Distance(pos, d.Post.Value) <= ArrivedRadius) d.Resume = null;

            bool melee = KiteMelee && MeleePressure(pos) > 0f;
            if (!InCombat(pos) && !melee)
            {
                // 没在交战：不干预。若游走打断过玩家的移动命令，把命令还给它。
                d.Waypoint = null;
                if (d.Resume.HasValue && d.LastOrder > d.ResumeIssued)
                {
                    Move(units, id, d.Resume.Value.x, d.Resume.Value.y);
                    d.ResumeIssued = d.LastOrder = now;
                }
                return;
            }

            // 没在走躲避段时：站着射（或按玩家命令行进），只有按当前速度推算会碰上来箭、或近战逼近时才走。
            // RestTicks = Hold 时一直如此；有限值时休息到点后接着游走（测试用）。
            if (RestTicks > 0 && !d.Waypoint.HasValue && !melee && (RestTicks >= Hold || now < d.RestUntil))
            {
                Vector2 v = d.Velocity;
                if (Danger(dt => pos + v * dt, now) <= RestDanger) return;
            }

            // 正在走一段：路线仍安全、还没到、没卡住，就继续。
            if (d.Waypoint.HasValue)
            {
                Vector2 w = d.Waypoint.Value;
                float left = Vector2.Distance(pos, w);
                bool reached = left <= ReachDistance;
                // 下命令后过了起步时间仍几乎没动（被挡住、被缠住），就换一段。
                bool stuck = now > d.WaypointUntil ||
                             (now - d.LastOrder > StartDelay + 10 && d.Velocity.magnitude < 0.3f);
                // 逃离近战时，只有终点本身贴近近战敌人才改道；否则一直跑完这段，别在原地反复起步。
                // 只擦边（离落点 13.5 以外）不算威胁：箭雨密集时，为擦边的箭反复改道会让单位原地起步、反而中箭。
                bool threatened = Danger(PathFunc(w, pos, 0, left / HorseSpeed), now) > ReplanDanger ||
                                  (KiteMelee && (melee ? NearMelee(w, KiteClose) : MeleePressure(w) > 0f));
                if (!reached && !stuck && (!threatened || now - d.LastOrder < MinOrderGap)) return;
                // 走完这段：停下站着（不接下一段）；若玩家的移动命令还没走到，接着走玩家的命令。
                if (RestTicks > 0 && reached && !stuck && !threatened && !melee)
                {
                    if (left > WorldPerTile && now <= d.WaypointUntil) return; // 还没真正走到
                    d.Waypoint = null;
                    d.RestUntil = now + RestTicks;
                    if (d.Resume.HasValue)
                    {
                        Move(units, id, d.Resume.Value.x, d.Resume.Value.y);
                        d.ResumeIssued = d.LastOrder = now;
                    }
                    return;
                }
            }

            // 选下一段。已在奔跑时只考虑前方 ±120° 的转向，起步时考虑所有方向。
            bool running = d.Waypoint.HasValue && d.Velocity.magnitude > 0.3f;
            float delay = running ? 1 : StartDelay;
            Vector2 heading = running ? d.Heading : Vector2.zero;
            Vector2? best = null;
            float bestScore = float.MaxValue, bestDanger = 0f;
            foreach (int legTiles in melee ? KiteLegTiles : LegTiles)
            {
                for (int k = 0; k < 16; k++)
                {
                    float a = k * Mathf.PI / 8f;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    float turn = running ? Vector2.Angle(heading, dir) : 90f;
                    if (turn > 125f && !melee) continue;
                    Vector2 c = pos + dir * legTiles * WorldPerTile;
                    // 目标格和半路格都要在地图内且可通行，否则命令无效，单位会原地不动。
                    if (!Walkable(c) || !Walkable((pos + c) / 2f)) continue;
                    float danger = Danger(PathFunc(c, pos, delay, legTiles * WorldPerTile / HorseSpeed), now);
                    float score = danger * 60f + (melee ? FleeCost(c, pos) : Cost(c, pos, d, turn)) + CornerCost(c);
                    if (score < bestScore) { bestScore = score; best = c; bestDanger = danger; }
                }
            }
            if (!best.HasValue) return;

            Vector2 b = best.Value;
            // 逃离近战后，岗位跟着移过去：否则游走会把它拉回近战敌人身边。
            if (melee && !d.Resume.HasValue) d.Post = b;
            d.Heading = (b - pos).normalized;
            d.Waypoint = b;
            d.WaypointUntil = now + (int)delay + Mathf.CeilToInt(Vector2.Distance(pos, b) / HorseSpeed) + 12;
            Vector2Int tile = Vector2Int.RoundToInt(b / WorldPerTile);
            Move(units, id, tile.x, tile.y);
            d.LastOrder = now;
            Dodges++;
            if (melee) Kites++;
            if (Verbose) log($"[weave] #{id} at ({pos.x:F0},{pos.y:F0}) v=({d.Velocity.x:F2},{d.Velocity.y:F2}) -> ({b.x:F0},{b.y:F0}) " +
                             $"danger={bestDanger:F1}{(melee ? " melee" : "")} threats={threats.Count}");
        }

        /// <summary>交战中：有敌人在射程附近，或有飞行物落在附近。</summary>
        private bool InCombat(Vector2 pos)
        {
            float reach = (RangeWorld > 0 ? RangeWorld : 54 * WorldPerTile) + 6 * WorldPerTile;
            foreach (Enemy e in enemies)
                if (Vector2.Distance(pos, e.Pos) <= reach) return true;
            foreach (Threat t in threats.Values)
                if (Vector2.Distance(pos, t.Aim) <= 8 * WorldPerTile) return true;
            return false;
        }

        /// <summary>
        /// 候选路点的代价：别离岗位太远；有玩家目的地时往那里推进；保持敌人在射程内；远离逼近的近战，不从近战身边穿过；
        /// 不和别的弓骑兵挤在一起；转弯适中（直行容易被预判，急转弯看起来像抖动）。
        /// </summary>
        private float Cost(Vector2 c, Vector2 from, Dodger d, float turn)
        {
            float cost = 0f;
            if (d.Resume.HasValue)
            {
                cost += 0.6f * Vector2.Distance(c, d.Post.Value);
            }
            else
            {
                float fromPost = Vector2.Distance(c, d.Post.Value);
                cost += 0.15f * fromPost;
                if (fromPost > LeashRadius) cost += (fromPost - LeashRadius) * 3f;
            }

            // 转弯：30–90° 最好；直行会被敌方预判，掉头看起来僵硬。
            if (turn < 20f) cost += 8f;
            else if (turn > 100f) cost += (turn - 100f) * 0.3f;

            foreach (Dodger o in dodgers.Values)
            {
                if (o == d) continue;
                float gap = Vector2.Distance(c, o.Waypoint ?? o.LastPos);
                if (gap < 2 * WorldPerTile) cost += (2 * WorldPerTile - gap) * 1.5f;
            }

            float nearest = float.MaxValue, nearestShooter = float.MaxValue;
            foreach (Enemy e in enemies)
            {
                float dist = Vector2.Distance(c, e.Pos);
                nearest = Mathf.Min(nearest, dist);
                if (!e.Melee) { nearestShooter = Mathf.Min(nearestShooter, dist); continue; }
                if (dist < KiteSafe) cost += (KiteSafe - dist) * 4f;
                if (DistanceToSegment(e.Pos, from, c) < PathClearance) cost += 300f;
            }
            if (RangeWorld > 0)
            {
                // 保持敌人在自己射程内……
                if (nearest < float.MaxValue && nearest > RangeWorld * 0.95f)
                    cost += (nearest - RangeWorld * 0.95f) * 2f;
                // ……但离敌方射手不要太近：距离越近箭飞得越快（120 格距离只飞 12–26 刻），根本来不及躲。
                float standOff = RangeWorld * StandOffFraction;
                if (nearestShooter < standOff) cost += (standOff - nearestShooter) * 5f;
            }
            return cost;
        }

        /// <summary>
        /// 被近战追赶时的代价：只看离近战远不远、别从近战旁边穿过、别撞上别的弓骑兵。
        /// 速度和步兵差不多，只有一开始就直线拉开才甩得掉，所以此时不顾岗位、不刻意转弯。
        /// </summary>
        private float FleeCost(Vector2 c, Vector2 from)
        {
            float cost = 0f;
            foreach (Enemy e in enemies)
            {
                if (!e.Melee) continue;
                // 按敌人当前速度预判它 20 刻后的位置。
                Vector2 ahead = e.Pos + e.Velocity * 20f;
                float dist = Mathf.Min(Vector2.Distance(c, e.Pos), Vector2.Distance(c, ahead));
                if (dist < KiteSafe * 1.5f) cost += (KiteSafe * 1.5f - dist) * 8f;
                if (DistanceToSegment(e.Pos, from, c) < PathClearance) cost += 300f;
            }
            foreach (Dodger o in dodgers.Values)
            {
                float gap = Vector2.Distance(c, o.Waypoint ?? o.LastPos);
                if (gap > 0.1f && gap < WorldPerTile) cost += (WorldPerTile - gap) * 2f;
            }
            return cost;
        }

        internal static bool Walkable(Vector2 world)
        {
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            int x = Mathf.RoundToInt(world.x / WorldPerTile), y = Mathf.RoundToInt(world.y / WorldPerTile);
            if (!tiles.IsTileInsideMapBounds(x, y)) return false;
            int id = tiles.GetTileId(x, y);
            return tiles.IsValidTileId(id) && tiles.IsTileWalkableAndUnoccupied(id);
        }

        /// <summary>周围 3 格一圈可通行的方向越少，越像死角：风筝时别往墙角、地图边缘退。</summary>
        private static float CornerCost(Vector2 c) => BlockedAround(c) * 6f;

        /// <summary>c 周围 3 格一圈 8 个方向中不可走的个数。</summary>
        internal static int BlockedAround(Vector2 c)
        {
            int blocked = 0;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                if (!Walkable(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 3 * WorldPerTile)) blocked++;
            }
            return blocked;
        }

        private bool NearMelee(Vector2 pos, float radius)
        {
            foreach (Enemy e in enemies)
                if (e.Melee && Vector2.Distance(pos, e.Pos) < radius) return true;
            return false;
        }

        /// <summary>逼近的近战敌人（或已经贴得很近的）。0 表示没有。</summary>
        private float MeleePressure(Vector2 pos)
        {
            float pressure = 0f;
            foreach (Enemy e in enemies)
            {
                if (!e.Melee) continue;
                Vector2 toMe = pos - e.Pos;
                float dist = toMe.magnitude;
                if (dist > KiteTrigger) continue;
                float closing = dist > 0.01f ? Vector2.Dot(e.Velocity, toMe / dist) : 0f;
                if (dist < KiteClose || closing > 0.2f) pressure += 1f;
            }
            return pressure;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f));
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>本刻所有敌方单位的位置和速度。</summary>
        private void SnapshotEnemies(GameUnitManagerAPI units, int now)
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int local = players.GetLocalPlayerId();
            enemies.Clear();
            all.Clear();
            units.GetAllUnits(all, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            foreach (int id in all)
            {
                int owner = units.GetOwner(id);
                if (owner <= 0 || owner == local || !players.IsPlayerIdValid(owner) || players.IsPlayerAlliedTo(local, owner)) continue;
                var p = units.GetCurrentWorldTilePosition(id);
                var pos = new Vector2(p.X, p.Y);
                // 位置只精确到地块：隔 8 刻以上再算速度，否则不是 0 就是 8 以上。
                Vector2 v = enemyVelocity.TryGetValue(id, out Vector2 kept) ? kept : Vector2.zero;
                if (!enemyLast.TryGetValue(id, out var last) || now - last.time > 30) enemyLast[id] = (pos, now);
                else if (now - last.time >= 8)
                {
                    v = (pos - last.pos) / (now - last.time);
                    enemyVelocity[id] = v;
                    enemyLast[id] = (pos, now);
                }
                enemies.Add(new Enemy { Id = id, Pos = pos, Velocity = v, Melee = !NotMelee.Contains((int)units.GetType(id)) });
            }
            if (enemyLast.Count > 2 * enemies.Count + 100)
            {
                scratch.Clear();
                foreach (var kv in enemyLast) if (kv.Value.time < now - 40) scratch.Add(kv.Key);
                foreach (int id in scratch) { enemyLast.Remove(id); enemyVelocity.Remove(id); }
            }
        }

        /// <summary>先原地停 delay 刻（起步），再用 travel 刻直线走到 c 并停在那里。</summary>
        private static Func<float, Vector2> PathFunc(Vector2 c, Vector2 from, float delay, float travel)
        {
            travel = Mathf.Max(travel, 0.01f);
            return dt => dt <= delay ? from : dt >= delay + travel ? c : Vector2.Lerp(from, c, (dt - delay) / travel);
        }

        /// <summary>单位按 path(相对刻数) 移动时，在各飞行物可能落地的时间窗内离落点有多近。0 = 安全；越大越危险。</summary>
        private float Danger(Func<float, Vector2> path, int now)
        {
            float danger = 0f;
            foreach (Threat t in threats.Values)
            {
                float closest = float.MaxValue;
                for (int tick = Mathf.Max(now, t.Impact - t.Slack); tick <= t.Impact + t.Slack; tick += 2)
                    closest = Mathf.Min(closest, Vector2.Distance(path(tick - now), t.Aim));
                if (closest < DangerRadius) danger += (DangerRadius - closest) / DangerRadius;
            }
            return danger;
        }

        private void Move(GameUnitManagerAPI units, int id, int tileX, int tileY)
        {
            IssuingOwnOrder = true;
            try { units.MoveToTile(id, tileX, tileY, 0); }
            finally { IssuingOwnOrder = false; }
        }
    }
}
