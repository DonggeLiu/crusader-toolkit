using SHCDESE.API;
using System;
namespace GranaryOverview
{
    internal static class ReleaseText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string Pick(string chinese, string english) => IsChinese ? chinese : english;
        public static string SettingsPath => IsChinese ? "GranaryOverview/Settings.zh.xaml" : "GranaryOverview/Settings.xaml";
    }
}
