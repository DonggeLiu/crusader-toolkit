using BepInEx.Configuration;
using BepInEx.Logging;
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
    /// 开发测试工具（配置 [Test] TestTools = true 才启用）。
    /// F1 记录目标；F2 在最近的塔外集结 5×2 阵型（集结期间不死）；F3 重置统计；F4 敌方射手保持满血开/关；
    /// F5 敌方工程师操作投石机/弩炮；F6 删除不在塔上的敌人；F7 进场并开始统计；F9 全部弓骑兵开/关躲避；
    /// F10 切换每段休息刻数；F11 敌方进攻姿态 + 近战冲锋；F12 切换休息时的危险门槛。
    /// </summary>
    internal sealed class TestBench
    {
        private readonly DodgeController dodge;
        private readonly ManualLogSource log;
        private readonly Func<List<int>> ownHorseArchers;
        private readonly FireStats fire;
        private readonly ConfigEntry<bool> invulnerable, healEnemyShooters;
        private float nextSummary, nextTargetLog;

        private static readonly eChimps[] Shooters =
            { eChimps.CHIMP_TYPE_ARCHER, eChimps.CHIMP_TYPE_XBOWMAN, eChimps.CHIMP_TYPE_ARAB_BOW };
        private static readonly int[] RestOptions = { 0, 40, 100, 200, 400, 800, 1600, DodgeController.Hold };
        private static readonly float[] RestDangerOptions = { 0.25f, 0.5f, 0.75f, 1.0f };

        public TestBench(ConfigFile config, DodgeController dodge, ManualLogSource log, Func<List<int>> ownHorseArchers, Func<ProjectileType, bool> damaging)
        {
            this.dodge = dodge;
            this.log = log;
            this.ownHorseArchers = ownHorseArchers;
            invulnerable = config.Bind("Test", "Invulnerable", false, "Keep your horse archers at full health (test only).");
            healEnemyShooters = config.Bind("Test", "HealEnemyShooters", false, "Keep enemy archers and crossbowmen at full health (test only).");
            fire = new FireStats(damaging) { OwnHorseArchers = ownHorseArchers };
        }

        private static int LocalPlayer => SmartDodgePlugin.LocalPlayer;

        public void Reset() => fire.Reset();

        public void OnProjectileDamage(int projectile, int attacked, int attacking, int damage)
        {
            GameUnitManagerAPI u = GameUnitManagerAPI.Instance;
            int local = LocalPlayer;
            if (u.IsValid(attacked) && u.GetOwner(attacked) == local && (int)u.GetType(attacked) == SmartDodgePlugin.HorseArcher)
            {
                var p = u.GetCurrentWorldTilePosition(attacked);
                fire.OnHorseArcherHit(projectile, new Vector2(p.X, p.Y));
            }
            else if (u.IsValid(attacking) && u.GetOwner(attacking) == local && (int)u.GetType(attacking) == SmartDodgePlugin.HorseArcher &&
                     u.IsValid(attacked) && u.GetOwner(attacked) != local)
                fire.OnHorseArcherDealt(damage);
        }

        public void OnKeys()
        {
            if (Input.GetKeyDown(KeyCode.F1)) LogHorseArcherTargets();
            if (Input.GetKeyDown(KeyCode.F2)) FormationStage();
            if (Input.GetKeyDown(KeyCode.F3)) { fire.Reset(); log.LogInfo("[test] statistics reset"); }
            if (Input.GetKeyDown(KeyCode.F4)) { healEnemyShooters.Value = !healEnemyShooters.Value; log.LogInfo($"[test] heal enemy shooters: {healEnemyShooters.Value}"); }
            if (Input.GetKeyDown(KeyCode.F5)) ManEnemyEngines();
            if (Input.GetKeyDown(KeyCode.F6)) RemoveEnemiesNotOnTowers();
            if (Input.GetKeyDown(KeyCode.F7)) FormationAdvance();
            if (Input.GetKeyDown(KeyCode.F9)) ToggleAll();
            if (Input.GetKeyDown(KeyCode.F10))
            {
                int i = Array.IndexOf(RestOptions, dodge.RestTicks);
                dodge.RestTicks = RestOptions[(i + 1) % RestOptions.Length];
                log.LogInfo($"[test] rest ticks between legs: {dodge.RestTicks}");
            }
            if (Input.GetKeyDown(KeyCode.F11)) { EnemiesAggressive(); EnemyMeleeCharge(); }
            if (Input.GetKeyDown(KeyCode.F12))
            {
                int i = Array.IndexOf(RestDangerOptions, dodge.RestDanger);
                dodge.RestDanger = RestDangerOptions[(i + 1) % RestDangerOptions.Length];
                log.LogInfo($"[test] rest danger threshold: {dodge.RestDanger}");
            }
        }

        public void OnTick(int now)
        {
            if (Time.unscaledTime >= nextTargetLog)
            {
                nextTargetLog = Time.unscaledTime + 3f;
                LogHorseArcherTargets();
            }
            int aliveBefore = fire.HorseArchersAlive;
            fire.Tick(now);
            SampleCoverage();
            if (now % 5 == 0) CheckCleared(now);
            if (aliveBefore > 0 && fire.HorseArchersAlive == 0)
                log.LogInfo("[final] " + fire.Output(now) + " | " + fire.Losses(now, dodge.Dodges));
            if (invulnerable.Value) HealOwnHorseArchers();
            if (healEnemyShooters.Value) HealEnemyShooters();
            if (Time.unscaledTime >= nextSummary)
            {
                nextSummary = Time.unscaledTime + 15f;
                log.LogInfo(fire.Report(now));
                log.LogInfo(fire.Output(now) + $" | rest {dodge.RestTicks}, rest danger {dodge.RestDanger}, dodging units {dodge.Enabled.Count}, found by scan {dodge.Discovered}");
                log.LogInfo(fire.Losses(now, dodge.Dodges));
            }
        }

        private void ToggleAll()
        {
            bool turnOn = dodge.Enabled.Count == 0;
            foreach (int id in new List<int>(dodge.Enabled)) dodge.Disable(id);
            if (turnOn)
                foreach (int id in ownHorseArchers()) dodge.Enabled.Add(id);
            log.LogInfo($"[test] dodging {(turnOn ? "ON" : "OFF")} for all: {dodge.Enabled.Count} horse archers");
        }

        private void HealOwnHorseArchers()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            foreach (int id in ownHorseArchers())
            {
                int max = units.GetMaxHealth(id);
                if (units.GetCurrentHealth(id) < max) units.SetCurrentHealth(id, max);
            }
        }

        private void HealEnemyShooters()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int local = LocalPlayer;
            var list = new List<int>();
            foreach (eChimps type in Shooters)
            {
                list.Clear();
                units.GetAllUnits(list, AliveState.IsAlive, type, PlayerRelationship.Any, null);
                foreach (int id in list)
                {
                    if (units.GetOwner(id) == local) continue;
                    int max = units.GetMaxHealth(id);
                    if (units.GetCurrentHealth(id) < max) units.SetCurrentHealth(id, max);
                }
            }
        }

        private List<int> Enemies()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int local = LocalPlayer;
            var all = new List<int>();
            units.GetAllUnits(all, AliveState.IsAlive, null, PlayerRelationship.Any, null);
            return all.FindAll(id => units.GetOwner(id) > 0 && units.GetOwner(id) != local);
        }

        /// <summary>单位站在塔上（方塔、圆塔等 STRUCT_TOWER*）。</summary>
        private static bool OnTower(GameUnitManagerAPI units, int id)
        {
            var p = units.GetCurrentWorldTilePosition(id);
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            int tile = tiles.GetTileId(p.X / DodgeController.WorldPerTile, p.Y / DodgeController.WorldPerTile);
            if (!tiles.IsValidTileId(tile)) return false;
            int building = tiles.GetTileBuildingId(tile);
            if (building <= 0) return false;
            int type = (int)GameBuildingManagerAPI.Instance.GetType(building);
            return type == (int)eStructs.STRUCT_TOWER || (type >= (int)eStructs.STRUCT_TOWER1 && type <= (int)eStructs.STRUCT_TOWER5);
        }

        /// <summary>F6：删除不在塔上的敌方单位，只留塔上的守军。</summary>
        private void RemoveEnemiesNotOnTowers()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int removed = 0, kept = 0;
            foreach (int id in Enemies())
            {
                if (OnTower(units, id)) { kept++; continue; }
                if (units.DeleteUnitSafe(id)) removed++;
            }
            log.LogInfo($"[test] removed {removed} enemy units not on towers, kept {kept} on towers");
        }

        /// <summary>F5：让敌方工程师去操作最近的投石机/弩炮。</summary>
        private void ManEnemyEngines()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            var enemies = Enemies();
            var engines = enemies.FindAll(id => units.GetType(id) == eChimps.CHIMP_TYPE_MANGONEL || units.GetType(id) == eChimps.CHIMP_TYPE_BALLISTA);
            var engineers = enemies.FindAll(id => units.GetType(id) == eChimps.CHIMP_TYPE_ENGINEER);
            int manned = 0;
            foreach (int engine in engines)
            {
                var e = units.GetCurrentWorldTilePosition(engine);
                int best = -1; float bestDist = float.MaxValue;
                foreach (int eng in engineers)
                {
                    var q = units.GetCurrentWorldTilePosition(eng);
                    float dist = Vector2.Distance(new Vector2(e.X, e.Y), new Vector2(q.X, q.Y));
                    if (dist < bestDist) { bestDist = dist; best = eng; }
                }
                if (best < 0) continue;
                engineers.Remove(best);
                if (GameTribeManagerAPI.Instance.ManSiegeEquipment(units.GetTribe(best), engine)) manned++;
            }
            log.LogInfo($"[test] manned {manned}/{engines.Count} engines");
        }

        /// <summary>F11：敌方全部进攻姿态，近战单位冲向己方弓骑兵中心（测试风筝）。</summary>
        private void EnemiesAggressive()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            var tribes = new HashSet<int>(Enemies().Select(id => units.GetTribe(id)).Where(t => t > 0));
            foreach (int tribe in tribes) GameTribeManagerAPI.Instance.SetStance(tribe, TribeStance.Aggressive);
            log.LogInfo($"[test] enemy stance -> Aggressive for {tribes.Count} tribes");
        }

        private void EnemyMeleeCharge()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            List<int> mine = ownHorseArchers();
            if (mine.Count == 0) return;
            Vector2 centre = Vector2.zero;
            foreach (int id in mine) { var p = units.GetCurrentWorldTilePosition(id); centre += new Vector2(p.X, p.Y); }
            Vector2Int tile = Vector2Int.RoundToInt(centre / mine.Count / DodgeController.WorldPerTile);
            int sent = 0;
            foreach (int id in Enemies())
            {
                var t = units.GetType(id);
                if (Array.IndexOf(Shooters, t) >= 0 || t == eChimps.CHIMP_TYPE_ARAB_SLINGER || t == eChimps.CHIMP_TYPE_ARAB_HORSEMAN || t == eChimps.CHIMP_TYPE_LORD) continue;
                units.MoveToTile(id, tile.x, tile.y, 0);
                sent++;
            }
            log.LogInfo($"[test] {sent} enemy melee units charge tile {tile}");
        }

        /// <summary>F1：每个己方弓骑兵的远程目标（单位结构 +0x99C，同 Smart Shooters）、目标距离和最近敌人距离。</summary>
        private unsafe void LogHorseArcherTargets()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            var enemies = Enemies();
            foreach (int id in ownHorseArchers())
            {
                if (!units.TryGetUnitById(id, out GameUnit* u) || u == null) continue;
                int target = *(ushort*)((byte*)u - 0x65C + 0x99C);
                var p = units.GetCurrentWorldTilePosition(id);
                var pos = new Vector2(p.X, p.Y);
                string t = "none";
                if (target > 0 && units.IsValid(target))
                {
                    var q = units.GetCurrentWorldTilePosition(target);
                    t = $"{units.GetType(target).ToString().Replace("CHIMP_TYPE_", "")}#{target} dist {Vector2.Distance(pos, new Vector2(q.X, q.Y)):F0}";
                }
                float nearest = float.MaxValue;
                foreach (int e in enemies) { var q = units.GetCurrentWorldTilePosition(e); nearest = Mathf.Min(nearest, Vector2.Distance(pos, new Vector2(q.X, q.Y))); }
                log.LogInfo($"[target] HA#{id} at ({p.X},{p.Y}) hp {units.GetCurrentHealth(id)} target {t} | nearest enemy {nearest:F0} | dodging {dodge.Enabled.Contains(id)}");
            }
        }

        // 测试阵型：以离弓骑兵最近的敌人（塔）为基准，站在自身射程 78% 处；5 列 × 2 行，间隔 2 格。
        private Vector2 formationTower, formationDir;
        private readonly Dictionary<int, Vector2> formationFinal = new Dictionary<int, Vector2>();
        private const float FormationSpacing = 2 * DodgeController.WorldPerTile;
        private const float StagingExtra = 30 * DodgeController.WorldPerTile;
        private bool clearedLogged, formationAdvanced;

        /// <summary>F2：在站位再往外 30 格处排好阵型。塔上弓手能打到 550 以外，小地图上没有射程外的地方，所以集结期间不死。</summary>
        private void FormationStage()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            List<int> mine = ownHorseArchers();
            if (mine.Count == 0) return;
            mine.Sort();
            Vector2 own = Vector2.zero;
            foreach (int id in mine) { var q = units.GetCurrentWorldTilePosition(id); own += new Vector2(q.X, q.Y); }
            own /= mine.Count;

            float nearest = float.MaxValue;
            foreach (int id in Enemies())
            {
                var q = units.GetCurrentWorldTilePosition(id);
                float dist = Vector2.Distance(own, new Vector2(q.X, q.Y));
                if (dist < nearest) { nearest = dist; formationTower = new Vector2(q.X, q.Y); }
            }
            if (nearest == float.MaxValue) return;

            float radius = 0.78f * units.GetUnitAttackRange(eChimps.CHIMP_TYPE_ARAB_HORSEMAN) * DodgeController.WorldPerTile;
            int bestScore = int.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                float a = k * Mathf.PI / 8f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Vector2 spot = formationTower + dir * radius, staging = formationTower + dir * (radius + StagingExtra);
                if (!DodgeController.Walkable(spot) || !DodgeController.Walkable(staging)) continue;
                int blocked = DodgeController.BlockedAround(spot) + DodgeController.BlockedAround(spot + dir * 6 * DodgeController.WorldPerTile);
                int score = blocked * 1000 + Mathf.RoundToInt(Vector2.Distance(staging, own));
                if (score < bestScore) { bestScore = score; formationDir = dir; }
            }
            var perp = new Vector2(-formationDir.y, formationDir.x);
            formationFinal.Clear();
            invulnerable.Value = true;
            for (int i = 0; i < mine.Count; i++)
            {
                Vector2 offset = perp * ((i % 5) - 2) * FormationSpacing + formationDir * (i / 5) * FormationSpacing;
                Vector2 final = WalkableNear(formationTower + formationDir * radius + offset, perp);
                formationFinal[mine[i]] = final;
                Vector2Int tile = Vector2Int.RoundToInt(WalkableNear(final + formationDir * StagingExtra, perp) / DodgeController.WorldPerTile);
                units.MoveToTile(mine[i], tile.x, tile.y, 0);
            }
            log.LogInfo($"[test] formation staged: tower ({formationTower.x:F0},{formationTower.y:F0}), stand {radius:F0}, staging {radius + StagingExtra:F0}; {mine.Count} horse archers");
        }

        /// <summary>p 不可走时，沿阵型横向找最近的可走位置。</summary>
        private static Vector2 WalkableNear(Vector2 p, Vector2 perp)
        {
            if (DodgeController.Walkable(p)) return p;
            for (int k = 1; k <= 12; k++)
                foreach (int sign in new[] { 1, -1 })
                {
                    Vector2 c = p + perp * sign * k * DodgeController.WorldPerTile;
                    if (DodgeController.Walkable(c)) return c;
                }
            return p;
        }

        /// <summary>F7：整队平移到站位，关闭不死、补满血，统计从这里开始。</summary>
        private void FormationAdvance()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int moved = 0;
            foreach (var kv in formationFinal)
            {
                if (!units.IsValid(kv.Key) || units.GetCurrentHealth(kv.Key) <= 0) continue;
                Vector2Int tile = Vector2Int.RoundToInt(kv.Value / DodgeController.WorldPerTile);
                units.MoveToTile(kv.Key, tile.x, tile.y, 0);
                dodge.OnPlayerMoveOrder(kv.Key, tile.x, tile.y);
                moved++;
            }
            invulnerable.Value = false;
            HealOwnHorseArchers();
            fire.Reset();
            fire.MarkAdvance(SmartDodgePlugin.GameTime, dodge.Dodges);
            clearedLogged = false;
            formationAdvanced = true;
            LogComposition();
            log.LogInfo($"[test] formation advance: {moved} horse archers; statistics reset; rest {dodge.RestTicks}, rest danger {dodge.RestDanger}, dodging {dodge.Enabled.Count}");
        }

        private bool NearTargetTower(GameUnitManagerAPI units, int id)
        {
            var p = units.GetCurrentWorldTilePosition(id);
            return Vector2.Distance(new Vector2(p.X, p.Y), formationTower) <= 10 * DodgeController.WorldPerTile;
        }

        private void LogComposition()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            var enemies = Enemies();
            string Count(IEnumerable<int> list) => string.Join(", ", list.GroupBy(id => units.GetType(id).ToString().Replace("CHIMP_TYPE_", ""))
                .OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            var onTarget = enemies.FindAll(id => NearTargetTower(units, id));
            log.LogInfo($"[composition] all enemies {enemies.Count}: {Count(enemies)}");
            log.LogInfo($"[composition] target tower {onTarget.Count}: {Count(onTarget)}");
        }

        /// <summary>目标塔上没有射手了（或弓骑兵全灭）：记录一次统计。</summary>
        private void CheckCleared(int now)
        {
            if (!formationAdvanced || clearedLogged) return;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            bool anyShooter = Enemies().Exists(id => Array.IndexOf(Shooters, units.GetType(id)) >= 0 && NearTargetTower(units, id));
            bool anyOwn = ownHorseArchers().Count > 0;
            if (anyShooter && anyOwn) return;
            clearedLogged = true;
            string why = anyShooter ? "horse archers all dead" : "target tower shooters all dead";
            log.LogInfo($"[cleared] {why} | " + fire.Output(now) + $" | rest {dodge.RestTicks}, rest danger {dodge.RestDanger}");
            log.LogInfo("[cleared] " + fire.Losses(now, dodge.Dodges));
            log.LogInfo("[cleared] " + fire.EnemyBreakdown());
        }

        /// <summary>每刻：在射程内、有目标的弓骑兵人数（验证实验有效）。</summary>
        private unsafe void SampleCoverage()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            float range = units.GetUnitAttackRange(eChimps.CHIMP_TYPE_ARAB_HORSEMAN) * DodgeController.WorldPerTile;
            var enemyPositions = Enemies().Select(e => { var q = units.GetCurrentWorldTilePosition(e); return new Vector2(q.X, q.Y); }).ToList();
            int inRange = 0, withTarget = 0;
            foreach (int id in ownHorseArchers())
            {
                var p = units.GetCurrentWorldTilePosition(id);
                var pos = new Vector2(p.X, p.Y);
                if (enemyPositions.Exists(e => Vector2.Distance(pos, e) <= range)) inRange++;
                if (units.TryGetUnitById(id, out GameUnit* u) && u != null && *(ushort*)((byte*)u - 0x65C + 0x99C) > 0) withTarget++;
            }
            fire.AddCoverage(inRange, withTarget);
        }
    }
}
