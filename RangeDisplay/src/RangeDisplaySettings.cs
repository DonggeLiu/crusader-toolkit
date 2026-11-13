using BepInEx.Configuration;
using System;
using System.ComponentModel;
using System.Windows.Input;

namespace RangeDisplay
{
    /// <summary>
    /// 扩展器 Mod Options 面板（Override/RangeDisplay/Settings.xaml）绑定的 ViewModel。
    /// 值直接存进 BepInEx 配置，不参与联机同步。
    /// </summary>
    public sealed class RangeDisplaySettings : INotifyPropertyChanged
    {
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<bool> includeSiege;
        private readonly ConfigEntry<bool> includeHealers;
        private readonly ConfigEntry<bool> previewTowers;

        public RangeDisplaySettings(ConfigEntry<bool> enabled, ConfigEntry<bool> includeSiege, ConfigEntry<bool> includeHealers, ConfigEntry<bool> previewTowers)
        {
            this.enabled = enabled;
            this.includeSiege = includeSiege;
            this.includeHealers = includeHealers;
            this.previewTowers = previewTowers;
            enabled.SettingChanged += (_, __) => Notify(nameof(Enabled));
            includeSiege.SettingChanged += (_, __) => Notify(nameof(IncludeSiege));
            includeHealers.SettingChanged += (_, __) => Notify(nameof(IncludeHealers));
            previewTowers.SettingChanged += (_, __) => Notify(nameof(PreviewTowers));
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

        public bool IncludeSiege
        {
            get => includeSiege.Value;
            set => includeSiege.Value = value;
        }

        public bool IncludeHealers
        {
            get => includeHealers.Value;
            set => includeHealers.Value = value;
        }

        public bool PreviewTowers
        {
            get => previewTowers.Value;
            set => previewTowers.Value = value;
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
                catch (Exception ex) { RangeDisplayPlugin.Log?.LogWarning($"Could not open link: {ex.Message}"); }
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
