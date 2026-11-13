using BepInEx;
using CrusaderDE;
using BepInEx.Configuration;
using HarmonyLib;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoTax
{
    [BepInDependency("000shcdese", "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class AutoTaxPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "AutoTax_Dongge", PluginName = "Auto Tax", PluginVersion = "1.0.0";
        private static AutoTaxPlugin instance;
        private readonly TaxPolicy policy = new TaxPolicy();
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private ConfigEntry<bool> autoEnabled;
        private ConfigEntry<double> lower, upper;
        private volatile bool resetRequested = true;
        private int playerId = -1;
        private float nextPoll, nextError;
        private AutoTaxSettings settings;
        private MatchOptions match;
        private TaxPanelView taxView;
        private volatile int mapRequest = -1;
        private volatile bool matchReady;
        private volatile bool mapTransition;
        private const string SaveEntry = "AutoTax_Dongge/settings-v1.txt";

        private void Awake()
        {
            instance = this;
            autoEnabled = Config.Bind("General", "Enabled", true, "Enable automatic taxes in single-player games. Enabled by default.");
            lower = Config.Bind("Policy", "TargetLowerPopularity", 99.0,
                new ConfigDescription("Recover at this popularity; must be below the upper target.", new AcceptableValueRange<double>(50, 99.5)));
            upper = Config.Bind("Policy", "TargetUpperPopularity", 99.5,
                new ConfigDescription("Harvest at this popularity; below 100 to leave headroom for bonuses.", new AcceptableValueRange<double>(50.25, 99.75)));
            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e => {
                matchReady = false; mapTransition = e.Phase != EventHookPhase.Post;
                if (e.Phase == EventHookPhase.Post) mapRequest = 1;
            }));
            subscriptions.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(e => {
                matchReady = false; mapTransition = e.Phase != EventHookPhase.Post;
                if (e.Phase == EventHookPhase.Post) mapRequest = 2;
            }));
            subscriptions.Add(MapLoaderR3EventHooks.OnUnloadMap.Observable.Subscribe(e => {
                matchReady = false; mapTransition = true; mapRequest = 0;
            }));
            var harmony = new Harmony(PluginGuid);
            harmony.Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(AutoTaxPlugin), nameof(AfterUpdate)));
            harmony.Patch(AccessTools.Method(typeof(GameMapArchiveManagerAPI), "PrepareMapArchiveAndGetBytes"),
                prefix: new HarmonyMethod(typeof(AutoTaxPlugin), nameof(BeforeSave)));
            settings = new AutoTaxSettings(autoEnabled, lower, upper);
            try { GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName, settings, ReleaseText.SettingsPath, false); }
            catch (Exception ex) { Logger.LogError($"Could not register settings: {ex}"); }
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded; autoEnabled={autoEnabled.Value}");
        }

        // Every frame on the main thread. The BepInEx plugin component is destroyed after start-up,
        // so compare the reference itself: Unity's == would report the still-usable instance as null.
        private static void AfterUpdate()
        {
            var self = instance;
            if (ReferenceEquals(self, null)) return;
            try { self.Frame(); }
            catch (Exception ex) { self.ReportFrameError(ex); }
        }
        private float nextFrameError;
        private void ReportFrameError(Exception ex)
        {
            if (Time.unscaledTime < nextFrameError) return;
            nextFrameError = Time.unscaledTime + 10;
            Logger.LogError(ex);
            taxView?.Hide();
        }
        private void Frame()
        {
            taxView?.Animate(Time.unscaledTime);
            Tick();
        }
        private static void BeforeSave(bool isMapEditorSave)
        {
            if (!isMapEditorSave) instance?.PersistMatch();
        }
        private void PersistMatch()
        {
            if (!matchReady || match == null) return;
            try {
                if (GameMapArchiveManagerAPI.Instance.TryWriteTextFile(SaveEntry, match.Serialize(), true, true) != true)
                    Logger.LogError("Could not store Auto Tax settings in the save archive.");
            } catch (Exception ex) { Logger.LogError($"Could not save Auto Tax settings: {ex}"); }
        }
        private void ChangeMatch(bool? lowerTarget = null, int direction = 0)
        {
            if (!matchReady || match == null) return;
            if (lowerTarget.HasValue) match.Adjust(lowerTarget.Value, direction);
            else { match.Enabled = !match.Enabled; match.Baseline = -1; }
            policy.Reset(); nextPoll = 0;
            // The archive is written with the next normal game save, not separately.
            PersistMatch();
        }
        private void RefreshPanel()
        {
            var hud = MainViewModel.Instance?.HUDBuildingPanel;
            var panel = hud?.RefKeepPanel;
            if (!matchReady || panel == null || !hud.IsVisible || !panel.IsVisible)
            { taxView?.Hide(); return; }
            if (taxView == null || !taxView.IsFor(panel)) {
                taxView?.Detach();
                taxView = new TaxPanelView(panel, hud, () => ChangeMatch(), (lo, step) => ChangeMatch(lo, step));
            }
            ui.Enabled = match.Enabled;
            ui.Extra = match.Extra;
            ui.Adjustments = match.Adjustments;
            ui.Lower = match.Lower;
            ui.Upper = match.Upper;
            ui.Explanation = settings.Status;
            taxView.Update(ui);
        }

        private readonly TaxPanelState ui = new TaxPanelState();
        private int lastGameTime = -1;

        private void SetStatus(string verb, string detail, bool alert, string explanation)
        {
            ui.Verb = verb; ui.Detail = detail; ui.Alert = alert;
            settings.Status = explanation;
        }

        private static string PopularityText(int popularity) =>
            ReleaseText.Pick($"民心 {popularity / 100.0:0.#}", $"Popularity {popularity / 100.0:0.#}");

        // Native monthly gold for a tax level (100 people: taxes +60..+200, donations -60..-100).
        // Taxes use the taxable count, donations the whole population.
        private static double MonthlyGold(int tax, int taxable, int population) =>
            tax > 3 ? Math.Max(0, taxable) * (tax - 1) / 5.0 :
            tax < 3 ? -Math.Max(0, population) * (5 - tax) / 5.0 : 0;
        private const double TicksPerMonth = 800;

        // Extra earnings: while automation controls the tax, add the difference between the native
        // monthly rate of the current tax and that of the baseline tax, pro rata per game tick.
        private unsafe void AccountExtra(int gameTime, int tax, SHCDESE.Interop.GamePlayerResources* resources)
        {
            if (match.Baseline < 0 && tax >= 3) match.Baseline = tax;   // automation takes over
            if (tax < 3) match.Baseline = -1;                         // player chose a donation; automation yields
            int elapsed = lastGameTime < 0 ? 0 : gameTime - lastGameTime;
            lastGameTime = gameTime;
            if (elapsed <= 0 || elapsed > 4 * TicksPerMonth || match.Baseline < 0 || tax < 3) return;
            int population = (int)resources->r_TotalPopulation;
            // Taxes skip well firemen while popularity is below 50 (resources + 0x2304).
            int taxable = population - (int)*(uint*)((byte*)resources + 0x2304);
            match.AddExtra((MonthlyGold(tax, taxable, population) - MonthlyGold(match.Baseline, taxable, population)) * elapsed / TicksPerMonth);
        }

        private int pendingTax = -1;
        private float pendingSince;

        // While the keep panel is open, use the same path as the player's slider click
        // (MainViewModel.ButtonAdjustTax: SetTaxRate on the open keep), because the panel shows the
        // engine's own tax value and a direct write would leave it at the old level. With the panel
        // closed there is nothing to keep in sync, so write the player's tax directly.
        private void WriteTax(GamePlayerManagerAPI api, int player, int level)
        {
            var hud = MainViewModel.Instance?.HUDBuildingPanel;
            var state = GameData.Instance?.lastGameState;
            if (state != null && hud?.RefKeepPanel != null && hud.RefKeepPanel.IsVisible && state.in_structure > 0)
            {
                EngineInterface.GameAction(Enums.GameActionCommand.SetTaxRate, state.in_structure, level, 0);
                pendingTax = level;
                pendingSince = Time.unscaledTime;
            }
            else api.SetTaxesMode(player, (TaxesMode)level);
        }

        private unsafe void Tick()
        {
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.25f;
            try
            {
                if (resetRequested) { resetRequested = false; policy.Reset(); playerId = -1; }
                if (mapTransition) { taxView?.Hide(); return; }
                var director = Director.instance;
                var state = GameData.Instance?.lastGameState;
                if (director == null || GameMap.instance == null || !director.SimRunning ||
                    director.MultiplayerGame || state == null || state.spectatorMode != 0 ||
                    GameData.Instance.mapType == Enums.GameModes.MAP_EDITOR ||
                    !CrusaderLibrary.Instance.IsLibraryLoaded())
                {
                    matchReady = false; taxView?.Hide();
                    policy.Reset(); playerId = -1;
                    SetStatus(ReleaseText.Pick("待命", "Idle"), "", false, ReleaseText.Pick("未启用或不在单人对局中", "Disabled or outside a single-player game"));
                    return;
                }
                int request = System.Threading.Interlocked.Exchange(ref mapRequest, -1);
                if (request == 0) { matchReady = false; taxView?.Hide(); return; }
                if (!matchReady || request > 0) {
                    var defaults = new MatchOptions(autoEnabled.Value, lower.Value, upper.Value);
                    match = request == 1 ? defaults : MatchOptions.Read(
                        GameMapArchiveManagerAPI.Instance.TryReadTextFile(SaveEntry, true), defaults);
                    matchReady = true; policy.Reset(); lastGameTime = -1; pendingTax = -1; PersistMatch();
                }
                if (!match.Enabled) {
                    policy.Reset(); lastGameTime = -1;
                    SetStatus(ReleaseText.Pick("已关闭", "Off"), ReleaseText.Pick("手动设置税率", "Manual taxes"), false,
                        ReleaseText.Pick("自动调税已关闭，税率保持你的设置。点火漆印重新开启。", "Auto Tax is off; your tax setting is kept. Click the seal to turn it back on."));
                    return;
                }
                var api = GamePlayerManagerAPI.Instance;
                int player = api.GetLocalPlayerId();
                if (player < 1 || player > 8 || !api.TryGetPlayerResourcesById(player, out var resources) || resources == null)
                { policy.Reset(); return; }
                if (player != playerId) { policy.Reset(); playerId = player; lastGameTime = -1; }
                int popularity = (int)resources->r_CurrentPopularity;
                int tax = (int)resources->r_TaxesMode;
                AccountExtra(state.game_time, tax, resources);
                if (pendingTax >= 0)
                {
                    if (tax == pendingTax) pendingTax = -1;
                    else if (Time.unscaledTime - pendingSince < 1f) return;
                    else
                    {
                        // The game did not take the action (e.g. no keep id); write the tax directly.
                        Logger.LogWarning($"Tax action to {pendingTax} not applied; writing it directly.");
                        api.SetTaxesMode(player, (TaxesMode)pendingTax);
                        pendingTax = -1;
                        return;
                    }
                }
                if (state.game_paused != 0) {
                    SetStatus(ReleaseText.Pick("已暂停", "Paused"), PopularityText(popularity), false,
                        ReleaseText.Pick("游戏暂停中，不调税；可以调整目标区间。", "The game is paused, so taxes are left alone; the target can still be changed."));
                    return;
                }
                if (!TaxPolicy.ValidBand(match.Lower, match.Upper))
                {
                    policy.Reset();
                    SetStatus(ReleaseText.Pick("待命", "Idle"), ReleaseText.Pick("目标区间无效", "Invalid target"), true,
                        ReleaseText.Pick("请设置：50 ≤ 下限 < 上限 < 100", "Set 50 <= lower < upper < 100"));
                    return;
                }
                if (resources->r_TotalPopulation <= 3)
                {
                    policy.Reset();
                    SetStatus(ReleaseText.Pick("待命", "Idle"), ReleaseText.Pick("人口过少", "Tiny population"), false,
                        ReleaseText.Pick("人口不超过 3 时游戏有特殊经济规则，自动调税不介入，保留当前税率。", "With 3 or fewer people the game uses special economy rules; the current tax is kept."));
                    return;
                }
                // Both terms come from the SAME exported snapshot. The tax term
                // may still describe the old tax (or the player's UI preview).
                // Subtracting it cancels that contribution without double-counting.
                // Granary selection can preview a different ration. Replace that
                // contribution with the actual committed food settings/stock.
                int correctedTotal = state.upcoming_total_popularity - state.food_popularity +
                    TaxPolicy.FoodEffect((int)resources->r_RationMode, resources->r_FoodStockTotal > 0,
                        (int)resources->r_FoodVarietyAmount);
                int next = policy.Evaluate(state.game_time, popularity,
                    correctedTotal, state.tax_popularity, tax,
                    match.Lower, match.Upper);
                if (next != tax)
                {
                    WriteTax(api, player, next);
                    Logger.LogInfo($"Tax {tax} -> {next}; popularity={popularity / 100.0:0.00}, predictedChange={policy.PredictedChange / 100.0:0.00}, phase={policy.CurrentPhase}");
                    match.CountAdjustment();
                    taxView?.AnimateMove(tax, next, Time.unscaledTime);
                }
                string why = ReleaseText.Pick($"民心 {popularity / 100.0:0.00}，目标 {match.Lower:0.##}–{match.Upper:0.##}，当前税档 {next}，预计每次民心更新 {policy.PredictedChange / 100.0:+0.##;-0.##;0}。",
                    $"Popularity {popularity / 100.0:0.00}, target {match.Lower:0.##}–{match.Upper:0.##}, tax level {next}, expected per update {policy.PredictedChange / 100.0:+0.##;-0.##;0}.");
                if (policy.CurrentPhase == TaxPolicy.Phase.ManualDonation)
                    SetStatus(ReleaseText.Pick("已让位", "Yielding"), ReleaseText.Pick("你选了补贴", "You chose a donation"), true,
                        ReleaseText.Pick("自动调税从不自己发钱。你手动选了补贴，它会保持你的选择；改回免税或征税后重新接管。", "Auto Tax never pays donations itself. It keeps your donation until you return to no tax or taxation."));
                else if (policy.CannotRecover)
                    SetStatus(ReleaseText.Pick("无法恢复", "Can't recover"), PopularityText(popularity), true,
                        ReleaseText.Pick("零税也止不住民心下降，需要处理食物、宗教等其他原因。", "Even no tax cannot stop popularity falling; check food, religion and other causes.") + " " + why);
                else if (policy.CurrentPhase == TaxPolicy.Phase.Harvest)
                    SetStatus(ReleaseText.Pick("收税中", "Collecting"), PopularityText(popularity), false,
                        ReleaseText.Pick("民心高于目标，加税把多余的民心换成金币。", "Popularity is above the target, so the surplus is turned into gold.") + " " + why +
                        (policy.AtTaxLimit ? ReleaseText.Pick(" 已是最高税档，民心仍在上涨。", " Already at the highest tax; popularity still rises.") : ""));
                else if (policy.CurrentPhase == TaxPolicy.Phase.Balanced)
                    SetStatus(ReleaseText.Pick("持平中", "Holding"), PopularityText(popularity), false,
                        ReleaseText.Pick("这一档税刚好让民心不升不降。", "This tax level keeps popularity steady.") + " " + why);
                else
                    SetStatus(ReleaseText.Pick("恢复中", "Recovering"), PopularityText(popularity), false,
                        ReleaseText.Pick("民心到了下限，降税让民心回升，同时尽量少损失税收。", "Popularity reached the lower target; taxes are lowered just enough for it to recover.") + " " + why);
            }
            catch (Exception ex)
            {
                policy.Reset();
                SetStatus(ReleaseText.Pick("待命", "Idle"), ReleaseText.Pick("读取失败", "Read failed"), true,
                    ReleaseText.Pick("读取或调整失败，等待重新观察", "Read or adjustment failed; restarting observation"));
                if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime + 10; Logger.LogError(ex); }
            }
            finally {
                try { RefreshPanel(); }
                catch (Exception ex) {
                    if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime + 10; Logger.LogError(ex); }
                }
            }
        }
    }
}
