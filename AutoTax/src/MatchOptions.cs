using System;
using System.Globalization;

namespace AutoTax
{
    // Independent of global configuration; serialized inside each game save.
    public sealed class MatchOptions
    {
        public bool Enabled { get; set; }
        public double Lower { get; private set; }
        public double Upper { get; private set; }
        // Gold gained compared with leaving the tax at Baseline, while automation was in control.
        public double Extra { get; private set; }
        // Tax level the player had when automation last took over; -1 until it does.
        public int Baseline { get; set; } = -1;
        // Tax changes written by automation in this game.
        public int Adjustments { get; private set; }

        public MatchOptions(bool enabled, double lower, double upper)
        {
            Enabled = enabled;
            Lower = TaxPolicy.ValidBand(lower, upper) ? lower : 99;
            Upper = TaxPolicy.ValidBand(lower, upper) ? upper : 99.5;
        }
        public void Adjust(bool lower, int direction)
        {
            if (lower) Lower = Math.Round(Math.Max(50, Math.Min(Upper - 0.25, Lower + direction * 0.25)), 2);
            else Upper = Math.Round(Math.Max(Lower + 0.25, Math.Min(99.75, Upper + direction * 0.25)), 2);
        }
        public void AddExtra(double gold)
        {
            if (!double.IsNaN(gold) && !double.IsInfinity(gold)) Extra += gold;
        }
        public void CountAdjustment()
        {
            if (Adjustments < int.MaxValue) Adjustments++;
        }

        public string Serialize() => string.Join("|", "3", Enabled ? "1" : "0",
            Lower.ToString("R", CultureInfo.InvariantCulture), Upper.ToString("R", CultureInfo.InvariantCulture),
            Extra.ToString("R", CultureInfo.InvariantCulture), Baseline.ToString(CultureInfo.InvariantCulture),
            Adjustments.ToString(CultureInfo.InvariantCulture));

        // Versions 1 and 2 kept settings (2 also a total of auto-collected tax, which is
        // a different figure); their extra earnings start from zero.
        public static MatchOptions Read(string text, MatchOptions fallback)
        {
            var parts = (text ?? "").Split('|');
            bool known = (parts.Length == 4 && parts[0] == "1") || (parts.Length == 5 && parts[0] == "2") ||
                (parts.Length == 7 && parts[0] == "3");
            if (known && (parts[1] == "0" || parts[1] == "1") &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double hi) &&
                TaxPolicy.ValidBand(lo, hi)) {
                var restored = new MatchOptions(parts[1] == "1", lo, hi);
                if (parts[0] == "3")
                {
                    if (double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double extra) &&
                        !double.IsNaN(extra) && !double.IsInfinity(extra))
                        restored.Extra = extra;
                    if (int.TryParse(parts[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int baseline) &&
                        baseline >= -1 && baseline <= 11)
                        restored.Baseline = baseline;
                    if (int.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out int adjustments))
                        restored.Adjustments = adjustments;
                }
                return restored;
            }
            return new MatchOptions(fallback.Enabled, fallback.Lower, fallback.Upper);
        }
    }
}
