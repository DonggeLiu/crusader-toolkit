using SHCDESE.API;
using System;
namespace SmartDodge
{
    internal static class ReleaseText
    {
        public static bool IsChinese => (GameAssetManagerAPI.Instance.CurrentLanguage ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string SettingsPath => IsChinese ? "SmartDodge/Settings.zh.xaml" : "SmartDodge/Settings.xaml";
    }
}
