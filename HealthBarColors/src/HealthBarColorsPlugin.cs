using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SHCDESE.API;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HealthBarColors
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class HealthBarColorsPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "HealthBarColors_Dongge";
        public const string PluginName = "Health Bar Colors";
        public const string PluginVersion = "1.2.0";

        // 游戏把第二条血条画在 sprRenderer6 上，帧号 = 400 + 血条帧（见 GameMap.addUpdateChimp）。
        private const int SecondaryHealthBarOffset = 400;

        // 原生血条是精灵文件 16 的第 17～27 帧：帧号 = 17 + ⌊10 × 当前血量 / 最大血量⌋（实测）。
        private const int FirstHealthFrame = 17;
        private const int HealthFrameSteps = 10;

        // 原生只在选中时给出血条相对单位的像素偏移；没见过的兵种用实测的通用值。
        private const int DefaultHealthBarX = -11;
        private const int DefaultHealthBarY = 45;

        private static readonly AccessTools.FieldRef<GameMap, Dictionary<int, Chimp>> ChimpsRef =
            AccessTools.FieldRefAccess<GameMap, Dictionary<int, Chimp>>("chimps");

        // 本作会在启动后销毁插件的 GameObject，Unity 的 == null 会把它当成 null，
        // 所以这里一律用引用比较（is null / ?.）。
        private static HealthBarColorsPlugin instance;

        private ConfigEntry<bool> recolorEnabled;
        private ConfigEntry<bool> alwaysShow;
        private ConfigEntry<bool> showInMapEditor; // 地图编辑器里也显示（在编辑器里摆兵对战时看血量）
        private ConfigEntry<Color> selfColor;
        private ConfigEntry<Color> allyColor;
        private ConfigEntry<Color> enemyColor;
        private ConfigEntry<ColorMode> colorMode;
        private ConfigEntry<bool> markLord;

        // 盾牌颜色编号 → 颜色。每个玩家的编号是 SpriteMapping.remapColours[玩家]（下标为玩家编号；玩家在设置里换色时游戏会交换编号）。
        // 颜色取自游戏的玩家调色板 spriteLoader.gmColors（下标即编号，2026-10-09 读出）；mixed 存档核对：
        // remapColours = 0,5,3,4,2,6,1,7,8，本地玩家（编号 5）紫色、玩家 3（编号 4）的单位穿黄色。
        // （单位的 r_SpritePlayerColorId 不能用：同一玩家的单位各不相同。）
        private static readonly Dictionary<int, Color> ShieldColors = new Dictionary<int, Color>
        {
            { 1, Hex(0xCC1C38) },  // 红
            { 2, Hex(0x0F92D4) },  // 蓝
            { 3, Hex(0xF17A1F) },  // 橙
            { 4, Hex(0xFFD223) },  // 黄
            { 5, Hex(0xA728C3) },  // 紫
            { 6, Hex(0x6F6F6F) },  // 灰
            { 7, Hex(0x46FDFB) },  // 浅蓝
            { 8, Hex(0x4DD421) },  // 绿
        };

        private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        private const int GreenShield = 8;
        private readonly int[] playerColorIds = new int[16];
        private float nextColourRefresh;
        private readonly HashSet<int> loggedLords = new HashSet<int>();
        private static readonly AccessTools.FieldRef<int[]> RemapColoursRef =
            AccessTools.StaticFieldRefAccess<int[]>(AccessTools.Field(AccessTools.TypeByName("SpriteMapping"), "remapColours"));

        private HealthBarSprites sprites;
        private readonly Dictionary<int, (int X, int Y)> barOffsets = new Dictionary<int, (int, int)>();
        private int cachedFrame = -1;
        private int localPlayer;
        private bool localValid;
        private float nextErrorLogTime;
        private float nextStatsTime;
        private readonly int[] sideCounts = new int[4];
        private int forcedBars;
        private string lastStats;

        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private static string SettingsXaml()
        {
            string language = GameAssetManagerAPI.Instance.CurrentLanguage ?? "";
            return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "HealthBarColors/Settings.zh.xaml" : "HealthBarColors/Settings.xaml";
        }

        private void Awake()
        {
            instance = this;

            recolorEnabled = Config.Bind("General", "Enabled", true,
                "Color health bars by side (also controlled by Mod Options)");
            alwaysShow = Config.Bind("General", "AlwaysShow", true,
                "Keep health bars visible for owned, allied and enemy units without selection");
            showInMapEditor = Config.Bind("General", "ShowInMapEditor", true,
                "Also color and show health bars in the map editor");
            selfColor = Config.Bind("Colors", "Self", new Color(0.2f, 0.9f, 0.2f, 1f),
                "Own-unit color (RGBA; health bars preserve their original shading). Also used by Range Display if installed");
            allyColor = Config.Bind("Colors", "Ally", new Color(0.2f, 0.5f, 1f, 1f),
                "Allied-unit color (RGBA; original health-bar shading is preserved)");
            enemyColor = Config.Bind("Colors", "Enemy", new Color(1f, 0.15f, 0.15f, 1f),
                "Enemy-unit color (RGBA; original health-bar shading is preserved)");
            colorMode = Config.Bind("General", "ColorMode", ColorMode.Side,
                "Side = own green, allies blue, enemies red. Shield = own green, every other player in their shield color (a green shield borrows your own shield color)");
            markLord = Config.Bind("General", "MarkLord", true,
                "Show lords' health bars a little larger with a crown on top");

            sprites = new HealthBarSprites();
            sprites.Warning += message => Logger.LogWarning(message);
            selfColor.SettingChanged += (_, __) => sprites.Clear();
            allyColor.SettingChanged += (_, __) => sprites.Clear();
            enemyColor.SettingChanged += (_, __) => sprites.Clear();
            colorMode.SettingChanged += (_, __) => sprites.Clear();

            // 本作会销毁 BepInEx 创建的对象，所以不用 MonoBehaviour.Update，
            // 而是挂在游戏每次刷新单位外观（包括血条帧）的函数上：
            // 前置补丁决定是否显示血条，后置补丁给血条换颜色。
            var harmony = new Harmony(PluginGuid);
            harmony.Patch(AccessTools.Method(typeof(GameMap), nameof(GameMap.addUpdateChimp)),
                prefix: new HarmonyMethod(typeof(HealthBarColorsPlugin), nameof(BeforeAddUpdateChimp)),
                postfix: new HarmonyMethod(typeof(HealthBarColorsPlugin), nameof(AfterAddUpdateChimp)));
            harmony.Patch(AccessTools.Method(typeof(GameMap), nameof(GameMap.deleteChimp), new[] { typeof(Chimp), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(HealthBarColorsPlugin), nameof(BeforeDeleteChimp)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new HealthBarColorsSettings(recolorEnabled, alwaysShow, colorMode, markLord), SettingsXaml(), false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Colors: {recolorEnabled.Value}, always show: {alwaysShow.Value}");
        }

        /// <summary>某个玩家的血条颜色；null = 中立或不着色。己方在两种模式下都是绿色。</summary>
        private Color? ColorForPlayer(int owner, Side side)
        {
            switch (side)
            {
                case Side.Self: return selfColor.Value;
                case Side.None: return null;
            }
            if (colorMode.Value == ColorMode.Shield && owner > 0 && owner < 16 && ShieldColors.TryGetValue(playerColorIds[owner], out Color shield))
            {
                // 绿盾会和己方的绿色混淆：借用己方空出来的盾牌颜色（每局各玩家盾牌色不同，所以不会再撞色）。
                if (playerColorIds[owner] == GreenShield && localPlayer > 0 && localPlayer < 16 &&
                    ShieldColors.TryGetValue(playerColorIds[localPlayer], out Color own))
                    return own;
                return shield;
            }
            return side == Side.Ally ? allyColor.Value : enemyColor.Value;
        }

        /// <summary>
        /// 供 Range Display 等 mod 通过反射调用：玩家 player 的血条颜色（Color），中立或未着色时返回 null。
        /// 射程圈用它就能和血条始终同色。
        /// </summary>
        public static object PlayerColor(int player)
        {
            HealthBarColorsPlugin self = instance;
            if (self is null || !self.recolorEnabled.Value) return null;
            self.RefreshLocalPlayer();
            if (!self.localValid) return null;
            self.RefreshPlayerColours();
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            if (!players.IsPlayerIdValid(player)) return null;
            Side side = player == self.localPlayer ? Side.Self : players.IsPlayerAlliedTo(self.localPlayer, player) ? Side.Ally : Side.Enemy;
            Color? color = self.ColorForPlayer(player, side);
            return color.HasValue ? (object)color.Value : null;
        }

        // 每秒从游戏读一次各玩家的盾牌颜色编号；玩家改色或读档后自动更新。
        private void RefreshPlayerColours()
        {
            if (Time.unscaledTime < nextColourRefresh) return;
            nextColourRefresh = Time.unscaledTime + 1f;
            int[] remap = RemapColoursRef();
            if (remap == null) return;
            bool changed = false;
            for (int player = 1; player < playerColorIds.Length && player < remap.Length; player++)
            {
                if (playerColorIds[player] != remap[player]) changed = true;
                playerColorIds[player] = remap[player];
            }
            if (changed)
            {
                Logger.LogInfo("Player shield colours: " + string.Join(", ", remap));
                sprites.Clear();
            }
        }

        private static void BeforeAddUpdateChimp(int _objectID, int _gameObjectType, ref int hpsFrame, ref int hpsX, ref int hpsY)
        {
            instance?.ForceHealthBar(_objectID, _gameObjectType, ref hpsFrame, ref hpsX, ref hpsY);
        }

        private static void AfterAddUpdateChimp(GameMap __instance, int _objectID)
        {
            instance?.OnChimpUpdated(__instance, _objectID);
        }

        // 渲染器会回收到对象池给别的单位用，删除前先换回原图。
        private static void BeforeDeleteChimp(Chimp this_chimp)
        {
            if (instance is null || this_chimp == null)
                return;

            try
            {
                instance.sprites.Restore(this_chimp.sprRenderer5);
                instance.sprites.Restore(this_chimp.sprRenderer6);
            }
            catch (Exception ex)
            {
                instance.LogFrameError(ex);
            }
        }

        private void ForceHealthBar(int unitId, int unitType, ref int hpsFrame, ref int hpsX, ref int hpsY)
        {
            try
            {
                // 原生给出了血条（选中的单位）：记下该兵种的血条位置，原样放行。
                if (hpsFrame != 0)
                {
                    if (hpsFrame >= FirstHealthFrame && hpsFrame <= FirstHealthFrame + HealthFrameSteps)
                        barOffsets[unitType] = (hpsX, hpsY);
                    return;
                }

                if (!alwaysShow.Value || Classify(unitId) == Side.None)
                    return;

                GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
                int max = units.GetMaxHealth(unitId);
                int hp = units.GetCurrentHealth(unitId);
                if (max <= 0 || hp <= 0)
                    return;

                int step = (int)Math.Min(HealthFrameSteps, (long)hp * HealthFrameSteps / max);
                hpsFrame = FirstHealthFrame + step;
                (hpsX, hpsY) = barOffsets.TryGetValue(unitType, out var offset)
                    ? offset
                    : (DefaultHealthBarX, DefaultHealthBarY);
                forcedBars++;
            }
            catch (Exception ex)
            {
                LogFrameError(ex);
            }
        }

        private void OnChimpUpdated(GameMap map, int objectId)
        {
            try
            {
                if (!ChimpsRef(map).TryGetValue(objectId, out Chimp chimp) || chimp == null)
                    return;

                Side side = recolorEnabled.Value ? Classify(objectId) : Side.None;
                sideCounts[(int)side]++;
                int owner = GameUnitManagerAPI.Instance.GetOwner(objectId);
                RefreshPlayerColours();
                Color? color = ColorForPlayer(owner, side);
                // 领主：玩家记录的领主单位（AI 领主的兵种编号可能不同），兵种为领主的也算。
                int unitType = (int)GameUnitManagerAPI.Instance.GetType(objectId);
                bool lord = markLord.Value && side != Side.None && owner > 0 &&
                            (GamePlayerManagerAPI.Instance.GetLordUnitId(owner) == objectId || unitType == (int)SHCDESE.Interop.eChimps.CHIMP_TYPE_LORD);
                if (lord && loggedLords.Add(objectId))
                    Logger.LogInfo($"Lord of player {owner}: unit {objectId}, type {unitType}, image5 {chimp.image5}, image6 {chimp.image6}");
                BarStyle? style = color.HasValue ? new BarStyle(color.Value, lord) : (BarStyle?)null;

                if (chimp.image5 > 0)
                    sprites.Apply(chimp.sprRenderer5, style);
                if (chimp.image6 > SecondaryHealthBarOffset)
                    sprites.Apply(chimp.sprRenderer6, style);

                ReportStats();
            }
            catch (Exception ex)
            {
                LogFrameError(ex);
            }
        }

        private Side Classify(int unitId)
        {
            RefreshLocalPlayer();
            if (!localValid)
                return Side.None;

            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int owner = GameUnitManagerAPI.Instance.GetOwner(unitId);
            if (!players.IsPlayerIdValid(owner))
                return Side.None;
            if (owner == localPlayer)
                return Side.Self;
            return players.IsPlayerAlliedTo(localPlayer, owner) ? Side.Ally : Side.Enemy;
        }

        // 本地玩家每帧只查一次。
        private void RefreshLocalPlayer()
        {
            int frame = Time.frameCount;
            if (frame == cachedFrame)
                return;

            cachedFrame = frame;
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            localPlayer = players.GetLocalPlayerId();
            localValid = players.IsPlayerIdValid(localPlayer) && (showInMapEditor.Value || !players.IsInMapEditor());
        }

        // 每分钟最多一条，只在统计变化时写，方便确认阵营判断和常驻血条是否在工作。
        private void ReportStats()
        {
            if (nextStatsTime == 0f)
                nextStatsTime = Time.unscaledTime + 10f;
            if (Time.unscaledTime < nextStatsTime)
                return;

            nextStatsTime = Time.unscaledTime + 60f;
            string stats = $"local={localPlayer}; self={sideCounts[(int)Side.Self] > 0}; " +
                           $"ally={sideCounts[(int)Side.Ally] > 0}; enemy={sideCounts[(int)Side.Enemy] > 0}; " +
                           $"forcedBars={forcedBars > 0}; knownBarOffsets={barOffsets.Count}; variants={sprites.VariantCount}";
            Array.Clear(sideCounts, 0, sideCounts.Length);
            forcedBars = 0;
            if (stats == lastStats)
                return;

            lastStats = stats;
            Logger.LogInfo($"Stats: {stats}");
        }

        private void LogFrameError(Exception ex)
        {
            if (Time.unscaledTime < nextErrorLogTime)
                return;

            nextErrorLogTime = Time.unscaledTime + 5f;
            Logger.LogError($"Health bar update failed: {ex}");
        }
    }
}
