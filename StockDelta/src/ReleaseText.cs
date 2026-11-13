using SHCDESE.API;
using System;
namespace StockDelta
{
    internal static class ReleaseText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string Pick(string chinese, string english) => IsChinese ? chinese : english;
        public static string SettingsPath => IsChinese ? "StockDelta/Settings.zh.xaml" : "StockDelta/Settings.xaml";
    }
}
