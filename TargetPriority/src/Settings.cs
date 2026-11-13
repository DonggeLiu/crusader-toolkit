using BepInEx.Configuration;
using System.Collections.Generic;

namespace TargetPriority
{
    /// <summary>配置项及兵种分类。档位 1 最优先，10 最后；不同类别可以同档。</summary>
    internal sealed class Settings
    {
        // 顺序即默认档位（1–10），用户指定；2026-10-08 实测后远程调到器械之前。
        public enum Category { Ranged, SiegeEngines, Engineers, Melee, ArmedWorkers, Civilians, Support, Lord, SiegeEquipment, Shield }

        // 兵种编号见 SHCDESE.Interop.eChimps（0–88）。未列出的归入平民（农民、其他工人、妇孺、牲畜等）。
        private static readonly Dictionary<int, Category> Categories = new Dictionary<int, Category>
        {
            // 投石与弩炮器械：投石车、投石机、射石机、弩炮、阿拉伯弩炮（巨火弩）
            [39] = Category.SiegeEngines, [40] = Category.SiegeEngines, [41] = Category.SiegeEngines,
            [61] = Category.SiegeEngines, [77] = Category.SiegeEngines,
            // 远程部队：弓手、弩手、阿拉伯弓手、投石兵、骑射手、掷火兵、伏击者、散兵、重骆驼
            [22] = Category.Ranged, [23] = Category.Ranged, [70] = Category.Ranged, [72] = Category.Ranged,
            [74] = Category.Ranged, [76] = Category.Ranged, [81] = Category.Ranged, [82] = Category.Ranged,
            [83] = Category.Ranged,
            // 工程师：能操作塔上的射石机和弩炮、搭建器械
            [30] = Category.Engineers,
            // 近战部队：矛兵、枪兵、锤兵、剑士、骑士、武僧、战犬、着火动物、奴隶、刺客、阿拉伯剑士、骆驼枪骑、宦官
            [24] = Category.Melee, [25] = Category.Melee, [26] = Category.Melee, [27] = Category.Melee,
            [28] = Category.Melee, [37] = Category.Melee, [67] = Category.Melee, [68] = Category.Melee,
            [69] = Category.Melee, [71] = Category.Melee, [73] = Category.Melee, [75] = Category.Melee,
            [78] = Category.Melee, [80] = Category.Melee,
            // 强壮工人和猛兽（对弓手：伐木工、石匠、采石工、铁匠 20，与矛兵相同；猎人 10；其他工人 2）+ 狮子、鬣狗、鳄鱼
            [3] = Category.ArmedWorkers, [6] = Category.ArmedWorkers, [7] = Category.ArmedWorkers,
            [8] = Category.ArmedWorkers, [19] = Category.ArmedWorkers,
            [45] = Category.ArmedWorkers, [87] = Category.ArmedWorkers, [88] = Category.ArmedWorkers,
            // 坑道兵等支援：坑道兵、两种挖掘工、贝都因医者、工兵、破坏者
            [5] = Category.Support, [31] = Category.Support, [32] = Category.Support,
            [79] = Category.Support, [84] = Category.Support, [85] = Category.Support,
            [55] = Category.Lord,
            // 冲车、攻城塔、梯兵：只攻城墙，不打部队
            [29] = Category.SiegeEquipment, [58] = Category.SiegeEquipment, [59] = Category.SiegeEquipment,
            [60] = Category.Shield,
        };

        private readonly ConfigEntry<bool> enabled, applyToAllies, applyToEnemies, shootWildAnimals, shootOverWalls, allocateFire, logTargets, probeThreat, startInvulnerable, testTools;
        private readonly ConfigEntry<float> fireMargin, movingHitFactor, toughVolleyFraction, meleeWarnSeconds;
        private readonly ConfigEntry<int> testStandX, testStandY, chargeFromX, chargeFromY, enemyStandX, enemyStandY;
        private readonly ConfigEntry<string> trimEnemies;
        private readonly ConfigEntry<int> actAsPlayer, shieldedSide;
        private readonly ConfigEntry<bool> planTowerShots, strictScores, planAliveAware, planNeedsBlockers, planOnFlat;
        private readonly Dictionary<Category, ConfigEntry<int>> tiers = new Dictionary<Category, ConfigEntry<int>>();

        public Settings(ConfigFile config)
        {
            enabled = config.Bind("General", "Enabled", true,
                "Use these priorities for your own ranged units (also in Mod Options).");
            applyToAllies = config.Bind("General", "ApplyToAllies", false,
                "Also use these priorities for allied players' ranged units.");
            applyToEnemies = config.Bind("General", "ApplyToEnemies", false,
                "Also use these priorities for enemy players' ranged units.");
            shootOverWalls = config.Bind("General", "ShootOverWalls", true,
                "Let your ranged units pick targets behind or on walls that they can hit with a manual attack order (vanilla only picks them if a flat line of fire clears the wall).");
            allocateFire = config.Bind("General", "AllocateFire", true,
                "Spread fire by what it takes to kill: once the arrows in the air and the archers already aiming at an enemy are enough to kill it, other archers pick the next enemy (also in Mod Options). Off = vanilla spreading (+50 per archer already aiming).");
            // 2026-10-09：已承诺伤害已按命中率打折，再加余量等于重复计算；平地 40 对 40 余量 0.2 时 +24，0 时 +31。
            fireMargin = config.Bind("Fire", "Margin", 0f, new ConfigDescription(
                "Extra damage on top of the enemy's health before it counts as covered (0.2 = 20%). Expected damage already accounts for misses.", new AcceptableValueRange<float>(0f, 2f)));
            movingHitFactor = config.Bind("Fire", "MovingHitFactor", 0.5f, new ConfigDescription(
                "Expected share of arrows that hit a walking enemy (they miss when it turns or stops).", new AcceptableValueRange<float>(0.1f, 1f)));
            toughVolleyFraction = config.Bind("Fire", "ToughVolleyFraction", 0.1f, new ConfigDescription(
                "With fire allocation on: an enemy that one volley of all your ranged units would take less than this share of health from (0.1 = 10%) is shot last within its tier, e.g. a mangonel on a tower.", new AcceptableValueRange<float>(0f, 1f)));
            meleeWarnSeconds = config.Bind("Fire", "MeleeWarnSeconds", 4f, new ConfigDescription(
                "With fire allocation on: enemy melee expected to reach your ranged units within this many seconds (or already fighting them) are shot as top tier.", new AcceptableValueRange<float>(0f, 30f)));
            shootWildAnimals = config.Bind("General", "ShootWildAnimals", false,
                "Also shoot lions and hyenas that are not attacking (vanilla only shoots them when they attack). Uses arrows on animals while enemies may be in range.");

            var range = new AcceptableValueRange<int>(1, TargetTables.TierCount);
            string tierHelp = "1 = shoot first, 10 = shoot last; categories may share a tier. Within a tier the nearer target wins.";
            Bind(config, Category.SiegeEngines, "SiegeEngines", "Catapults, trebuchets, mangonels, ballistas and fire ballistas. ", tierHelp, range);
            Bind(config, Category.Ranged, "Ranged", "Archers, crossbowmen, slingers, horse archers, fire throwers and Bedouin ranged units. ", tierHelp, range);
            Bind(config, Category.Engineers, "Engineers", "Engineers: they man mounted mangonels and ballistas. ", tierHelp, range);
            Bind(config, Category.Melee, "Melee", "Melee troops and war dogs. ", tierHelp, range);
            Bind(config, Category.ArmedWorkers, "ArmedWorkers", "Hard-hitting workers (woodcutters, stonemasons, quarry workers, blacksmiths, hunters) and beasts (lions, hyenas, crocodiles). ", tierHelp, range);
            Bind(config, Category.Civilians, "Civilians", "Other peasants and workers, women and children, livestock. ", tierHelp, range);
            Bind(config, Category.Support, "Support", "Tunnelers, Bedouin healers, sappers and demolishers. ", tierHelp, range);
            Bind(config, Category.Lord, "Lord", "Enemy lords: very hard to kill with arrows. ", tierHelp, range);
            Bind(config, Category.SiegeEquipment, "SiegeEquipment", "Battering rams, siege towers and laddermen. ", tierHelp, range);
            Bind(config, Category.Shield, "Shield", "Mantlets; units behind them are rated by their own category. ", tierHelp, range);

            logTargets = config.Bind("Debug", "LogTargets", false, "Log every target your ranged units pick.");
            actAsPlayer = config.Bind("Debug", "ActAsPlayer", 0,
                "Testing only: treat this player number as yours (0 = the real local player). Used to swap sides in A/B battles.");
            planTowerShots = config.Bind("Debug", "PlanTowerShots", true,
                "Experimental: targets your shooters would mostly miss (on towers) are chosen to maximize expected kills, using the measured hit pattern. Off = spread like vanilla. Test key Shift+F7.");
            planAliveAware = config.Bind("Debug", "PlanAliveAware", false,
                "Experimental: planned tower shots expect a blocker who is likely to die first to let the arrow through to the next one (tested 2026-10-09: no gain).");
            planOnFlat = config.Bind("Debug", "PlanOnFlat", true,
                "Use planned shots for every target, including accurate ones on flat ground (off = fire allocation on flat ground). Test key Shift+F9.");
            planNeedsBlockers = config.Bind("Debug", "PlanNeedsBlockers", true,
                "Planned tower shots only when some aim point has an enemy standing in front of it; otherwise spread like vanilla. Test key Shift+F8.");
            strictScores = config.Bind("Debug", "StrictScores", true,
                "With planned tower shots: pick the target with the best mod score, skipping vanilla's extra preference for enemies nobody aims at yet (only when the shot is planned).");
            shieldedSide = config.Bind("Debug", "ShieldedSide", 0,
                "Testing only: with ProbeThreat, this player's units take no arrow damage after F6 (one-way shooting range). 0 = off.");
            trimEnemies = config.Bind("Debug", "TrimEnemies", "XBOWMAN:tower:4,ARCHER:tower:2,XBOWMAN:ground:3",
                "Test key F4: enemy units to delete, as TYPE:tower|ground:count, comma separated.");
            chargeFromX = config.Bind("Debug", "ChargeFromX", 437, "Test key F3: enemy ground melee within 8 tiles of this tile walk to TestStandX/Y.");
            chargeFromY = config.Bind("Debug", "ChargeFromY", 429, "Test key F3: see ChargeFromX.");
            enemyStandX = config.Bind("Debug", "EnemyStandX", 430, "Test key F2: tile enemy ground shooters walk to.");
            enemyStandY = config.Bind("Debug", "EnemyStandY", 400, "Test key F2: tile enemy ground shooters walk to.");
            testStandX = config.Bind("Debug", "TestStandX", 390, "Test key F5: tile your ranged units walk to.");
            testStandY = config.Bind("Debug", "TestStandY", 425, "Test key F5: tile your ranged units walk to.");
            startInvulnerable = config.Bind("Debug", "StartInvulnerable", false, "With ProbeThreat: start with all units invulnerable (test key F6 turns it off).");
            probeThreat = config.Bind("Debug", "ProbeThreat", false, "Developer probe: log every melee hit and projectile launch (attacker fields and game time). Not for normal play.");
            testTools = config.Bind("Debug", "TestTools", false,
                "Developer test mode: F10 toggles the mod, F8 toggles shooting over walls, F7 toggles fire allocation, F12 lists units; with LogTargets it also logs what vanilla would have picked. Not for normal play.");
        }

        // 默认档位 = 类别在枚举中的顺序 + 1。
        private void Bind(ConfigFile config, Category category, string key, string what, string help, AcceptableValueRange<int> range)
        {
            tiers[category] = config.Bind("Priority", key, (int)category + 1, new ConfigDescription(what + help, range));
        }

        public ConfigEntry<bool> EnabledEntry => enabled;
        public ConfigEntry<bool> ApplyToAlliesEntry => applyToAllies;
        public ConfigEntry<bool> ApplyToEnemiesEntry => applyToEnemies;
        public ConfigEntry<bool> ShootWildAnimalsEntry => shootWildAnimals;
        public ConfigEntry<bool> ShootOverWallsEntry => shootOverWalls;
        public ConfigEntry<bool> AllocateFireEntry => allocateFire;
        public bool AllocateFire { get => allocateFire.Value; set => allocateFire.Value = value; }
        public float FireMargin => fireMargin.Value;
        public float MovingHitFactor => movingHitFactor.Value;
        public float ToughVolleyFraction => toughVolleyFraction.Value;
        public float MeleeWarnSeconds => meleeWarnSeconds.Value;

        public bool Enabled { get => enabled.Value; set => enabled.Value = value; }
        public bool ApplyToAllies => applyToAllies.Value;
        public bool ApplyToEnemies => applyToEnemies.Value;
        public bool ShootWildAnimals => shootWildAnimals.Value;
        public bool ShootOverWalls { get => shootOverWalls.Value; set => shootOverWalls.Value = value; }
        public bool LogTargets => logTargets.Value;
        public bool ProbeThreat => probeThreat.Value;
        public bool StartInvulnerable => startInvulnerable.Value;
        public string TrimEnemies => trimEnemies.Value;
        /// <summary>本 mod 视为“我方”的玩家：通常是本地玩家；测试时可用 ActAsPlayer 换成别的玩家（交换双方做对照）。</summary>
        public int ShieldedSide => shieldedSide.Value;
        public bool StrictScores => strictScores.Value;
        public bool PlanAliveAware { get => planAliveAware.Value; set => planAliveAware.Value = value; }
        public bool PlanOnFlat { get => planOnFlat.Value; set => planOnFlat.Value = value; }
        public bool PlanNeedsBlockers { get => planNeedsBlockers.Value; set => planNeedsBlockers.Value = value; }
        public bool PlanTowerShots { get => planTowerShots.Value; set => planTowerShots.Value = value; }
        public int LocalPlayer => actAsPlayer.Value > 0 ? actAsPlayer.Value : SHCDESE.API.GamePlayerManagerAPI.Instance.GetLocalPlayerId();
        public int EnemyStandX => enemyStandX.Value;
        public int EnemyStandY => enemyStandY.Value;
        public int ChargeFromX => chargeFromX.Value;
        public int ChargeFromY => chargeFromY.Value;
        public int TestStandX => testStandX.Value;
        public int TestStandY => testStandY.Value;
        public bool TestTools => testTools.Value;

        public static Category CategoryOf(int unitType) =>
            Categories.TryGetValue(unitType, out Category c) ? c : Category.Civilians;

        public int TierFor(int unitType)
        {
            int tier = tiers[CategoryOf(unitType)].Value;
            return tier < 1 ? 1 : tier > TargetTables.TierCount ? TargetTables.TierCount : tier;
        }

        public IEnumerable<ConfigEntry<int>> TierEntries => tiers.Values;

        public ConfigEntry<int> TierEntry(Category category) => tiers[category];
    }
}
