using BepInEx.Configuration;
using System;
using System.ComponentModel;
using System.Windows.Input;

namespace SmartDodge
{
    /// <summary>
    /// 扩展器 Mod Options 面板（Override/SmartDodge/Settings.xaml）绑定的 ViewModel。
    /// 值直接存进 BepInEx 配置，不参与联机同步。
    /// </summary>
    public sealed class SmartDodgeSettings : INotifyPropertyChanged
    {
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<bool> kiteMelee;
        private readonly ConfigEntry<bool> autoEnableNew;

        public SmartDodgeSettings(ConfigEntry<bool> enabled, ConfigEntry<bool> kiteMelee, ConfigEntry<bool> autoEnableNew)
        {
            this.enabled = enabled;
            this.kiteMelee = kiteMelee;
            this.autoEnableNew = autoEnableNew;
            enabled.SettingChanged += (_, __) => Notify(nameof(Enabled));
            kiteMelee.SettingChanged += (_, __) => Notify(nameof(KiteMelee));
            autoEnableNew.SettingChanged += (_, __) => Notify(nameof(AutoEnableNew));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand OpenWorkshop { get; } = new OpenLinkCommand(ReleaseLinks.Workshop);
        public ICommand OpenKoFi { get; } = new OpenLinkCommand(ReleaseLinks.KoFi);
        public ICommand OpenAfdian { get; } = new OpenLinkCommand(ReleaseLinks.Afdian);
        public Noesis.Visibility WorkshopVisibility => Visible(OpenWorkshop);
        public Noesis.Visibility KoFiVisibility => Visible(OpenKoFi);
        public Noesis.Visibility AfdianVisibility => Visible(OpenAfdian);

        public bool Enabled { get => enabled.Value; set => enabled.Value = value; }
        public bool KiteMelee { get => kiteMelee.Value; set => kiteMelee.Value = value; }
        public bool AutoEnableNew { get => autoEnableNew.Value; set => autoEnableNew.Value = value; }

        private static Noesis.Visibility Visible(ICommand command) =>
            command.CanExecute(null) ? Noesis.Visibility.Visible : Noesis.Visibility.Collapsed;

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
                catch (Exception ex) { SmartDodgePlugin.Log?.LogWarning($"Could not open link: {ex.Message}"); }
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
