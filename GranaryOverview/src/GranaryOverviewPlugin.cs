using BepInEx;
using BepInEx.Configuration;
using CrusaderDE;
using HarmonyLib;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.Interop;
using System;
using System.Collections.Generic;
using System.Reflection;
using Time = UnityEngine.Time;

namespace GranaryOverview
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class GranaryOverviewPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "GranaryOverview_Dongge";
        public const string PluginName = "Granary Overview";
        public const string PluginVersion = "1.0.2";

        private const float PollSeconds = 0.25f;
        private const int GranaryType = 19;

        // 没有实测数据时每座建筑每月的产出和面包链的转换率（实测值见 README）。
        // 苹果园、奶牛场、面包房取自测试存档（原始征程 4，12 个月内的月平均）；猎人、磨坊、小麦农场样本太少，为估计值。
        private const double DefaultAppleFarm = 4, DefaultDairy = 4, DefaultHunter = 4, DefaultBakery = 8,
            DefaultMill = 12, DefaultWheatFarm = 8, DefaultBreadPerFlour = 1, DefaultFlourPerWheat = 1;

        /// <summary>转换率至少要这么多份原料样本才用实测值。</summary>
        private const double MinRatioSamples = 10;

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static GranaryOverviewPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> showOverview;
        private ConfigEntry<bool> logFood;

        private volatile bool libraryLoaded;
        private volatile bool resetRequested = true;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        private readonly FoodLedger ledger = new FoodLedger();
        private readonly FoodModel model = new FoodModel();
        private readonly GranaryView.Snapshot snapshot = new GranaryView.Snapshot();
        private GranaryView view;

        // 月份和每种食物的月初库存、上月净变化（同 Stock Delta）。
        private int currentMonthKey = -1, monthStartTime;
        private readonly int[] monthStartStock = new int[4];
        private readonly Queue<int> monthLengths = new Queue<int>();

        private float nextPoll, nextErrorLogTime, nextModelLog;

        // 调试：上一帧的食物库存和开饭进度。
        private readonly int[] lastStock = new int[4];
        private int lastClock = -1, lastRation = -1;

        private void Awake()
        {
            instance = this;

            showOverview = Config.Bind("General", "Enabled", true, "Combine granary pages and show food balance estimates.");
            logFood = Config.Bind("Debug", "LogFood", false, "Log food changes, meals and estimates to the BepInEx log.");

            ledger.Log = message => { if (logFood.Value) Logger.LogInfo(message); };
            ledger.GameTime = () => GameData.Instance?.lastGameState?.game_time ?? 0;

            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e => { if (e.Phase == EventHookPhase.Post) resetRequested = true; }));
            subscriptions.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(e => { if (e.Phase == EventHookPhase.Post) resetRequested = true; }));
            SubscribeWorkerEvents();
            subscriptions.Add(BuildingR3EventHooks.OnBuildingSpawn.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Post && e.ReturnValue > 0)
                    ledger.OnBuildingBuilt((int)e.ReturnValue);
            }));
            subscriptions.Add(BuildingR3EventHooks.OnGoodsyardAddGood.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre && e.Add)
                    ledger.OnGoodsyardAdd(e.BuildingId, (int)e.Good, e.AddAmount);
            }));

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(GranaryOverviewPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new GranaryOverviewSettings(showOverview), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {showOverview.Value}");
        }

        /// <summary>订阅 UnitR3EventHooks 里所有带 UnitId 的取货、送货、存放事件（与仓库增减配对）。</summary>
        private void SubscribeWorkerEvents()
        {
            MethodInfo subscribe = typeof(GranaryOverviewPlugin).GetMethod(nameof(SubscribeWorkerEvent), BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (FieldInfo field in typeof(UnitR3EventHooks).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.Name.Contains("PickUp") && !field.Name.Contains("DropOff") && !field.Name.Contains("Store"))
                    continue;
                Type fieldType = field.FieldType;
                if (!fieldType.IsGenericType || fieldType.GetGenericTypeDefinition() != typeof(R3EventHook<>))
                    continue;
                Type argsType = fieldType.GetGenericArguments()[0];
                PropertyInfo unitId = argsType.GetProperty("UnitId");
                if (unitId == null || unitId.PropertyType != typeof(int))
                    continue;
                subscriptions.Add((IDisposable)subscribe.MakeGenericMethod(argsType).Invoke(this, new object[] { field.GetValue(null), unitId }));
            }
        }

        private IDisposable SubscribeWorkerEvent<T>(R3EventHook<T> hook, PropertyInfo unitId) where T : EventHookBase
        {
            return hook.Observable.Subscribe(e => ledger.OnWorkerEvent((int)unitId.GetValue(e)));
        }

        private static void AfterKeyManagerUpdate()
        {
            instance?.OnFrame();
        }

        private void OnFrame()
        {
            ledger.NextFrame();
            if (!libraryLoaded)
                return;
            try
            {
                EngineInterface.PlayState s = GameData.Instance?.lastGameState;
                bool inGame = GameMap.instance != null && Director.instance != null && Director.instance.SimRunning && s?.resources != null;
                if (!inGame)
                {
                    view?.Hide();
                    return;
                }
                if (logFood.Value)
                    LogChanges(s);
                if (Time.unscaledTime < nextPoll)
                    return;
                nextPoll = Time.unscaledTime + PollSeconds;
                Tick(s);
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    Logger.LogError($"Granary update failed: {ex}");
                }
            }
        }

        private void Tick(EngineInterface.PlayState s)
        {
            UpdateMonth(s);

            HUD_Buildings hud = MainViewModel.Instance?.HUDBuildingPanel;
            Grid granary = hud?.RefGranaryPanel;
            if (!showOverview.Value || hud == null || !hud.IsVisible || granary == null)
            {
                view?.Hide();
                return;
            }

            // 选中粮仓时若停在食物页（上次看的是那页），切回配给页：两页已合成一页。
            if (!granary.IsVisible && hud.FindName("ReportsFoodPanel") is Grid foodPage && foodPage.IsVisible && SelectedIsGranary(s))
                MainViewModel.Instance.ButtonGranarySwitchModeCommand?.Execute("0");

            if (!granary.IsVisible)
            {
                view?.Hide();
                return;
            }

            if (view == null || !view.IsAttachedTo(granary))
            {
                view?.Hide();
                view = new GranaryView(hud, granary);
            }

            FillModel(s);
            view.Show(snapshot);
        }

        private static unsafe bool SelectedIsGranary(EngineInterface.PlayState s)
        {
            return s.in_structure > 0 && GameBuildingManagerAPI.Instance.TryGetBuildingById(s.in_structure, out GameBuilding* b) &&
                   b != null && (int)b->r_BuildingType == GranaryType;
        }

        private void UpdateMonth(EngineInterface.PlayState s)
        {
            int monthKey = s.year * 12 + s.month;
            if (resetRequested || currentMonthKey < 0)
            {
                resetRequested = false;
                monthLengths.Clear();
                currentMonthKey = monthKey;
                monthStartTime = s.game_time;
                for (int i = 0; i < 4; i++)
                {
                    monthStartStock[i] = s.resources[FoodModel.Foods[i]];
                    snapshot.LastMonth[i] = null;
                }
                ledger.Reset();
                return;
            }
            if (monthKey == currentMonthKey)
                return;

            bool consecutive = monthKey == currentMonthKey + 1;
            for (int i = 0; i < 4; i++)
            {
                int now = s.resources[FoodModel.Foods[i]];
                snapshot.LastMonth[i] = consecutive ? now - monthStartStock[i] : (int?)null;
                monthStartStock[i] = now;
            }
            if (consecutive)
            {
                int length = s.game_time - monthStartTime;
                if (length > 400 && length < 1600)
                {
                    monthLengths.Enqueue(length);
                    while (monthLengths.Count > 6)
                        monthLengths.Dequeue();
                }
            }
            else
            {
                ledger.Reset();
            }
            currentMonthKey = monthKey;
            monthStartTime = s.game_time;
        }

        private void FillModel(EngineInterface.PlayState s)
        {
            double ticksPerMonth = 800;
            if (monthLengths.Count > 0)
            {
                double sum = 0;
                foreach (int l in monthLengths)
                    sum += l;
                ticksPerMonth = sum / monthLengths.Count;
            }
            double fraction = Math.Max(0, Math.Min(1, (s.game_time - monthStartTime) / ticksPerMonth));

            model.Population = s.population;
            model.Ration = s.rationing;
            model.TicksPerMonth = ticksPerMonth;
            model.WheatStock = s.resources[FoodLedger.Wheat];
            model.FlourStock = s.resources[FoodLedger.Flour];
            for (int i = 0; i < 4; i++)
            {
                model.Stock[i] = s.resources[FoodModel.Foods[i]];
                model.Enabled[i] = s.food_types_not_eatable == null || s.food_types_not_eatable[i] == 0;
                snapshot.ThisMonth[i] = model.Stock[i] - monthStartStock[i];
            }

            var apples = ledger.Rates(FoodLedger.AppleFarmType, ticksPerMonth);
            var dairy = ledger.Rates(FoodLedger.DairyType, ticksPerMonth);
            var hunter = ledger.Rates(FoodLedger.HunterType, ticksPerMonth);
            var bakery = ledger.Rates(FoodLedger.BakeryType, ticksPerMonth);
            var mill = ledger.Rates(FoodLedger.MillType, ticksPerMonth);
            var wheat = ledger.Rates(FoodLedger.WheatFarmType, ticksPerMonth);

            model.Production[0] = Get(bakery.PerMonth, FoodLedger.Bread);
            model.Production[1] = Get(dairy.PerMonth, FoodLedger.Cheese);
            model.Production[2] = Get(hunter.PerMonth, FoodLedger.Meat);
            model.Production[3] = Get(apples.PerMonth, FoodLedger.Apples);

            model.AppleFarmRate = PerBuilding(apples, FoodLedger.Apples, DefaultAppleFarm);
            model.DairyRate = PerBuilding(dairy, FoodLedger.Cheese, DefaultDairy);
            model.HunterRate = PerBuilding(hunter, FoodLedger.Meat, DefaultHunter);
            model.BakeryRate = PerBuilding(bakery, FoodLedger.Bread, DefaultBakery);
            model.MillRate = PerBuilding(mill, FoodLedger.Flour, DefaultMill);
            model.WheatFarmRate = PerBuilding(wheat, FoodLedger.Wheat, DefaultWheatFarm);
            model.BreadPerFlour = Ratio(bakery.PerMonth, FoodLedger.Bread, FoodLedger.Flour, DefaultBreadPerFlour);
            model.FlourPerWheat = Ratio(mill.PerMonth, FoodLedger.Flour, FoodLedger.Wheat, DefaultFlourPerWheat);

            model.Compute();

            snapshot.Model = model;
            snapshot.Ready = ledger.HasData(ticksPerMonth);
            snapshot.TotalFood = s.total_food;
            snapshot.RationPopularity = s.rationing_popularity / 25;
            snapshot.TypesPopularity = s.foodsEaten_popularity / 25;

            if (logFood.Value && Time.unscaledTime >= nextModelLog)
            {
                nextModelLog = Time.unscaledTime + 5f;
                Logger.LogInfo($"model ticks/month={ticksPerMonth:0} frac={fraction:0.00} prod(b/c/m/a)={model.Production[0]:0.0}/{model.Production[1]:0.0}/{model.Production[2]:0.0}/{model.Production[3]:0.0} " +
                               $"rates apple={model.AppleFarmRate:0.0}({apples.Buildings}) dairy={model.DairyRate:0.0}({dairy.Buildings}) hunter={model.HunterRate:0.0}({hunter.Buildings}) " +
                               $"bakery={model.BakeryRate:0.0}({bakery.Buildings}) mill={model.MillRate:0.0}({mill.Buildings}) wheat={model.WheatFarmRate:0.0}({wheat.Buildings}) " +
                               $"bread/flour={model.BreadPerFlour:0.00} flour/wheat={model.FlourPerWheat:0.00} " +
                               $"net=[{string.Join(",", Array.ConvertAll(model.Net, n => n.ToString("0.0")))}]");
            }
        }

        private static double Get(Dictionary<int, double> d, int key) => d.TryGetValue(key, out double v) ? v : 0;

        private static double PerBuilding((Dictionary<int, double> PerMonth, int Buildings) r, int good, double fallback)
        {
            double total = Get(r.PerMonth, good);
            return r.Buildings > 0 && total > 0 ? total / r.Buildings : fallback;
        }

        private static double Ratio(Dictionary<int, double> d, int output, int input, double fallback)
        {
            double o = Get(d, output), i = -Get(d, input);
            return o > 0 && i >= MinRatioSamples ? o / i : fallback;
        }

        private void LogChanges(EngineInterface.PlayState s)
        {
            bool changed = s.food_clock < lastClock || s.rationing != lastRation;
            for (int i = 0; i < 4; i++)
                changed |= s.resources[FoodModel.Foods[i]] != lastStock[i];
            if (changed)
            {
                string meal = s.food_clock < lastClock ? " MEAL" : "";
                Logger.LogInfo($"food t{s.game_time} {s.year}/{s.month} clock={lastClock}->{s.food_clock}{meal} ration={s.rationing} pop={s.population} " +
                               $"bread={s.resources[10]} cheese={s.resources[11]} meat={s.resources[12]} apples={s.resources[13]} total={s.total_food} months={s.months_of_food}");
                for (int i = 0; i < 4; i++)
                    lastStock[i] = s.resources[FoodModel.Foods[i]];
                lastRation = s.rationing;
            }
            lastClock = s.food_clock;
        }
    }
}
