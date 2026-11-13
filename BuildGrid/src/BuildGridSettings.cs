using BepInEx.Configuration;
using System;
using System.ComponentModel;
using System.Windows.Input;

namespace BuildGrid
{
    /// <summary>
    /// 扩展器 Mod Options 面板（Override/BuildGrid/Settings.xaml）绑定的 ViewModel。
    /// 值直接存进 BepInEx 配置，不参与联机同步。
    /// </summary>
    public sealed class BuildGridSettings : INotifyPropertyChanged
    {
        private readonly ConfigEntry<bool> enabled;

        public BuildGridSettings(ConfigEntry<bool> enabled)
        {
            this.enabled = enabled;
            enabled.SettingChanged += (_, __) => Notify(nameof(Enabled));
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
                catch (Exception ex) { BuildGridPlugin.Log?.LogWarning($"Could not open link: {ex.Message}"); }
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
