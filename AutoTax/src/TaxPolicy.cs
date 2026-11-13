using System;

namespace AutoTax
{
    // Native tax popularity contributions per economic update, in 1/100 popularity.
    // Decisions use a coherent snapshot's total minus its tax contribution, never
    // subtract the newly selected tax from an old total.
    public sealed class TaxPolicy
    {
        private static readonly int[] TaxEffects = { 175, 125, 75, 25, -50, -100, -150, -200, -300, -400, -500, -600 };
        public enum Phase { Recover, Harvest, Balanced, ManualDonation }
        public Phase CurrentPhase { get; private set; } = Phase.Recover;
        public bool CannotRecover { get; private set; }
        public bool AtTaxLimit { get; private set; }
        public int PredictedChange { get; private set; }
        private int lastTick = -1, expectedTax = -1;

        public static int TaxEffect(int tax) => TaxEffects[tax];
        // Native food branch: actual rations, stock and currently eaten variety.
        // The exported food contribution can instead be a granary UI preview.
        public static int FoodEffect(int ration, bool hasFood, int variety)
        {
            int rations = !hasFood ? -200 : (Math.Max(0, Math.Min(4, ration)) - 2) * 100;
            int diversity = Math.Max(0, Math.Min(4, variety) - 1) * 25;
            return rations + diversity;
        }
        public static bool ValidBand(double lower, double upper) =>
            !double.IsNaN(lower) && !double.IsNaN(upper) && lower >= 50 && lower < upper && upper < 100;

        public void Reset()
        {
            lastTick = expectedTax = -1;
            CurrentPhase = Phase.Recover;
            CannotRecover = AtTaxLimit = false;
            PredictedChange = 0;
        }

        public int Evaluate(int tick, int popularity, int totalEffect, int snapshotTaxEffect,
            int tax, double lower, double upper)
        {
            if (tick < 0 || popularity < 0 || popularity > 10000 || tax < 0 || tax > 11 || !ValidBand(lower, upper))
            { Reset(); return tax; }
            // A load or manual tax change starts a fresh phase, but no month-long
            // delay: enabled automation must react at either boundary immediately.
            if (lastTick < 0 || tick < lastTick || tax != expectedTax)
                CurrentPhase = Phase.Recover;
            lastTick = tick;
            CannotRecover = AtTaxLimit = false;
            int bottom = (int)Math.Round(lower * 100), top = (int)Math.Round(upper * 100);
            long otherEffect = (long)totalEffect - snapshotTaxEffect;
            if (tax < 3)
            {
                // Only the player may select donations. Never overwrite that choice.
                CurrentPhase = Phase.ManualDonation;
                expectedTax = tax;
                PredictedChange = totalEffect;
                return tax;
            }
            const int minimum = 3;
            int recovery = -1, balanced = -1, harvest = -1;
            for (int t = minimum; t <= 11; t++)
            {
                long change = otherEffect + TaxEffects[t];
                if (change > 0) recovery = t; // Highest revenue that still recovers.
                else if (change == 0) balanced = t;
                else if (harvest < 0) harvest = t; // First deficit, not maximum tax.
            }

            if (popularity <= bottom) CurrentPhase = Phase.Recover;
            else if (popularity >= top) CurrentPhase = Phase.Harvest;
            int next;
            if (recovery < 0 && balanced < 0)
            {
                // Taxes alone cannot fix e.g. starvation with donations disabled.
                next = minimum;
                CurrentPhase = Phase.Recover;
                CannotRecover = true;
            }
            else if (balanced >= 0 && popularity > bottom && (popularity < 10000 || harvest < 0))
            {
                next = balanced;
                CurrentPhase = Phase.Balanced;
            }
            else if (CurrentPhase == Phase.Harvest && harvest >= 0)
                next = harvest;
            else if (recovery >= 0)
            {
                next = recovery;
                CurrentPhase = Phase.Recover;
            }
            else
            {
                next = balanced;
                CannotRecover = popularity <= bottom;
                CurrentPhase = Phase.Balanced;
            }

            long predicted = otherEffect + TaxEffects[next];
            // If a discrete tax step would cross the lower bound, wait for enough
            // headroom. The target band is soft: one update may be wider than it.
            if (predicted < 0 && popularity + predicted < bottom && popularity < 10000 && recovery >= 0)
            {
                next = recovery;
                predicted = otherEffect + TaxEffects[next];
                CurrentPhase = Phase.Recover;
            }
            // Anticipate a known cap overshoot, where a safe deficit is available.
            if (predicted > 0 && popularity + predicted >= 10000 && popularity > bottom && harvest >= 0 &&
                popularity + otherEffect + TaxEffects[harvest] >= bottom)
            {
                next = harvest;
                predicted = otherEffect + TaxEffects[next];
                CurrentPhase = Phase.Harvest;
            }
            // At the cap, do not wait for cooldowns or a new economic update.
            if (popularity >= 10000 && harvest >= 0)
            {
                next = harvest;
                predicted = otherEffect + TaxEffects[next];
                CurrentPhase = Phase.Harvest;
            }
            AtTaxLimit = next == 11 && predicted > 0;
            PredictedChange = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, predicted));
            expectedTax = next;
            return next;
        }
    }
}
