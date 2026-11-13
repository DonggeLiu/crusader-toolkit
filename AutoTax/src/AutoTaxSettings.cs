using BepInEx.Configuration;
using System;
using System.ComponentModel;
using System.Windows.Input;
namespace AutoTax
{
    public sealed class AutoTaxSettings : INotifyPropertyChanged
    {
        private readonly ConfigEntry<bool> enabled;
        private readonly ConfigEntry<double> lower, upper;
        public AutoTaxSettings(ConfigEntry<bool> enabled,
            ConfigEntry<double> lower, ConfigEntry<double> upper)
        {
            this.enabled = enabled;
            this.lower = lower; this.upper = upper;
            enabled.SettingChanged += (_, __) => Notify(nameof(Enabled));
            lower.SettingChanged += (_, __) => Notify(nameof(Lower));
            upper.SettingChanged += (_, __) => Notify(nameof(Upper));
            Adjust = new Command(p => {
                string[] parts = (p as string ?? "").Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out int step)) return;
                ConfigEntry<double> entry = parts[0] == "Lower" ? lower : parts[0] == "Upper" ? upper : null;
                if (entry != null) entry.Value = Math.Round(entry.Value + step * 0.25, 2);
            });
        }
        public bool Enabled { get => enabled.Value; set => enabled.Value = value; }
        public double Lower => lower.Value;
        public double Upper => upper.Value;
        private string status = "";
        public string Status { get => status; set { if (status == value) return; status = value; Notify(nameof(Status)); } }
        public ICommand Adjust { get; }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private sealed class Command : ICommand
        {
            private readonly Action<object> action;
            public Command(Action<object> action) { this.action = action; }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => action(parameter);
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
