using System;
using System.ComponentModel;
using System.Windows.Input;

namespace TargetPriority
{
    /// <summary>
    /// 扩展器 Mod Options 面板（Override/TargetPriority/Settings.xaml）绑定的 ViewModel。
    /// 值直接存进 BepInEx 配置，不参与联机同步。
    /// </summary>
    public sealed class TargetPrioritySettings : INotifyPropertyChanged
    {
        private readonly Settings settings;

        internal TargetPrioritySettings(Settings settings)
        {
            this.settings = settings;
            settings.EnabledEntry.SettingChanged += (_, __) => Notify(nameof(Enabled));
            settings.ApplyToAlliesEntry.SettingChanged += (_, __) => Notify(nameof(ApplyToAllies));
            settings.ApplyToEnemiesEntry.SettingChanged += (_, __) => Notify(nameof(ApplyToEnemies));
            settings.ShootWildAnimalsEntry.SettingChanged += (_, __) => Notify(nameof(ShootWildAnimals));
            settings.ShootOverWallsEntry.SettingChanged += (_, __) => Notify(nameof(ShootOverWalls));
            settings.AllocateFireEntry.SettingChanged += (_, __) => Notify(nameof(AllocateFire));
            foreach (var entry in settings.TierEntries)
                entry.SettingChanged += (_, __) => NotifyRows();
            Earlier = new ParameterCommand(p => Step(p, -1));
            Later = new ParameterCommand(p => Step(p, +1));
            ResetOrder = new ParameterCommand(_ => ResetTiers());
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand OpenWorkshop { get; } = new OpenLinkCommand(ReleaseLinks.Workshop);
        public ICommand OpenKoFi { get; } = new OpenLinkCommand(ReleaseLinks.KoFi);
        public ICommand OpenAfdian { get; } = new OpenLinkCommand(ReleaseLinks.Afdian);
        public Noesis.Visibility WorkshopVisibility => OpenWorkshop.CanExecute(null)
            ? Noesis.Visibility.Visible : Noesis.Visibility.Collapsed;
        public Noesis.Visibility KoFiVisibility => OpenKoFi.CanExecute(null)
            ? Noesis.Visibility.Visible : Noesis.Visibility.Collapsed;
        public Noesis.Visibility AfdianVisibility => OpenAfdian.CanExecute(null)
            ? Noesis.Visibility.Visible : Noesis.Visibility.Collapsed;

        public bool Enabled
        {
            get => settings.EnabledEntry.Value;
            set => settings.EnabledEntry.Value = value;
        }

        public bool ApplyToAllies
        {
            get => settings.ApplyToAlliesEntry.Value;
            set => settings.ApplyToAlliesEntry.Value = value;
        }

        public bool ApplyToEnemies
        {
            get => settings.ApplyToEnemiesEntry.Value;
            set => settings.ApplyToEnemiesEntry.Value = value;
        }

        public bool ShootWildAnimals
        {
            get => settings.ShootWildAnimalsEntry.Value;
            set => settings.ShootWildAnimalsEntry.Value = value;
        }

        public bool ShootOverWalls
        {
            get => settings.ShootOverWallsEntry.Value;
            set => settings.ShootOverWallsEntry.Value = value;
        }

        public bool AllocateFire
        {
            get => settings.AllocateFireEntry.Value;
            set => settings.AllocateFireEntry.Value = value;
        }

        // 每个类别一个档位（1 最先打，10 最后）。界面上“提前/推后”改档位，“恢复默认”写回默认值。
        public ICommand Earlier { get; }
        public ICommand Later { get; }
        public ICommand ResetOrder { get; }

        public string TierSiegeEngines => TierText(Settings.Category.SiegeEngines);
        public string TierRanged => TierText(Settings.Category.Ranged);
        public string TierEngineers => TierText(Settings.Category.Engineers);
        public string TierMelee => TierText(Settings.Category.Melee);
        public string TierArmedWorkers => TierText(Settings.Category.ArmedWorkers);
        public string TierCivilians => TierText(Settings.Category.Civilians);
        public string TierSupport => TierText(Settings.Category.Support);
        public string TierLord => TierText(Settings.Category.Lord);
        public string TierSiegeEquipment => TierText(Settings.Category.SiegeEquipment);
        public string TierShield => TierText(Settings.Category.Shield);

        private string TierText(Settings.Category category) => settings.TierEntry(category).Value.ToString();

        private void Step(object parameter, int step)
        {
            if (!Enum.TryParse(parameter?.ToString(), out Settings.Category category)) return;
            var entry = settings.TierEntry(category);
            int tier = entry.Value + step;
            if (tier >= 1 && tier <= TargetTables.TierCount) entry.Value = tier;
            NotifyRows();
        }

        private void ResetTiers()
        {
            foreach (var entry in settings.TierEntries)
                entry.Value = (int)entry.DefaultValue;
            NotifyRows();
        }

        private void NotifyRows()
        {
            foreach (Settings.Category category in Enum.GetValues(typeof(Settings.Category)))
                Notify("Tier" + category);
        }

        private void Notify(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }

        private sealed class ParameterCommand : ICommand
        {
            private readonly Action<object> action;
            public ParameterCommand(Action<object> action) { this.action = action; }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                try { action(parameter); }
                catch (Exception ex) { TargetPriorityPlugin.ReportError(ex); }
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
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
                catch (Exception ex) { TargetPriorityPlugin.Log?.LogWarning($"Could not open link: {ex.Message}"); }
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
