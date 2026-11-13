using CrusaderDE;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitStats
{
    /// <summary>当前打开的招募/建造菜单。</summary>
    internal enum StatsContext
    {
        None,
        Barracks,
        MercenaryPost,
        BedouinStockade,
        EngineersGuild,
        TunnelersGuild,
        Cathedral,
        SiegeEngines,
        TowerEngines,
    }

    /// <summary>表格中的一列（一个兵种）。</summary>
    internal sealed class UnitColumn
    {
        public string Name;
        public string[] Values;
    }

    /// <summary>
    /// 从游戏实际使用的数值表读取战斗属性（经扩展器 GameUnitManagerAPI），不维护静态数据。
    /// </summary>
    internal static class UnitStatsData
    {
        public static string[] RowLabels => UnitStatsText.RowLabels;

        private static readonly Dictionary<StatsContext, eChimps[]> Units = new Dictionary<StatsContext, eChimps[]>
        {
            [StatsContext.Barracks] = new[]
            {
                eChimps.CHIMP_TYPE_ARCHER, eChimps.CHIMP_TYPE_SPEARMAN, eChimps.CHIMP_TYPE_MACEMAN,
                eChimps.CHIMP_TYPE_XBOWMAN, eChimps.CHIMP_TYPE_PIKEMAN, eChimps.CHIMP_TYPE_SWORDSMAN,
                eChimps.CHIMP_TYPE_KNIGHT,
            },
            [StatsContext.MercenaryPost] = new[]
            {
                eChimps.CHIMP_TYPE_ARAB_BOW, eChimps.CHIMP_TYPE_ARAB_SLAVE, eChimps.CHIMP_TYPE_ARAB_SLINGER,
                eChimps.CHIMP_TYPE_ARAB_ASSASIN, eChimps.CHIMP_TYPE_ARAB_HORSEMAN, eChimps.CHIMP_TYPE_ARAB_SWORDSMAN,
                eChimps.CHIMP_TYPE_ARAB_GRENADIER,
            },
            [StatsContext.BedouinStockade] = new[]
            {
                eChimps.CHIMP_TYPE_BEDOUIN_CAMEL_LANCER, eChimps.CHIMP_TYPE_BEDOUIN_HEALER,
                eChimps.CHIMP_TYPE_BEDOUIN_EUNUCH, eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER,
                eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER, eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL,
                eChimps.CHIMP_TYPE_BEDOUIN_SAPPER, eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER,
            },
            [StatsContext.EngineersGuild] = new[] { eChimps.CHIMP_TYPE_ENGINEER, eChimps.CHIMP_TYPE_LADDERMAN },
            [StatsContext.TunnelersGuild] = new[] { eChimps.CHIMP_TYPE_TUNNELER },
            [StatsContext.Cathedral] = new[] { eChimps.CHIMP_TYPE_MONK },
            [StatsContext.SiegeEngines] = new[]
            {
                eChimps.CHIMP_TYPE_CATAPULT, eChimps.CHIMP_TYPE_TREBUCHET, eChimps.CHIMP_TYPE_SIEGE_TOWER,
                eChimps.CHIMP_TYPE_BATTERING_RAM, eChimps.CHIMP_TYPE_PORTABLE_SHIELD, eChimps.CHIMP_TYPE_ARAB_BALLISTA,
            },
            [StatsContext.TowerEngines] = new[] { eChimps.CHIMP_TYPE_MANGONEL, eChimps.CHIMP_TYPE_BALLISTA },
        };

        // 与原生目标选择函数里带射程表查询的单位一致（同 RangeDisplay）；其余单位的射程查询只返回默认值，不显示。
        private static readonly HashSet<eChimps> Ranged = new HashSet<eChimps>
        {
            eChimps.CHIMP_TYPE_ARCHER, eChimps.CHIMP_TYPE_XBOWMAN, eChimps.CHIMP_TYPE_ARAB_BOW,
            eChimps.CHIMP_TYPE_ARAB_SLINGER, eChimps.CHIMP_TYPE_ARAB_HORSEMAN, eChimps.CHIMP_TYPE_ARAB_GRENADIER,
            eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER, eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER,
            eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL,
            eChimps.CHIMP_TYPE_CATAPULT, eChimps.CHIMP_TYPE_TREBUCHET, eChimps.CHIMP_TYPE_MANGONEL,
            eChimps.CHIMP_TYPE_BALLISTA, eChimps.CHIMP_TYPE_ARAB_BALLISTA,
        };

        private static readonly HashSet<eChimps> SiegeEngines = new HashSet<eChimps>
        {
            eChimps.CHIMP_TYPE_CATAPULT, eChimps.CHIMP_TYPE_TREBUCHET, eChimps.CHIMP_TYPE_SIEGE_TOWER,
            eChimps.CHIMP_TYPE_BATTERING_RAM, eChimps.CHIMP_TYPE_PORTABLE_SHIELD, eChimps.CHIMP_TYPE_ARAB_BALLISTA,
            eChimps.CHIMP_TYPE_MANGONEL, eChimps.CHIMP_TYPE_BALLISTA,
        };

        // 放在塔楼上的器械不会移动。
        private static readonly HashSet<eChimps> Stationary = new HashSet<eChimps>
        {
            eChimps.CHIMP_TYPE_MANGONEL, eChimps.CHIMP_TYPE_BALLISTA,
        };

        // 近战伤害取“对各兵种伤害”的众数，代表对大多数目标的伤害（少数克制关系另有加成）。
        private static readonly eChimps[] MeleeTargets = Units
            .Where(kv => kv.Key <= StatsContext.Cathedral)
            .SelectMany(kv => kv.Value)
            .ToArray();

        public static List<UnitColumn> Build(StatsContext context)
        {
            var columns = new List<UnitColumn>();
            if (!Units.TryGetValue(context, out eChimps[] types))
                return columns;

            foreach (eChimps type in types)
                columns.Add(BuildColumn(type));
            return columns;
        }

        private static UnitColumn BuildColumn(eChimps type)
        {
            GameUnitManagerAPI u = GameUnitManagerAPI.Instance;
            int hp = (int)u.GetDefaultHealth(type);

            return new UnitColumn
            {
                Name = DisplayName(type),
                Values = new[]
                {
                    (hp / 100).ToString(),
                    SiegeEngines.Contains(type) ? "-" : TypicalMelee(u, type),
                    Ranged.Contains(type) ? TypicalRanged(u, type) : "-",
                    Ranged.Contains(type) ? u.GetUnitAttackRange(type).ToString() : "-",
                    HitsToKill(hp, u.GetRangedArrowDamageTo(type)),
                    HitsToKill(hp, u.GetRangedBoltDamageTo(type)),
                    Stationary.Contains(type) ? UnitStatsText.Pick("固定", "Fixed") : SpeedText(u, type),
                },
            };
        }

        private static string TypicalMelee(GameUnitManagerAPI u, eChimps type)
        {
            int mode = MeleeTargets
                .Select(target => u.GetMeleeDamageFromTo(type, target))
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First().Key;
            return mode.ToString();
        }

        // 远程伤害按弹种查“对各兵种伤害”表，取众数；单位与生命一致（表值 / 100）。
        // 火弹、石弹（攻城器械）没有按兵种的伤害表，不显示数值。
        private static string TypicalRanged(GameUnitManagerAPI u, eChimps type)
        {
            Func<eChimps, int> table;
            switch (GameUnitManagerAPI.GetDefaultAttackRangeProjectileType(type))
            {
                case ProjectileType.ArcherArrow: table = u.GetRangedArrowDamageTo; break;
                case ProjectileType.CrossbowBolt: table = u.GetRangedBoltDamageTo; break;
                case ProjectileType.SlingerStone: table = u.GetRangedSlingerDamageTo; break;
                case ProjectileType.BedouinLance: table = u.GetRangedJavelinDamageTo; break;
                default: return "-";
            }

            int mode = MeleeTargets
                .Select(target => table(target))
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First().Key;
            return (mode / 100).ToString();
        }

        private static string HitsToKill(int hp, int damagePerHit)
        {
            if (damagePerHit <= 0)
                return UnitStatsText.Pick("免疫", "Immune");
            return ((hp + damagePerHit - 1) / damagePerHit).ToString();
        }

        // 游戏速度值越小越快。
        private static string SpeedText(GameUnitManagerAPI u, eChimps type)
        {
            string text;
            switch (u.GetDefaultSpeed(type))
            {
                case 1: text = UnitStatsText.Pick("快", "Fast"); break;
                case 2: text = UnitStatsText.Pick("中", "Medium"); break;
                case 3: text = UnitStatsText.Pick("慢", "Slow"); break;
                default: text = UnitStatsText.Pick("很慢", "Very slow"); break;
            }

            int runBonus = 1;
            try
            {
                runBonus = u.GetDefaultCavalryRunSpeedBonus(type);
            }
            catch (Exception)
            {
                // 非骑兵单位可能没有冲锋加成数据。
            }

            return runBonus > 1 ? text + UnitStatsText.Pick(" (冲)", " +charge") : text;
        }

        // 兵种名用游戏自己的本地化文本，找不到时退回内部名。
        private static string DisplayName(eChimps type)
        {
            try
            {
                if (Enum.TryParse(type.ToString(), out Enums.eChimps gameType))
                {
                    string name = Translate.Instance.lookUpText(Enums.eTextSections.TEXT_CHIMP_NAMES, (int)gameType);
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            catch (Exception)
            {
                // 回退到内部名。
            }

            return type.ToString().Replace("CHIMP_TYPE_", "");
        }
    }
}
