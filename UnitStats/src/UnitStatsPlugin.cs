using BepInEx;
using BepInEx.Configuration;
using CrusaderDE;
using HarmonyLib;
using Noesis;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using System;

namespace UnitStats
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class UnitStatsPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "UnitStats_Dongge";
        public const string PluginName = "Unit Stats";
        public const string PluginVersion = "1.0.2";

        // 菜单状态每隔这么久检查一次；表格内容只在菜单切换时重建。
        private const float PollSeconds = 0.25f;

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static UnitStatsPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;
        private string shownLanguage;
        private bool refreshRequested = true;

        private ConfigEntry<bool> showStats;
        private ConfigEntry<float> rightOffset;
        private ConfigEntry<float> bottomOffset;

        private volatile bool libraryLoaded;
        private readonly StatsTable table = new StatsTable();
        private StatsContext shownContext = StatsContext.None;
        private float nextPoll;
        private float nextErrorLogTime;

        private void Awake()
        {
            instance = this;

            showStats = Config.Bind("General", "Enabled", true,
                "Show unit stats above recruitment/build menus (also controlled by Mod Options)");
            rightOffset = Config.Bind("Layout", "RightOffset", 250f,
                "Distance from the right screen edge in UI units (1920x1080 reference)");
            bottomOffset = Config.Bind("Layout", "BottomOffset", 212f,
                "Distance from the bottom screen edge in UI units (1920x1080 reference)");
            showStats.SettingChanged += (_, __) => refreshRequested = true;
            rightOffset.SettingChanged += (_, __) => table.SetOffset(rightOffset.Value, bottomOffset.Value);
            bottomOffset.SettingChanged += (_, __) => table.SetOffset(rightOffset.Value, bottomOffset.Value);

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(UnitStatsPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new UnitStatsSettings(showStats), UnitStatsText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {showStats.Value}");
        }

        private static void AfterKeyManagerUpdate()
        {
            instance?.OnFrame();
        }

        private void OnFrame()
        {
            if (!libraryLoaded || UnityEngine.Time.unscaledTime < nextPoll)
                return;

            nextPoll = UnityEngine.Time.unscaledTime + PollSeconds;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                table.Hide();
                shownContext = StatsContext.None;
                if (UnityEngine.Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = UnityEngine.Time.unscaledTime + 5f;
                    Logger.LogError($"Unit stats update failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            MainViewModel vm = MainViewModel.Instance;
            if (vm == null)
                return;

            EnsureAttached(vm);
            StatsContext context = showStats.Value ? DetectContext(vm) : StatsContext.None;
            string language = GameAssetManagerAPI.Instance.CurrentLanguage ?? "";
            if (!refreshRequested && context == shownContext && language == shownLanguage)
                return;

            shownContext = context;
            shownLanguage = language;
            refreshRequested = false;
            if (context == StatsContext.None)
            {
                table.Hide();
                return;
            }

            table.Show(UnitStatsData.Build(context));
            Logger.LogInfo($"Showing stats for {context}");
        }

        // 属性表挂在 MainHUD 的根 Grid 上：HUD_Buildings → 外层 Grid → 根 Grid。
        // 该根 Grid 同时承载 HUD_Main / HUD_Troops / HUD_Buildings，所以一个表能服务所有菜单。
        private void EnsureAttached(MainViewModel vm)
        {
            HUD_Buildings buildings = vm.HUDBuildingPanel;
            if (buildings == null)
                return;

            Panel root = (buildings.Parent as FrameworkElement)?.Parent as Panel;
            if (root == null)
                return;

            bool changedHost = table.AttachTo(root);
            table.SetOffset(rightOffset.Value, bottomOffset.Value);
            if (changedHost && table.IsAttached)
            {
                refreshRequested = true;
                Logger.LogInfo("Stats table attached to the main HUD.");
            }
        }

        private static StatsContext DetectContext(MainViewModel vm)
        {
            HUD_Buildings b = vm.HUDBuildingPanel;
            if (b != null && b.IsVisible)
            {
                if (IsShown(b.RefBarracksPanel)) return StatsContext.Barracks;
                if (IsShown(b.RefMercPostPanel)) return StatsContext.MercenaryPost;
                if (IsShown(b.RefBedouinStockadePanel)) return StatsContext.BedouinStockade;
                if (IsShown(b.RefShowEngineersGuildPanel)) return StatsContext.EngineersGuild;
                if (IsShown(b.FindName("TunellersGuildPanel"))) return StatsContext.TunnelersGuild;
                if (IsShown(b.FindName("CathedralPanel"))) return StatsContext.Cathedral;
            }

            HUD_Troops t = vm.HUDTroopPanel;
            if (t != null && t.IsVisible && IsShown(t.FindName("UnitBuildCat")))
                return StatsContext.SiegeEngines;

            HUD_Main m = vm.HUDmain;
            if (m != null && m.IsVisible && IsShown(m.FindName("ButtonBuildMangonel")))
                return StatsContext.TowerEngines;

            return StatsContext.None;
        }

        // IsVisible 只有在元素及其所有祖先都可见时才为 true。
        private static bool IsShown(object element)
        {
            return element is UIElement ui && ui.IsVisible;
        }
    }
}
