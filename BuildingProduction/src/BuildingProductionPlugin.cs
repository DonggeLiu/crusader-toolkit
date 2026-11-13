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

namespace BuildingProduction
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BuildingProductionPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "BuildingProduction_Dongge";
        public const string PluginName = "Building Production";
        public const string PluginVersion = "1.0.2";

        private const float PollSeconds = 0.5f;

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static BuildingProductionPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> showProduction;
        private ConfigEntry<bool> debugLog;

        private volatile bool libraryLoaded;
        private volatile bool resetRequested = true;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        private readonly ProductionLedger ledger = new ProductionLedger();
        private ProductionPanel panel;

        private int currentMonthKey = -1;
        private int lastLoggedSelection = -1;
        private float nextStockLog;
        private const float DairyPollSeconds = 2f;
        private float nextDairyPoll;
        private readonly System.Diagnostics.Stopwatch pollWatch = new System.Diagnostics.Stopwatch();
        private double pollTotal;
        private int pollCount;
        private float nextPoll;
        private float nextErrorLogTime;

        private void Awake()
        {
            instance = this;

            showProduction = Config.Bind("General", "Enabled", true,
                "Show actual consumption and output on production building panels.");
            debugLog = Config.Bind("Debug", "LogEvents", false,
                "Log stock events and statistics for troubleshooting.");

            ledger.Log = message => { if (debugLog.Value) Logger.LogInfo(message); };
            ledger.GameTime = () => GameData.Instance?.lastGameState?.game_time ?? 0;

            // 新对局、读档都重新开始统计。
            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Post)
                    resetRequested = true;
            }));
            subscriptions.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Post)
                    resetRequested = true;
            }));

            // 工人送货、取货事件（只带 UnitId）与同一帧仓库的增减配对（见 ProductionLedger）。
            int workerEvents = SubscribeWorkerEvents();
            // 对局中新建的建筑：建成的那个月不完整，不计入月平均。
            subscriptions.Add(BuildingR3EventHooks.OnBuildingSpawn.Observable.Subscribe(e =>
            {
                if (e.Phase != EventHookPhase.Post || e.ReturnValue <= 0 ||
                    e.PlayerId != GamePlayerManagerAPI.Instance.GetLocalPlayerId())
                    return;
                if (debugLog.Value)
                    Logger.LogInfo($"[f{ledger.Frame}] building spawned id={e.ReturnValue} type={e.Building}");
                ledger.OnBuildingBuilt((int)e.ReturnValue);
            }));
            subscriptions.Add(BuildingR3EventHooks.OnGoodsyardAddGood.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre && e.Add)
                    ledger.OnGoodsyardAdd(e.BuildingId, (int)e.Good, e.AddAmount, e.Capacity);
            }));

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(BuildingProductionPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new BuildingProductionSettings(showProduction), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Worker events: {workerEvents}. Enabled: {showProduction.Value}");
        }

        /// <summary>订阅 UnitR3EventHooks 里所有带 UnitId 的取货、送货、存放事件。</summary>
        private int SubscribeWorkerEvents()
        {
            MethodInfo subscribe = typeof(BuildingProductionPlugin).GetMethod(nameof(SubscribeWorkerEvent),
                BindingFlags.NonPublic | BindingFlags.Instance);
            int count = 0;
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

                subscriptions.Add((IDisposable)subscribe.MakeGenericMethod(argsType)
                    .Invoke(this, new object[] { field.GetValue(null), field.Name, unitId }));
                count++;
            }
            return count;
        }

        private IDisposable SubscribeWorkerEvent<T>(R3EventHook<T> hook, string name, PropertyInfo unitId)
            where T : EventHookBase
        {
            return hook.Observable.Subscribe(e => ledger.OnWorkerEvent(name, (int)unitId.GetValue(e)));
        }

        private static unsafe bool HasLocalBuilding(int type)
        {
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            foreach (int id in GameBuildingManagerAPI.Instance.GetAllAliveBuildings())
            {
                if (GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out GameBuilding* b) && b != null &&
                    (int)b->r_BuildingType == type && b->r_PlayerIdOwner == player)
                    return true;
            }
            return false;
        }

        private static void AfterKeyManagerUpdate()
        {
            instance?.OnFrame();
        }

        private void OnFrame()
        {
            ledger.NextFrame();
            if (!libraryLoaded || Time.unscaledTime < nextPoll)
                return;

            nextPoll = Time.unscaledTime + PollSeconds;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    Logger.LogError($"Building production update failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            bool inGame = GameMap.instance != null && Director.instance != null && Director.instance.SimRunning &&
                          state != null;
            if (!inGame)
            {
                panel?.Hide();
                return;
            }

            UpdateMonth(state);
            // 牛只会被皮革作坊牵走：有皮革作坊时才每 2 秒数一次各奶牛场的牛。
            pollWatch.Restart();
            if (Time.unscaledTime >= nextDairyPoll)
            {
                nextDairyPoll = Time.unscaledTime + DairyPollSeconds;
                if (HasLocalBuilding(ProductionGoods.TannerType))
                    ledger.PollDairies();
            }
            pollWatch.Stop();
            pollTotal += pollWatch.Elapsed.TotalMilliseconds;
            pollCount++;
            if (debugLog.Value && pollCount >= 60)
            {
                Logger.LogInfo($"PollDairies avg {pollTotal / pollCount:0.000} ms over {pollCount} polls");
                pollTotal = 0;
                pollCount = 0;
            }

            if (!showProduction.Value)
            {
                panel?.Hide();
                return;
            }

            UpdatePanel(state);
        }

        private void UpdateMonth(EngineInterface.PlayState state)
        {
            int monthKey = state.year * 12 + state.month;
            if (resetRequested || currentMonthKey < 0)
            {
                resetRequested = false;
                currentMonthKey = monthKey;
                ledger.Reset(monthKey);
                Logger.LogInfo($"Production tracking started at {state.year}/{state.month}.");
                return;
            }

            if (monthKey == currentMonthKey)
                return;

            // 若一次跳过了多个月（例如读档后首帧），当作重新开始统计。
            if (monthKey == currentMonthKey + 1)
                ledger.SetMonth(monthKey);
            else
                ledger.Reset(monthKey);
            currentMonthKey = monthKey;
            if (debugLog.Value)
                Logger.LogInfo($"month {state.year}/{state.month} at {DateTime.Now:HH:mm:ss.f} t{state.game_time}");
        }

        private void UpdatePanel(EngineInterface.PlayState state)
        {
            HUD_Buildings hud = MainViewModel.Instance?.HUDBuildingPanel;
            if (hud == null || !hud.IsVisible || !(hud.FindName("BuildingPanel") is Grid buildingPanel) ||
                !buildingPanel.IsVisible)
            {
                panel?.Hide();
                return;
            }

            int buildingId = state.in_structure;
            if (debugLog.Value && buildingId != lastLoggedSelection)
            {
                lastLoggedSelection = buildingId;
                ProductionLedger.TryGetBuildingType(buildingId, out int t);
                Logger.LogInfo($"selected in_structure={buildingId} in_structure_type={state.in_structure_type} type={t}");
            }
            if (!ProductionLedger.TryGetBuildingType(buildingId, out int type) ||
                !ProductionGoods.TryGet(type, out ProductionGoods goods))
            {
                panel?.Hide();
                return;
            }

            if (panel == null || !panel.IsAttachedTo(buildingPanel))
            {
                panel?.Hide();
                panel = new ProductionPanel(buildingPanel, hud);
            }

            // 采石场的数据记在它的石料堆上。
            int dataId = buildingId, dataType = type;
            float fill = -1f;
            if (type == ProductionGoods.QuarryType)
            {
                dataId = ProductionLedger.QuarryPileId(buildingId);
                dataType = ProductionGoods.QuarryPileType;
                int amount = ProductionLedger.LocalStock(dataId, ProductionGoods.Stone).Amount;
                int max = ledger.PileCapacity(dataId);
                fill = max > 0 ? Math.Min(1f, (float)amount / max) : 0f;
            }
            else if (type == ProductionGoods.DairyType && !HasLocalBuilding(ProductionGoods.TannerType))
            {
                fill = Math.Min(1f, ledger.CountHerd(buildingId) / (float)ProductionGoods.DairyHerdMax);
            }

            if (debugLog.Value && Time.unscaledTime >= nextStockLog)
            {
                nextStockLog = Time.unscaledTime + 5f;
                Logger.LogInfo($"selected {buildingId} type={type} data={dataId}: {ProductionLedger.DebugStock(dataId)} pileCap={ledger.PileCapacity(dataId)} fill={fill}");
            }

            panel.Show(goods, ledger.Snapshot(dataId, dataType), fill);
        }
    }
}
