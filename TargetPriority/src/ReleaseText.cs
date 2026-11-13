using SHCDESE.API;
using System;
namespace TargetPriority
{
    internal static class ReleaseText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string SettingsPath => IsChinese ? "TargetPriority/Settings.zh.xaml" : "TargetPriority/Settings.xaml";
    }
}
