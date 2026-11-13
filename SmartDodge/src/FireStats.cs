using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SmartDodge
{
    /// <summary>
    /// 测试用：统一口径统计敌方火力。每刻扫描飞行物表（塔上射手的箭不走生成事件），
    /// 记录每支敌方飞行物的发射高度、距离、飞行时间、是否瞄向己方弓骑兵，以及弓骑兵实际中箭情况。
    /// </summary>
    internal sealed class FireStats
    {
        private sealed class Shot
        {
            public ProjectileType Type;
            public int First;         // 首次看到的刻
            public float Distance;    // 发射点到落点
            public int Elevation;     // 发射点高度
            public bool AtHorseArcher; // 落点在某个己方弓骑兵 3 格内（首次看到时）
            public int FlightState = -1;
            public int Landed = -1;   // 落地时刻
            public int Hits;
            public float Lead = -1;   // 落点与目标单位当时位置的距离（预判量）
        }

        private readonly Dictionary<int, Shot> live = new Dictionary<int, Shot>();
        private readonly HashSet<int> ignored = new HashSet<int>();
        private readonly List<int> ids = new List<int>();
        private readonly List<Shot> done = new List<Shot>();
        private readonly List<float> hitOffsets = new List<float>();
        private readonly List<Vector2> horseArchers = new List<Vector2>();
        private readonly Func<ProjectileType, bool> damaging;
        public Func<List<int>> OwnHorseArchers;
        private int startTick = -1, hitsOnHorseArchers, horseArcherShots, horseArcherHits, horseArcherDamage;

        public FireStats(Func<ProjectileType, bool> damaging) { this.damaging = damaging; }

        public void Reset()
        {
            live.Clear(); ignored.Clear(); done.Clear(); hitOffsets.Clear();
            startTick = -1; hitsOnHorseArchers = horseArcherShots = horseArcherHits = horseArcherDamage = 0;
            edgeSamples = 0; edgeBlocked = 0;
            advanceTick = firstShotTick = -1; coverageTicks = inRangeTicks = withTargetTicks = 0;
            enemyHealth.Clear(); healthLost = 0; horseArcherTicks = 0; kills = 0;
            ownHealth.Clear(); ownLastPos.Clear(); ownHealthLost = 0; ownDeaths = 0; ownDistance = 0;
            lostById.Clear(); typeById.Clear(); killedIds.Clear(); startReal = -1;
        }

        private long edgeSamples, edgeBlocked;
        private int advanceTick = -1, firstShotTick = -1;
        private long coverageTicks, inRangeTicks, withTargetTicks;

        /// <summary>阵型开始进场的时刻：之后第一支弓骑兵箭的延迟也记录下来。</summary>
        public void MarkAdvance(int now, int orders) { advanceTick = now; firstShotTick = -1; ordersBase = orders; }

        /// <summary>移动频率、我方损失、实际时间。</summary>
        public string Losses(int now, int orders)
        {
            int ticks = Math.Max(1, now - startTick);
            float realSeconds = startReal >= 0 ? Time.unscaledTime - startReal : 0;
            float haSeconds = realSeconds > 0 ? horseArcherTicks * realSeconds / ticks : 0;
            return $"[losses] {ticks} ticks in {realSeconds:F0} s ({(realSeconds > 0 ? ticks / realSeconds : 0):F1} ticks/s) | own health lost {ownHealthLost}, deaths {ownDeaths}, hits {hitsOnHorseArchers} | " +
                   $"move orders {orders - ordersBase} = {(haSeconds > 0 ? (orders - ordersBase) / haSeconds : 0):F2} per horse archer per second | " +
                   $"distance moved {ownDistance:F0} = {(horseArcherTicks > 0 ? ownDistance / horseArcherTicks : 0):F2} per horse-archer-tick";
        }

        /// <summary>敌方掉血按兵种分：打死的 / 没打死的。</summary>
        public string EnemyBreakdown()
        {
            var groups = lostById.GroupBy(kv => (typeById.TryGetValue(kv.Key, out string t) ? t : "?") + (killedIds.Contains(kv.Key) ? " killed" : " alive"));
            return "[enemy-lost] " + string.Join(", ", groups.OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()} units {g.Sum(kv => kv.Value)}"));
        }

        /// <summary>本刻有多少弓骑兵在射程内、有多少有目标。</summary>
        public void AddCoverage(int inRange, int withTarget)
        {
            coverageTicks += HorseArchersAlive;
            inRangeTicks += inRange;
            withTargetTicks += withTarget;
        }

        /// <summary>弓骑兵周围 3 格一圈 8 个方向里不可走的平均个数（越大越靠边/墙角）。</summary>
        public void SampleEdges(int blocked) { edgeSamples++; edgeBlocked += blocked; }

        public unsafe void Tick(int now)
        {
            if (startTick < 0) startTick = now;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            GameProjectileManagerAPI projectiles = GameProjectileManagerAPI.Instance;
            int local = GamePlayerManagerAPI.Instance.GetLocalPlayerId();

            horseArchers.Clear();
            foreach (int id in OwnHorseArchers())
            {
                var p = units.GetCurrentWorldTilePosition(id);
                horseArchers.Add(new Vector2(p.X, p.Y));
                if (now % 10 == 0) SampleEdges(DodgeController.BlockedAround(new Vector2(p.X, p.Y)));
            }

            ids.Clear();
            projectiles.GetAllProjectiles(ids, AliveState.IsAlive, null);
            var alive = new HashSet<int>(ids);
            foreach (int id in ids)
            {
                if (!projectiles.TryGetProjectileById(id, out GameProjectile* p) || p == null) continue;
                if (!live.TryGetValue(id, out Shot s))
                {
                    if (ignored.Contains(id)) continue;
                    int owner = (int)p->r_PlayerSourceId;
                    if (!damaging(p->r_ProjectileType) || owner <= 0) { ignored.Add(id); continue; }
                    if (owner == local)
                    {
                        // 己方射出的箭：只统计弓骑兵的射击次数。
                        int src = p->r_SourceUnitId;
                        if (src > 0 && units.IsValid(src) && (int)units.GetType(src) == (int)eChimps.CHIMP_TYPE_ARAB_HORSEMAN)
                        {
                            horseArcherShots++;
                            if (advanceTick >= 0 && firstShotTick < 0) firstShotTick = now;
                        }
                        ignored.Add(id);
                        continue;
                    }
                    var source = new Vector2(p->r_SourceWorldTileX, p->r_SourceWorldTileY);
                    var aim = new Vector2(p->r_TargetWorldTileX, p->r_TargetWorldTileY);
                    live[id] = s = new Shot
                    {
                        Type = p->r_ProjectileType, First = now, Distance = Vector2.Distance(source, aim),
                        Elevation = p->r_SourceElevation,
                        AtHorseArcher = horseArchers.Any(h => Vector2.Distance(h, aim) <= 3 * DodgeController.WorldPerTile),
                    };
                    Aims[id] = aim;
                    int target = p->r_TargetUnidId;
                    if (target > 0 && units.IsValid(target))
                    {
                        var tp = units.GetCurrentWorldTilePosition(target);
                        s.Lead = Vector2.Distance(aim, new Vector2(tp.X, tp.Y));
                    }
                }
                int state = (int)p->r_ArrowState;
                if (s.FlightState < 0) s.FlightState = state;
                else if (s.Landed < 0 && state != s.FlightState) s.Landed = now;
            }
            TrackEnemyHealth(units, local, horseArchers.Count);
            TrackOwn(units, OwnHorseArchers());
            if (startReal < 0) startReal = Time.unscaledTime;

            // 已删除的飞行物归档。
            var gone = live.Keys.Where(id => !alive.Contains(id)).ToList();
            foreach (int id in gone) { done.Add(live[id]); live.Remove(id); Aims.Remove(id); }
            if (ignored.Count > 4 * ids.Count + 500) ignored.IntersectWith(ids);
        }

        public readonly Dictionary<int, Vector2> Aims = new Dictionary<int, Vector2>();

        // 真实输出：每刻读敌方血量，累计下降量；消失的单位按最后血量计入并算击杀。
        private readonly Dictionary<int, int> enemyHealth = new Dictionary<int, int>();
        private readonly List<int> enemyIds = new List<int>();
        private readonly HashSet<int> seenEnemies = new HashSet<int>();
        private long healthLost, horseArcherTicks;
        private int kills;
        public int HorseArchersAlive { get; private set; }

        // 我方：弓骑兵掉血、阵亡、移动距离。
        private readonly Dictionary<int, int> ownHealth = new Dictionary<int, int>();
        private readonly Dictionary<int, Vector2> ownLastPos = new Dictionary<int, Vector2>();
        private readonly HashSet<int> seenOwn = new HashSet<int>();
        private long ownHealthLost;
        private int ownDeaths;
        private double ownDistance;
        // 敌方掉血按单位记，用来解释两次清空同一座塔时总掉血的差别。
        private readonly Dictionary<int, long> lostById = new Dictionary<int, long>();
        private readonly Dictionary<int, string> typeById = new Dictionary<int, string>();
        private readonly HashSet<int> killedIds = new HashSet<int>();
        private float startReal = -1;
        private int ordersBase;

        private void TrackOwn(GameUnitManagerAPI units, List<int> own)
        {
            seenOwn.Clear();
            foreach (int id in own)
            {
                int hp = units.GetCurrentHealth(id);
                var p = units.GetCurrentWorldTilePosition(id);
                var pos = new Vector2(p.X, p.Y);
                if (ownHealth.TryGetValue(id, out int old) && hp < old) ownHealthLost += old - hp;
                if (ownLastPos.TryGetValue(id, out Vector2 last)) ownDistance += Vector2.Distance(last, pos);
                ownHealth[id] = hp;
                ownLastPos[id] = pos;
                seenOwn.Add(id);
            }
            foreach (int id in ownHealth.Keys.Where(k => !seenOwn.Contains(k)).ToList())
            {
                ownHealthLost += ownHealth[id];
                ownDeaths++;
                ownHealth.Remove(id);
                ownLastPos.Remove(id);
            }
        }

        private void TrackEnemyHealth(GameUnitManagerAPI units, int local, int horseArchersAlive)
        {
            HorseArchersAlive = horseArchersAlive;
            horseArcherTicks += horseArchersAlive;
            enemyIds.Clear();
            seenEnemies.Clear();
            units.GetAllUnits(enemyIds, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            foreach (int id in enemyIds)
            {
                int owner = units.GetOwner(id);
                if (owner <= 0 || owner == local) continue;
                int hp = units.GetCurrentHealth(id);
                if (hp <= 0) continue;
                if (enemyHealth.TryGetValue(id, out int old) && hp < old)
                {
                    healthLost += old - hp;
                    lostById.TryGetValue(id, out long l);
                    lostById[id] = l + old - hp;
                }
                enemyHealth[id] = hp;
                if (!typeById.ContainsKey(id)) typeById[id] = units.GetType(id).ToString().Replace("CHIMP_TYPE_", "");
                seenEnemies.Add(id);
            }
            var dead = enemyHealth.Keys.Where(id => !seenEnemies.Contains(id)).ToList();
            foreach (int id in dead)
            {
                healthLost += enemyHealth[id];
                lostById.TryGetValue(id, out long l);
                lostById[id] = l + enemyHealth[id];
                killedIds.Add(id);
                kills++;
                enemyHealth.Remove(id);
            }
        }

        public string Output(int now)
        {
            int ticks = Math.Max(1, now - startTick);
            float perHa = horseArcherTicks > 0 ? healthLost * 1000f / horseArcherTicks : 0;
            float shotsPerHa = horseArcherTicks > 0 ? horseArcherShots * 1000f / horseArcherTicks : 0;
            return $"[output] {ticks} ticks | enemy health lost {healthLost}, kills {kills} | horse archers alive {HorseArchersAlive}, " +
                   $"horse-archer-ticks {horseArcherTicks} | per 1000 horse-archer-ticks: health {perHa:F0}, shots {shotsPerHa:F1} | shots {horseArcherShots}" +
                   $" | in range {(coverageTicks > 0 ? inRangeTicks * 100f / coverageTicks : 0):F0}%, has target {(coverageTicks > 0 ? withTargetTicks * 100f / coverageTicks : 0):F0}%" +
                   $" | first shot {(firstShotTick >= 0 && advanceTick >= 0 ? (firstShotTick - advanceTick).ToString() : "-")} ticks after advance";
        }

        /// <summary>己方弓骑兵被飞行物伤到。</summary>
        public void OnHorseArcherHit(int projectile, Vector2 unitPos)
        {
            hitsOnHorseArchers++;
            if (live.TryGetValue(projectile, out Shot s)) s.Hits++;
            if (Aims.TryGetValue(projectile, out Vector2 aim)) hitOffsets.Add(Vector2.Distance(aim, unitPos));
        }

        /// <summary>己方弓骑兵的箭打中了敌人。</summary>
        public void OnHorseArcherDealt(int damage) { horseArcherHits++; horseArcherDamage += damage; }

        public string Report(int now)
        {
            var all = done.Concat(live.Values).ToList();
            int ticks = Math.Max(1, now - startTick);
            var incoming = all.Where(s => s.AtHorseArcher).ToList();
            string Group(IEnumerable<Shot> g, string name)
            {
                var l = g.ToList();
                if (l.Count == 0) return $"{name}: none";
                var landed = l.Where(s => s.Landed > 0).ToList();
                string flight = landed.Count > 0 ? $"{landed.Average(s => s.Landed - s.First):F0}" : "-";
                var leads = l.Where(s => s.AtHorseArcher && s.Lead >= 0).Select(s => s.Lead).OrderBy(x => x).ToList();
                string lead = leads.Count > 0 ? $"lead median {leads[leads.Count / 2]:F0} (n={leads.Count})" : "lead -";
                return $"{name}: n={l.Count} dist {l.Average(s => s.Distance):F0} elev {l.Average(s => s.Elevation):F0} flight {flight} hits {l.Sum(s => s.Hits)} {lead}";
            }
            var byElev = all.GroupBy(s => s.Elevation > 20 ? "high" : "low");
            var offs = hitOffsets.OrderBy(x => x).ToList();
            return $"[fire] {ticks} ticks | enemy projectiles {all.Count} ({all.Count * 100f / ticks:F1}/100t), aimed at horse archers {incoming.Count} ({incoming.Count * 100f / ticks:F1}/100t) | " +
                   $"hits on horse archers {hitsOnHorseArchers} = {(incoming.Count > 0 ? hitsOnHorseArchers * 100f / incoming.Count : 0):F1}% of aimed | " +
                   string.Join(" | ", byElev.Select(g => Group(g, g.Key))) + " | " +
                   string.Join(" | ", all.GroupBy(s => s.Type).Select(g => Group(g, g.Key.ToString()))) +
                   (offs.Count > 0 ? $" | hit offset median {offs[offs.Count / 2]:F0} max {offs.Last():F0}" : "") +
                   $" | horse archers shot {horseArcherShots}, hit {horseArcherHits}, damage {horseArcherDamage}" +
                   (edgeSamples > 0 ? $" | blocked directions around horse archers {edgeBlocked / (float)edgeSamples:F2}/8" : "");
        }
    }
}
