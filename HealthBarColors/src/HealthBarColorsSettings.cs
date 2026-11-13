using BepInEx.Configuration;
using System;
using System.ComponentModel;
using System.Windows.Input;

namespace HealthBarColors
{
    /// <summary>
    /// 扩展器 Mod Options 面板（Override/HealthBarColors/Settings.xaml）绑定的 ViewModel。
    /// 值直接存进 BepInEx 配置，不参与联机同步。
    /// </summary>
    public sealed class HealthBarColorsSettings : INotifyPropertyChanged
    {
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<bool> alwaysShow;
        private readonly ConfigEntry<ColorMode> colorMode;
        private readonly ConfigEntry<bool> markLord;

        public HealthBarColorsSettings(ConfigEntry<bool> enabled, ConfigEntry<bool> alwaysShow, ConfigEntry<ColorMode> colorMode, ConfigEntry<bool> markLord)
        {
            this.enabled = enabled;
            this.alwaysShow = alwaysShow;
            this.colorMode = colorMode;
            this.markLord = markLord;
            colorMode.SettingChanged += (_, __) => Notify(nameof(ShieldColors));
            markLord.SettingChanged += (_, __) => Notify(nameof(MarkLord));
            enabled.SettingChanged += (_, __) => Notify(nameof(Enabled));
            alwaysShow.SettingChanged += (_, __) => Notify(nameof(AlwaysShow));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand OpenWorkshop { get; } = new OpenLinkCommand(ReleaseLinks.Workshop);
        public ICommand OpenKoFi { get; } = new OpenLinkCommand(ReleaseLinks.KoFi);
        public ICommand OpenAfdian { get; } = new OpenLinkCommand(ReleaseLinks.Afdian);
        public Noesis.Visibility KoFiVisibility => OpenKoFi.CanExecute(null)
            ? Noesis.Visibility.Visible : Noesis.Visibility.Collapsed;
        public Noesis.Visibility AfdianVisibility => OpenAfdian.CanExecute(null)
            ? Noesis.Visibility.Visible : Noesis.Visibility.Collapsed;

        public bool Enabled
        {
            get => enabled.Value;
            set => enabled.Value = value;
        }

        public bool AlwaysShow
        {
            get => alwaysShow.Value;
            set => alwaysShow.Value = value;
        }

        /// <summary>Mod Options 里的勾选框：勾上 = 盾牌色模式，不勾 = 敌我友三色。</summary>
        public bool ShieldColors
        {
            get => colorMode.Value == ColorMode.Shield;
            set => colorMode.Value = value ? ColorMode.Shield : ColorMode.Side;
        }

        public bool MarkLord
        {
            get => markLord.Value;
            set => markLord.Value = value;
        }

        private void Notify(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }

        private sealed class OpenLinkCommand : ICommand
        {
            private readonly string url;
            public OpenLinkCommand(string url) { this.url = url; }
            public bool CanExecute(object parameter) =>
                Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps;
            public void Execute(object parameter)
            {
                if (!CanExecute(parameter)) return;
                try { UnityEngine.Application.OpenURL(url); }
                catch (Exception ex) { HealthBarColorsPlugin.Log?.LogWarning($"Could not open link: {ex.Message}"); }
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
