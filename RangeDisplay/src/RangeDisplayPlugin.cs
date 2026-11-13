using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RangeDisplay
{
    // 攻击射程接口 GameUnitManagerAPI.GetUnitAttackRange 自扩展器 2.10.0 起提供，
    // 2.10.1 修复了该路径上的崩溃，所以最低要求 2.10.1。
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInDependency(HealthBarColorsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RangeDisplayPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        private const string HealthBarColorsGuid = "HealthBarColors_Dongge";
        public const string PluginGuid = "RangeDisplay_Dongge";
        public const string PluginName = "Range Display";
        public const string PluginVersion = "1.4.0";

        // 中立单位（无有效主人）的圈用灰色。
        private static readonly Color NeutralColor = new Color(0.8f, 0.8f, 0.8f, 1f);

        // 本作会在启动后销毁插件的 GameObject，Unity 的 == null 会把它当成 null，
        // 所以这里一律用引用比较（is null / ?.）。
        private static RangeDisplayPlugin instance;

        private ConfigEntry<KeyCode> toggleKey;
        private ConfigEntry<bool> showRings;
        private ConfigEntry<bool> includeSiege;
        private ConfigEntry<bool> includeHealers;
        private ConfigEntry<bool> previewTowers;
        private ConfigEntry<int> maxRings;
        private ConfigEntry<Color> selfColor;
        private ConfigEntry<Color> allyColor;
        private ConfigEntry<Color> enemyColor;
        private ConfigEntry<float> opacity;
        private ConfigEntry<float> lineWidthPixels;
        private ConfigEntry<float> dashPixels;
        private ConfigEntry<float> dotPixels;

        private volatile bool libraryLoaded;
        private bool colorsResolved;
        private RangeRingRenderer ringRenderer;
        private float nextErrorLogTime;
        private string lastState;
        private readonly List<RangeRing> rings = new List<RangeRing>();
        private readonly HashSet<long> ringKeys = new HashSet<long>();
        private readonly List<int> candidates = new List<int>();
        private readonly HashSet<int> ownSelected = new HashSet<int>();

        internal static ManualLogSource Log => instance?.Logger;

        /// <summary>Mod Options 面板：游戏语言为中文时用中文版，否则用英文版。</summary>
        private static string SettingsXaml()
        {
            string language = GameAssetManagerAPI.Instance.CurrentLanguage ?? "";
            return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "RangeDisplay/Settings.zh.xaml"
                : "RangeDisplay/Settings.xaml";
        }

        private void Awake()
        {
            instance = this;

            toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8,
                "Key that toggles the range rings");
            showRings = Config.Bind("General", "Enabled", true,
                "Show range rings (the Mod Options panel and the toggle key change this)");
            includeSiege = Config.Bind("General", "IncludeSiegeEngines", true,
                "Also show the range of siege engines (catapults, trebuchets, mangonels, ballistas, fire ballistas)");
            includeHealers = Config.Bind("General", "IncludeHealers", true,
                "Also show the Healer's search range: a dotted circle of 4 / 7 / 10 tiles by stance (Hold / Defensive / Aggressive); " +
                "a Healer on a tower outlines the whole tower instead");
            previewTowers = Config.Bind("General", "PreviewTowerRange", true,
                "While placing a tower, preview the range of archers on it; for square and round towers also the mangonel and " +
                "ballista ranges (when siege engines are included)");
            maxRings = Config.Bind("General", "MaxRings", 80,
                "Maximum rings drawn at once (units on the same tile with the same range and side share one ring)");
            selfColor = Config.Bind("Appearance", "SelfColor", new Color(0.2f, 0.9f, 0.2f, 1f),
                "Ring color for your own units. With Health Bar Colors installed its health bar colors are used instead");
            allyColor = Config.Bind("Appearance", "AllyColor", new Color(0.2f, 0.5f, 1f, 1f),
                "Ring color for allied units (see above)");
            enemyColor = Config.Bind("Appearance", "EnemyColor", new Color(1f, 0.15f, 0.15f, 1f),
                "Ring color for enemy units (see above)");
            opacity = Config.Bind("Appearance", "Opacity", 0.6f,
                "Ring opacity (0-1)");
            lineWidthPixels = Config.Bind("Appearance", "LineWidthPixels", 2.5f,
                "Line width in screen pixels (independent of zoom)");
            dashPixels = Config.Bind("Appearance", "DashLengthPixels", 18f,
                "Length of one dash plus gap in screen pixels (independent of zoom)");
            dotPixels = Config.Bind("Appearance", "DotSpacingPixels", 9f,
                "Length of one dot plus gap of the Healer's dotted line in screen pixels (independent of zoom)");

            ringRenderer = new RangeRingRenderer();

            CrusaderLibrary.Instance.LibraryLoaded += OnLibraryLoaded;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会在启动后销毁 BepInEx 创建的对象，MonoBehaviour.Update 不可靠。
            // 与 CrusaderHotkeys 一样挂在游戏自己的 KeyManager.Update 上，每帧都会执行。
            new Harmony(PluginGuid).Patch(
                AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(RangeDisplayPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new RangeDisplaySettings(showRings, includeSiege, includeHealers, previewTowers), SettingsXaml(), false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {showRings.Value}, toggle key: {toggleKey.Value}");
        }

        private void OnLibraryLoaded(CrusaderLibraryLoadContext context)
        {
            libraryLoaded = true;
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
#if RANGE_TEST
                if (GameMap.instance != null && Director.instance != null && Director.instance.SimRunning)
                    RangeTest.Tick(Logger);
#endif
            }
            catch (Exception ex)
            {
                ringRenderer.HideAll();
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    Logger.LogError($"Range Display frame failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            bool inGame = GameMap.instance != null &&
                          Director.instance != null &&
                          Director.instance.SimRunning;

            if (inGame && toggleKey.Value != KeyCode.None && Input.GetKeyDown(toggleKey.Value) &&
                (FatControler.instance == null || !FatControler.instance.NoesisHasKeyboard))
            {
                showRings.Value = !showRings.Value;
            }

            if (!inGame || !showRings.Value)
            {
                ReportState(inGame ? "disabled" : "not in game");
                ringRenderer.HideAll();
                return;
            }

            ResolveSharedColors();
            UpdateClickSelection();
            int candidateCount = CollectRings();
            AddTowerPreview();
            int drawn = ringRenderer.Draw(rings, lineWidthPixels.Value, dashPixels.Value, dotPixels.Value);
            ReportState($"in game; candidates={candidateCount}; rings={rings.Count}; drawn={drawn}; otherSelected={otherSelection.Selected.Count}");
        }

        // 装了 Health Bar Colors 就直接用它的颜色配置项，两个 mod 的颜色始终一致（包括运行中修改）。
        private void ResolveSharedColors()
        {
            if (colorsResolved)
                return;

            colorsResolved = true;
            if (!Chainloader.PluginInfos.TryGetValue(HealthBarColorsGuid, out PluginInfo info))
            {
                Logger.LogInfo("Health Bar Colors not installed; using this mod's ring colors.");
                return;
            }

            if (info.Instance is null)
            {
                Logger.LogWarning("Health Bar Colors has no plugin instance; using this mod's ring colors.");
                return;
            }

            // 新版 Health Bar Colors 提供按玩家查颜色（盾牌色模式下每个玩家颜色不同），射程圈和血条同色。
            sharedPlayerColor = info.Instance.GetType().GetMethod("PlayerColor", new[] { typeof(int) });

            ConfigFile shared = info.Instance.Config;
            if (shared.TryGetEntry("Colors", "Self", out ConfigEntry<Color> self) &&
                shared.TryGetEntry("Colors", "Ally", out ConfigEntry<Color> ally) &&
                shared.TryGetEntry("Colors", "Enemy", out ConfigEntry<Color> enemy))
            {
                selfColor = self;
                allyColor = ally;
                enemyColor = enemy;
                Logger.LogInfo("Using ring colors from Health Bar Colors.");
            }
            else
            {
                Logger.LogWarning("Health Bar Colors color settings not found; using this mod's ring colors.");
            }
        }

        // 只在状态变化时写日志，方便排查又不刷屏。
        private void ReportState(string state)
        {
            if (state == lastState)
                return;

            lastState = state;
            Logger.LogInfo($"State: {state}");
        }

        private readonly OtherUnitSelection otherSelection = new OtherUnitSelection();

        private void UpdateClickSelection()
        {
            otherSelection.Update(GameMap.instance);
            if (otherSelection.ChangedThisFrame)
                LogOtherSelection();
        }

        // 诊断：记录每个选中单位的落脚点（anchor）与射程圈圆心在屏幕上的位置。
        // 地面单位两者重合；塔上、城墙上的单位 x 相同、圆心在其正下方的地面（塔基），差值即塔高。
        private void LogOtherSelection()
        {
            GameMap map = GameMap.instance;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            Camera camera = Camera.main;
            var parts = new List<string>();
            foreach (int id in otherSelection.Selected)
            {
                UnmanagedVector2<ushort> tile = units.GetCurrentLocalTilePosition(id);
                string sprite = OtherUnitSelection.TryGetScreenRect(map, id, out Rect rect)
                    ? $"({rect.center.x:F0},{rect.center.y:F0})" : "?";
                string anchor = OtherUnitSelection.TryGetScreenAnchor(map, id, out Vector2 feet)
                    ? $"({feet.x:F0},{feet.y:F0})" : "?";
                string ring = "?";
                if (camera != null && MainControls.instance != null)
                {
                    map.mapGameTileToTilemapCoord(tile.X, tile.Y, out int mx, out int my);
                    Vector3 world = MainControls.instance.getCellCentre(mx, my);
                    world.y += RangeRingRenderer.GroundHeight(map, GameTileManagerAPI.Instance, EngineInterface.FlattenedLandscape, tile.X, tile.Y);
                    Vector3 screen = camera.WorldToScreenPoint(world);
                    ring = $"({screen.x:F0},{screen.y:F0})";
                }
                parts.Add($"id={id} type={units.GetType(id)} owner={units.GetOwner(id)} tile=({tile.X},{tile.Y}) sprite={sprite} anchor={anchor} ringCenter={ring}");
            }

            Vector3 mouse = Input.mousePosition;
            Logger.LogInfo($"Other-unit selection ({parts.Count}) mouse=({mouse.x:F0},{mouse.y:F0}): " +
                           (parts.Count == 0 ? "none" : string.Join(" | ", parts)));
        }

        /// <returns>候选单位数（选中的己方部队 + 点中的友方/敌方单位）。</returns>
        private int CollectRings()
        {
            rings.Clear();
            ringKeys.Clear();
            candidates.Clear();
            ownSelected.Clear();

            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            // 己方选中部队读游戏自己的状态副本（lastGameState.selectedChimps）。
            // 扩展器的 GetSelectedChimps 依赖本地玩家 ID，在部分对局里取不到 ID 时会返回空，射程圈就不显示了。
            var state = GameData.Instance?.lastGameState;
            if (state != null && state.selectedChimps != null)
            {
                int count = Math.Min(state.numSelectedChimps, state.selectedChimps.Length);
                for (int i = 0; i < count; i++)
                {
                    int id = state.selectedChimps[i];
                    if (id > 0 && ownSelected.Add(id))
                        candidates.Add(id);
                }
            }

            foreach (int id in otherSelection.Selected)
            {
                if (!candidates.Contains(id))
                    candidates.Add(id);
            }

            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int limit = Math.Max(0, maxRings.Value);
            int localPlayer = players.GetLocalPlayerId();

            foreach (int unitId in candidates)
            {
                if (rings.Count >= limit)
                    break;

                eChimps type = units.GetType(unitId);
                RangedKind kind = RangedUnitTypes.Classify(type);
                if (kind == RangedKind.None ||
                    (kind == RangedKind.Siege && !includeSiege.Value) ||
                    (kind == RangedKind.Healer && !includeHealers.Value))
                    continue;

                UnmanagedVector2<ushort> tile = units.GetCurrentLocalTilePosition(unitId);
                int x = tile.X;
                int y = tile.Y;
                if (x <= 0 || y <= 0)
                    continue;

                // 游戏自己的部队选择里只会有己方单位。
                int owner = units.GetOwner(unitId);
                int side = ownSelected.Contains(unitId) ? 1 : SideOf(players, owner, localPlayer);
                ringOwner = side == 1 ? localPlayer : owner;

                if (kind == RangedKind.Healer)
                {
                    AddHealerRing(units, unitId, x, y, side);
                    continue;
                }

                // 原生射程判定只比较水平格距（dx² + dy² ≤ R²），不看高度，所以塔上与平地的圈一样大。
                int range = units.GetUnitAttackRange(type);
                if (range <= 0)
                    continue;

                // 同一格、同射程、同阵营的单位（例如塔上一群弓箭手）只画一个圈。
                if (!ringKeys.Add(CircleKey(x, y, side, range, RingStyle.Dashed)))
                    continue;

                // 判定比较的是格中心之间的距离，被覆盖格子的外轮廓约在 R + 0.5 处。
                rings.Add(RangeRing.Circle(x, y, range + 0.5f, ColorOf(side), RingStyle.Dashed));
            }

            return candidates.Count;
        }

        private string lastPreview;

        /// <summary>
        /// 放置塔楼时，预览塔上射手的射程：弓箭手用虚线；方塔、圆塔再用长虚线画散射机、弩炮。
        /// 射程只看水平格距（地形、塔高都不影响），
        /// 塔顶有多格，射手可站任一格，所以画占地内每格射程圈的外包络。
        /// </summary>
        private void AddTowerPreview()
        {
            if (!previewTowers.Value ||
                !TowerPlacementPreview.TryGet(out int tower, out int minX, out int minY, out int maxX, out int maxY))
            {
                if (lastPreview != null)
                {
                    lastPreview = null;
                    Logger.LogInfo("Tower placement preview ended");
                }
                return;
            }

            bool engines = includeSiege.Value && TowerPlacementPreview.SupportsEngines(tower);
            string preview = $"tower {tower} footprint ({minX},{minY})-({maxX},{maxY}) engines={engines}";
            if (preview != lastPreview)
            {
                lastPreview = preview;
                Logger.LogInfo($"Tower placement preview: {preview}");
            }

            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            Color color = ColorOf(1);
            AddEnvelope(units, eChimps.CHIMP_TYPE_ARCHER, minX, minY, maxX, maxY, color, RingStyle.Dashed, RingIcon.Archer);
            if (engines)
            {
                AddEnvelope(units, eChimps.CHIMP_TYPE_MANGONEL, minX, minY, maxX, maxY, color, RingStyle.LongDash, RingIcon.Mangonel);
                AddEnvelope(units, eChimps.CHIMP_TYPE_BALLISTA, minX, minY, maxX, maxY, color, RingStyle.LongDash, RingIcon.Ballista);
            }
        }

        private void AddEnvelope(GameUnitManagerAPI units, eChimps type, int minX, int minY, int maxX, int maxY, Color color, RingStyle style, RingIcon icon)
        {
            int range = units.GetUnitAttackRange(type);
            if (range > 0)
                rings.Add(RangeRing.Envelope(minX, minY, maxX, maxY, range + 0.5f, color, style, icon));
        }

        private static long CircleKey(int x, int y, int side, int radius, RingStyle style) =>
            ((long)x << 40) | ((long)y << 20) | ((long)style << 18) | ((long)side << 16) | (uint)(radius & 0xFFFF);

        /// <summary>
        /// 军医搜索伤员的范围（CrusaderDE.dll 0x18DE10）：
        /// 按所在部队姿态，原地 / 防守 / 进攻 = 32 / 56 / 80 世界单位，即 4 / 7 / 10 格，不在部队里按 4 格；
        /// 距离是连续距离，所以圈画在正好 R 格处。
        /// 军医站在 TOWER1–5 上时，同一座塔上的伤员不受距离限制，改为框出整座塔；
        /// 地面上的人与塔顶高度差 ≥ 148，超过治疗允许的 65，圈对他们没有意义。
        /// </summary>
        private void AddHealerRing(GameUnitManagerAPI units, int unitId, int x, int y, int side)
        {
            Color color = ColorOf(side);
            if (TowerFootprint.TryFind(x, y, out int buildingId, out int minX, out int minY, out int maxX, out int maxY))
            {
                long towerKey = (1L << 62) | ((long)buildingId << 8) | (uint)side;
                if (ringKeys.Add(towerKey))
                    rings.Add(RangeRing.Footprint(minX, minY, maxX, maxY, color, RingStyle.Dotted));
                return;
            }

            int radius = HealerSearchRadius(units.GetTribe(unitId));
            if (ringKeys.Add(CircleKey(x, y, side, radius, RingStyle.Dotted)))
                rings.Add(RangeRing.Circle(x, y, radius, color, RingStyle.Dotted));
        }

        private static int HealerSearchRadius(int tribe)
        {
            if (tribe <= 0)
                return 4;

            switch (GameTribeManagerAPI.Instance.GetStance(tribe))
            {
                case TribeStance.Defensive: return 7;
                case TribeStance.Aggressive: return 10;
                default: return 4;
            }
        }

        // 0 = 中立，1 = 己方，2 = 友方，3 = 敌方。
        private static int SideOf(GamePlayerManagerAPI players, int owner, int localPlayer)
        {
            if (!players.IsPlayerIdValid(owner) || !players.IsPlayerIdValid(localPlayer))
                return 0;
            if (owner == localPlayer)
                return 1;
            return players.IsPlayerAlliedTo(localPlayer, owner) ? 2 : 3;
        }

        // 当前这个圈所属的玩家（ColorOf 用它向 Health Bar Colors 查按玩家的颜色）。
        private int ringOwner;
        private System.Reflection.MethodInfo sharedPlayerColor;

        private Color ColorOf(int side)
        {
            Color color;
            object shared = null;
            if (sharedPlayerColor != null && (side == 2 || side == 3)) // 己方两种模式都是绿色，直接用共享的 Self 颜色
            {
                try { shared = sharedPlayerColor.Invoke(null, new object[] { ringOwner }); }
                catch (Exception) { sharedPlayerColor = null; }
            }
            if (shared is Color playerColor)
            {
                color = playerColor;
                color.a = Mathf.Clamp01(opacity.Value);
                return color;
            }
            switch (side)
            {
                case 1: color = selfColor.Value; break;
                case 2: color = allyColor.Value; break;
                case 3: color = enemyColor.Value; break;
                default: color = NeutralColor; break;
            }

            color.a = Mathf.Clamp01(opacity.Value);
            return color;
        }
    }
}
