using SHCDESE.API;
using System;
namespace AutoTax
{
    internal static class ReleaseText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string Pick(string chinese, string english) => IsChinese ? chinese : english;
        public static string SettingsPath => IsChinese ? "AutoTax/Settings.zh.xaml" : "AutoTax/Settings.xaml";
    }
}
