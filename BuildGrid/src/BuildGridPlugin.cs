using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using System;
using UnityEngine;

namespace BuildGrid
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BuildGridPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "BuildGrid_Dongge";
        public const string PluginName = "Build Grid";
        public const string PluginVersion = "1.0.2";

        // MainControls.CurrentAction 的取值 5 = 正在放置建筑（Enums.editorActions.placingBuilding）。
        private const int PlacingBuildingAction = 5;

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static BuildGridPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> showGrid;
        private ConfigEntry<int> radius;
        private ConfigEntry<Color> lineColor;
        private ConfigEntry<float> lineWidthPixels;

        private volatile bool libraryLoaded;
        private readonly GridRenderer grid = new GridRenderer();
        private bool wasPlacing;
        private bool lastDrawn;
        private float nextErrorLogTime;

        private void Awake()
        {
            instance = this;

            showGrid = Config.Bind("General", "Enabled", true,
                "Show a local grid while placing buildings (also controlled by Mod Options).");
            radius = Config.Bind("General", "Radius", 8,
                new ConfigDescription("Grid radius in tiles around the cursor.", new AcceptableValueRange<int>(3, 15)));
            lineColor = Config.Bind("Appearance", "Color", new Color(1f, 1f, 1f, 1f),
                "Grid line RGBA color; opacity fades away from the cursor.");
            lineWidthPixels = Config.Bind("Appearance", "LineWidthPixels", 2.5f,
                "Line width in screen pixels, independent of zoom.");

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(BuildGridPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new BuildGridSettings(showGrid), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {showGrid.Value}, radius: {radius.Value}");
        }

        private static void AfterKeyManagerUpdate()
        {
            instance?.OnFrame();
        }

        private void OnFrame()
        {
            if (!libraryLoaded)
                return;

            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                grid.HideAll();
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    Logger.LogError($"Build grid frame failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            MainControls controls = MainControls.instance;
            GameMap map = GameMap.instance;
            bool placing = showGrid.Value && controls != null && map != null &&
                           controls.CurrentAction == PlacingBuildingAction;

            if (placing != wasPlacing)
            {
                wasPlacing = placing;
                Logger.LogInfo(placing ? $"Placement started (building {controls.CurrentSubAction})" : "Placement ended");
            }

            if (!placing)
            {
                grid.HideAll();
                return;
            }

            float mx = 0f, my = 0f;
            controls.getMouseMapTilePosition(ref mx, ref my);
            bool drawn = grid.Draw(map, (int)mx, (int)my, radius.Value, lineColor.Value, lineWidthPixels.Value);
            string state = $"mouseTile=({(int)mx},{(int)my}) drawn={drawn}";
            if (drawn != lastDrawn)
            {
                lastDrawn = drawn;
                Logger.LogInfo($"Grid {state}");
            }
        }
    }
}
