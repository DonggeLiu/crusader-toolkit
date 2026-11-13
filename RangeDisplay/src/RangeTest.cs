#if RANGE_TEST
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RangeDisplay
{
    /// <summary>
    /// 只在测试构建（-p:DefineConstants=RANGE_TEST）里编译：记录己方远程单位的位置、所站建筑、高度、
    /// 当前远程目标与距离，以及每支射出的箭的目标距离，用于实测塔上是否有射程加成。不进入发布包。
    /// </summary>
    internal static class RangeTest
    {
        private static float nextTick;
        private static float nextEnemyDump;
        private static readonly List<int> ids = new List<int>();
        // 飞行物编号是复用的槽位：按“本轮仍在 + 起点与落点不变”判断是不是同一支箭。
        private static readonly Dictionary<int, long> liveProjectiles = new Dictionary<int, long>();
        private static readonly HashSet<int> aliveNow = new HashSet<int>();
        private static readonly Dictionary<int, int> lastTarget = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> maxShot = new Dictionary<int, int>();

        // 距离扫描：BepInEx 目录下 rt-sweep.txt  写入一名己方射手的编号。其他敌人停到远角；
        // 一名敌人从离其他射手最远的方向、StartDistance 格处起，每 StepSeconds 秒前进 1 格。
        private const int ShooterPlayer = 1;
        private const int StartDistance = 62;
        private const int EndDistance = 40;
        private const float StepSeconds = 4f;
        private static int laneArcher, laneEnemy, laneX, laneY, laneDx, laneDy;
        private static readonly List<int> parked = new List<int>();
        private static readonly Queue<int> queue = new Queue<int>();
        private static readonly HashSet<int> doomed = new HashSet<int>();
        private static readonly Dictionary<int, int> shotsBy = new Dictionary<int, int>();
        private static int sweepDistance = -1;
        private static float nextStep;

        private static readonly string TriggerFile =
            System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "rt-sweep.txt");
        private static float nextTriggerCheck;

        private static unsafe void Heal(GameUnitManagerAPI units, int id)
        {
            if (units.TryGetUnitById(id, out GameUnit* e) && e != null)
            {
                e->r_MaxHealth = 1000000;
                e->r_CurrentHealth = 1000000;
            }
        }

        // 多余的敌人直接清掉，避免射手黏在别的目标上。
        private static unsafe void Remove(GameUnitManagerAPI units, int id)
        {
            doomed.Add(id);
            if (units.TryGetUnitById(id, out GameUnit* e) && e != null)
                e->r_CurrentHealth = 0;
        }

        private static void HandleSweep(ManualLogSource log, GameUnitManagerAPI units)
        {
            // 测试构建里敌人一直补满血，等到扫描开始也不会被射死。
            ids.Clear();
            units.GetAllUnits(ids, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            foreach (int id in ids)
                if (!doomed.Contains(id)) Heal(units, id);

            if (Time.unscaledTime >= nextTriggerCheck)
            {
                nextTriggerCheck = Time.unscaledTime + 0.5f;
                if (System.IO.File.Exists(TriggerFile))
                {
                    foreach (string part in System.IO.File.ReadAllText(TriggerFile).Split(new[] { (char)44, (char)32, (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(part, out int id) && id > 0) queue.Enqueue(id);
                    System.IO.File.Delete(TriggerFile);
                    log.LogInfo($"[rt] queued {queue.Count} sweeps");
                }
            }

            if (sweepDistance < 0 && queue.Count > 0)
            {
                StartSweep(log, units, queue.Dequeue());
                return;
            }

            if (sweepDistance < 0 || Time.unscaledTime < nextStep)
                return;

            sweepDistance--;
            nextStep = Time.unscaledTime + StepSeconds;
            if (sweepDistance < EndDistance)
            {
                Summarize(log, units);
                log.LogInfo($"[rt] SWEEP done archer={laneArcher}");
                sweepDistance = -1;
                return;
            }

            Summarize(log, units);
            units.MoveToTile(laneEnemy, laneX + laneDx * sweepDistance, laneY + laneDy * sweepDistance, 0);
            log.LogInfo($"[rt] STEP archer={laneArcher} d={sweepDistance}");
        }

        // 上一步（StepSeconds 秒）结束时：射手当前目标、与扫描敌人的实际格距、这一步射了几箭。
        private static unsafe void Summarize(ManualLogSource log, GameUnitManagerAPI units)
        {
            int target = units.TryGetUnitById(laneArcher, out GameUnit* u) && u != null ? u->r_RangedAttackTargetUnitId : -1;
            shotsBy.TryGetValue(laneArcher, out int shots);
            shotsBy[laneArcher] = 0;
            log.LogInfo($"[rt] STEPSUM archer={laneArcher} stepD={sweepDistance + 1} enemyDist={Dist(units, laneArcher, laneEnemy)} target={target} shots={shots}");
        }

        private static void StartSweep(ManualLogSource log, GameUnitManagerAPI units, int archer)
        {
            var shooters = new List<(int X, int Y)>();
            var enemies = new List<int>();
            ids.Clear();
            units.GetAllUnits(ids, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            foreach (int id in ids)
            {
                if (units.GetOwner(id) == ShooterPlayer)
                {
                    if (id != archer)
                    {
                        UnmanagedVector2<ushort> o = units.GetCurrentLocalTilePosition(id);
                        shooters.Add((o.X, o.Y));
                    }
                }
                else if (units.GetCurrentHealth(id) > 0) enemies.Add(id);
            }

            if (enemies.Count == 0 || units.GetOwner(archer) != ShooterPlayer)
            {
                log.LogInfo($"[rt] cannot sweep archer={archer}: enemies={enemies.Count}");
                return;
            }

            UnmanagedVector2<ushort> a = units.GetCurrentLocalTilePosition(archer);
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
            int bestDx = 0, bestDy = 0;
            double bestClear = -1;
            foreach (var (dx, dy) in dirs)
            {
                double clear = double.MaxValue;
                bool inside = true;
                for (int d = EndDistance; d <= StartDistance; d++)
                {
                    int x = a.X + dx * d, y = a.Y + dy * d;
                    if (!tiles.IsTileInsideMapBounds(x, y)) { inside = false; break; }
                    foreach (var o in shooters)
                        clear = Math.Min(clear, Math.Sqrt((x - o.X) * (x - o.X) + (y - o.Y) * (y - o.Y)));
                }
                if (inside && clear > bestClear) { bestClear = clear; bestDx = dx; bestDy = dy; }
            }

            if (bestClear < 0)
            {
                log.LogInfo($"[rt] no lane fits the map for archer={archer}");
                return;
            }

            laneArcher = archer; laneEnemy = enemies[0];
            laneX = a.X; laneY = a.Y; laneDx = bestDx; laneDy = bestDy;
            parked.Clear();
            for (int i = 1; i < enemies.Count; i++)
                Remove(units, enemies[i]);

            sweepDistance = StartDistance;
            nextStep = Time.unscaledTime + 25f;
            units.MoveToTile(laneEnemy, laneX + laneDx * sweepDistance, laneY + laneDy * sweepDistance, 0);
            eChimps type = units.GetType(archer);
            log.LogInfo($"[rt] RANGES {type} attack={units.GetUnitAttackRange(type)} tiles, engage={units.GetEngageRange(type)} world (default {units.GetDefaultEngageRange(type)})");
            log.LogInfo($"[rt] LANE archer={archer} {units.GetType(archer)} at ({a.X},{a.Y}) dir=({bestDx},{bestDy}) clearOfOthers={bestClear:F1} " +
                        $"enemy={laneEnemy} parked={parked.Count}");
        }

        public static unsafe void Tick(ManualLogSource log)
        {
            HandleSweep(log, GameUnitManagerAPI.Instance);
            if (Time.unscaledTime < nextTick)
                return;
            nextTick = Time.unscaledTime + 0.25f;

            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            int local = ShooterPlayer;

            // 远程目标变化
            ids.Clear();
            units.GetAllUnits(ids, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            foreach (int id in ids)
            {
                if (units.GetOwner(id) != local || RangedUnitTypes.Classify(units.GetType(id)) != RangedKind.Unit)
                    continue;
                if (!units.TryGetUnitById(id, out GameUnit* u) || u == null)
                    continue;

                int target = u->r_RangedAttackTargetUnitId;
                lastTarget.TryGetValue(id, out int previous);
                if (target != previous)
                {
                    lastTarget[id] = target;
                    log.LogInfo($"[rt] ACQ archer={id} {Describe(units, tiles, id)} target={target} {(target > 0 ? Describe(units, tiles, target) : "")} dist={Dist(units, id, target)}");
                }
            }

            // 新射出的箭：用射手与目标单位的格距
            ids.Clear();
            GameProjectileManagerAPI projectiles = GameProjectileManagerAPI.Instance;
            projectiles.GetAllProjectiles(ids, AliveState.IsAlive, null);
            aliveNow.Clear();
            foreach (int pid in ids)
            {
                if (!projectiles.TryGetProjectileById(pid, out GameProjectile* p) || p == null)
                    continue;
                aliveNow.Add(pid);
                // r_SourceWorldTile 实为飞行中的当前位置，不能用来区分；用落点与目标。
                long sig = ((long)p->r_TargetUnidId << 40) ^ ((long)p->r_TargetWorldTileX << 20) ^ p->r_TargetWorldTileY;
                if (liveProjectiles.TryGetValue(pid, out long known) && known == sig)
                    continue;
                liveProjectiles[pid] = sig;
                int src = p->r_SourceUnitId;
                if (src <= 0 || units.GetOwner(src) != local)
                    continue;
                int tgt = p->r_TargetUnidId;
                string d = Dist(units, src, tgt);
                if (int.TryParse(d.Split('.')[0], out int whole))
                {
                    maxShot.TryGetValue(src, out int best);
                    if (whole > best) maxShot[src] = whole;
                }
                shotsBy.TryGetValue(src, out int n);
                shotsBy[src] = n + 1;
                log.LogInfo($"[rt] SHOT src={src} {Describe(units, tiles, src)} tgt={tgt} {(tgt > 0 ? Describe(units, tiles, tgt) : "")} dist={d} " +
                            $"srcWorld=({p->r_SourceWorldTileX},{p->r_SourceWorldTileY}) aimWorld=({p->r_TargetWorldTileX},{p->r_TargetWorldTileY}) elev={p->r_SourceElevation}");
            }
            var gone = new List<int>();
            foreach (int pid in liveProjectiles.Keys)
                if (!aliveNow.Contains(pid)) gone.Add(pid);
            foreach (int pid in gone) liveProjectiles.Remove(pid);

            // 每 5 秒列一次敌方单位
            if (Time.unscaledTime >= nextEnemyDump)
            {
                nextEnemyDump = Time.unscaledTime + 5f;
                ids.Clear();
                units.GetAllUnits(ids, AliveState.IsAlive, null, PlayerRelationship.Any, null);
                var parts = new List<string>();
                foreach (int id in ids)
                    parts.Add($"{id}:{units.GetType(id)} o{units.GetOwner(id)} {Describe(units, tiles, id)} hp={units.GetCurrentHealth(id)}");
                log.LogInfo("[rt] UNITS " + string.Join(" | ", parts));
            }
        }

        private static unsafe string Describe(GameUnitManagerAPI units, GameTileManagerAPI tiles, int id)
        {
            UnmanagedVector2<ushort> t = units.GetCurrentLocalTilePosition(id);
            string building = "-";
            if (tiles.IsTileInsideMapBounds(t.X, t.Y))
            {
                int b = tiles.GetTileBuildingId(tiles.GetTileId(t.X, t.Y));
                if (b > 0 && GameBuildingManagerAPI.Instance.TryGetBuildingById(b, out GameBuilding* gb) && gb != null)
                    building = gb->r_BuildingType.ToString();
            }
            string h = units.TryGetUnitById(id, out GameUnit* u) && u != null ? u->r_HeightElevation.ToString() : "?";
            return $"tile=({t.X},{t.Y}) on={building} h={h}";
        }

        private static string Dist(GameUnitManagerAPI units, int a, int b)
        {
            if (a <= 0 || b <= 0)
                return "-";
            UnmanagedVector2<ushort> p = units.GetCurrentLocalTilePosition(a);
            UnmanagedVector2<ushort> q = units.GetCurrentLocalTilePosition(b);
            int dx = p.X - q.X, dy = p.Y - q.Y;
            return Math.Sqrt(dx * dx + dy * dy).ToString("F2");
        }
    }
}
#endif
