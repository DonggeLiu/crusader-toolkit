using SHCDESE.API;
using System;
namespace BuildingProduction
{
    internal static class ReleaseText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string Pick(string chinese, string english) => IsChinese ? chinese : english;
        public static string SettingsPath => IsChinese ? "BuildingProduction/Settings.zh.xaml" : "BuildingProduction/Settings.xaml";
    }
}
