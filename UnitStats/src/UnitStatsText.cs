using SHCDESE.API;
using System;

namespace UnitStats
{
    internal static class UnitStatsText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "")
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string Pick(string chinese, string english) => IsChinese ? chinese : english;
        public static string[] RowLabels => IsChinese
            ? new[] { "生命", "近战伤害*", "远程伤害*", "射程（格）", "中箭致死", "中弩致死", "速度" }
            : new[] { "Health", "Melee damage*", "Ranged damage*", "Range (tiles)", "Arrows to kill", "Bolts to kill", "Speed" };
        public static string Notes => Pick(
            "* 伤害取目标表众数；- 表示无此攻击或数据不可用。致死次数为估算；(冲) 表示冲锋加成。",
            "* Damage: most common value across sampled troop targets. -: no attack or unavailable data. Hits to kill are estimates; +charge: charge bonus.");
        public static string SettingsPath => IsChinese ? "UnitStats/Settings.zh.xaml" : "UnitStats/Settings.xaml";
    }
}
