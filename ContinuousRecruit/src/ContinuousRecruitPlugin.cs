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
using System;
using System.Collections.Generic;
using System.Linq;
using Input = UnityEngine.Input;
using KeyCode = UnityEngine.KeyCode;
using Time = UnityEngine.Time;

namespace ContinuousRecruit
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ContinuousRecruitPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "ContinuousRecruit_Dongge";
        public const string PluginName = "Continuous Recruit";
        public const string PluginVersion = "1.0.2";

        private const string BadgeText = "∞";

        /// <summary>
        /// 各招募面板（按 app_sub_mode）里兵种按钮的顺序，即 Shift+Alt+数字 的槽位顺序。
        /// 与游戏界面 HUD_Buildings.xaml 中的按钮顺序、CrusaderHotkeys 的 Alt+数字槽位一致。
        /// </summary>
        private static readonly Dictionary<int, RecruitPanel> Panels = new Dictionary<int, RecruitPanel>
        {
            [1] = new RecruitPanel("BarracksPanel", "CHIMP_TYPE_ARCHER", "CHIMP_TYPE_SPEARMAN", "CHIMP_TYPE_MACEMAN",
                "CHIMP_TYPE_XBOWMAN", "CHIMP_TYPE_PIKEMAN", "CHIMP_TYPE_SWORDSMAN", "CHIMP_TYPE_KNIGHT"),
            [44] = new RecruitPanel("MercPostPanel", "CHIMP_TYPE_ARAB_BOW", "CHIMP_TYPE_ARAB_SLAVE", "CHIMP_TYPE_ARAB_SLINGER",
                "CHIMP_TYPE_ARAB_ASSASIN", "CHIMP_TYPE_ARAB_HORSEMAN", "CHIMP_TYPE_ARAB_SWORDSMAN", "CHIMP_TYPE_ARAB_GRENADIER"),
            [63] = new RecruitPanel("BedouinStockadePanel", "CHIMP_TYPE_BEDOUIN_CAMEL_LANCER", "CHIMP_TYPE_BEDOUIN_HEALER",
                "CHIMP_TYPE_BEDOUIN_EUNUCH", "CHIMP_TYPE_BEDOUIN_AMBUSHER", "CHIMP_TYPE_BEDOUIN_SKIRMISHER",
                "CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL", "CHIMP_TYPE_BEDOUIN_SAPPER", "CHIMP_TYPE_BEDOUIN_DEMOLISHER"),
            [23] = new RecruitPanel("EngineersGuildPanel", "CHIMP_TYPE_ENGINEER", "CHIMP_TYPE_LADDERMAN"),
            [24] = new RecruitPanel("TunellersGuildPanel", "CHIMP_TYPE_TUNNELER"),
            [96] = new RecruitPanel("CathedralPanel", "CHIMP_TYPE_MONK"),
        };

        private static readonly KeyCode[] SlotKeys =
        {
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8,
        };

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static ContinuousRecruitPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> featureEnabled;
        private ConfigEntry<float> recruitIntervalSeconds;

        private volatile bool libraryLoaded;
        private volatile bool resetRequested;
        // 开启连续生产的兵种（按游戏界面的 CHIMP_TYPE_* 名称），保持开启的先后顺序。
        private readonly List<string> active = new List<string>();
        private readonly Dictionary<Button, TextBlock> badges = new Dictionary<Button, TextBlock>();
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private float nextRecruit;
        private float nextErrorLogTime;

        private void Awake()
        {
            instance = this;

            featureEnabled = Config.Bind("General", "Enabled", true,
                "Enable continuous recruitment. Disabling clears all active troop types.");
            recruitIntervalSeconds = Config.Bind("General", "RecruitIntervalSeconds", 1f,
                new ConfigDescription("Seconds between attempts to recruit one unit of each active type.", new AcceptableValueRange<float>(0.25f, 10f)));
            featureEnabled.SettingChanged += (_, __) =>
            {
                if (!featureEnabled.Value)
                    resetRequested = true;
            };

            // 每局开始时全部关闭（新对局、读档都算）。
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

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(ContinuousRecruitPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new ContinuousRecruitSettings(featureEnabled), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {featureEnabled.Value}");
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
                if (Time.unscaledTime >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.unscaledTime + 5f;
                    Logger.LogError($"Continuous recruit update failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            if (resetRequested)
            {
                resetRequested = false;
                if (active.Count > 0)
                    Logger.LogInfo("Continuous recruit reset: all unit types off.");
                active.Clear();
            }

            bool inGame = GameMap.instance != null && Director.instance != null && Director.instance.SimRunning;
            if (!inGame || !featureEnabled.Value)
            {
                UpdateBadges(null, null);
                return;
            }

            (RecruitPanel panel, Grid grid) = VisiblePanel();
            HandleToggleInput(panel, grid);
            UpdateBadges(panel, grid);

            if (active.Count > 0 && !Director.instance.Paused && Time.unscaledTime >= nextRecruit)
            {
                nextRecruit = Time.unscaledTime + recruitIntervalSeconds.Value;
                RecruitOnce();
            }
        }

        // 原版招募命令：与点击头像相同（MainViewModel.ButtonCreateTroop 最终发出的就是它），
        // 金币、武器、空闲农民、人口等条件由游戏自己判断，不满足时不会招募。
        private void RecruitOnce()
        {
            foreach (string chimp in active)
            {
                if (Enum.TryParse(chimp, out Enums.eChimps type))
                    EngineInterface.GameAction(Enums.GameActionCommand.MakeTroop, 1, (int)type);
            }
        }

        private (RecruitPanel Panel, Grid Grid) VisiblePanel()
        {
            HUD_Buildings hud = MainViewModel.Instance?.HUDBuildingPanel;
            GameData data = GameData.Instance;
            if (hud == null || data == null || !hud.IsVisible || !Panels.TryGetValue(data.app_sub_mode, out RecruitPanel panel))
                return (null, null);

            Grid grid = hud.FindName(panel.GridName) as Grid;
            return grid != null && grid.IsVisible ? (panel, grid) : (null, null);
        }

        private void HandleToggleInput(RecruitPanel panel, Grid grid)
        {
            if (panel == null)
                return;

            // 右键点兵种头像。
            if (Input.GetMouseButtonDown(1))
            {
                foreach (Button button in RecruitButtons(grid))
                {
                    if (button.IsMouseOver)
                    {
                        Toggle((string)button.CommandParameter);
                        break;
                    }
                }
            }

            // Shift+Alt+数字：切换当前面板第 N 个兵种（与 Alt+数字招募的槽位一致）。
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!shift || !alt || ctrl)
                return;

            for (int slot = 0; slot < SlotKeys.Length && slot < panel.Units.Length; slot++)
            {
                if (Input.GetKeyDown(SlotKeys[slot]))
                    Toggle(panel.Units[slot]);
            }
        }

        private void Toggle(string chimp)
        {
            if (string.IsNullOrEmpty(chimp))
                return;

            if (active.Remove(chimp))
            {
                Logger.LogInfo($"Continuous recruit OFF: {chimp}");
                return;
            }

            active.Add(chimp);
            nextRecruit = 0f;
            Logger.LogInfo($"Continuous recruit ON: {chimp}");
        }

        private static IEnumerable<Button> RecruitButtons(Grid grid)
        {
            foreach (object child in grid.Children)
            {
                if (child is Button button && button.CommandParameter is string parameter &&
                    parameter.StartsWith("CHIMP_TYPE_", StringComparison.Ordinal) && button.IsVisible)
                {
                    yield return button;
                }
            }
        }

        // 在开启连续生产的兵种头像上叠一个 ∞ 标记（不接收鼠标）。
        private void UpdateBadges(RecruitPanel panel, Grid grid)
        {
            var wanted = new HashSet<Button>();
            if (panel != null)
            {
                foreach (Button button in RecruitButtons(grid))
                {
                    if (active.Contains((string)button.CommandParameter))
                        wanted.Add(button);
                }
            }

            foreach (Button button in badges.Keys.ToList())
            {
                if (!wanted.Contains(button))
                    badges[button].Visibility = Visibility.Collapsed;
            }

            foreach (Button button in wanted)
            {
                if (!badges.TryGetValue(button, out TextBlock badge))
                {
                    badge = CreateBadge(button);
                    grid.Children.Add(badge);
                    badges[button] = badge;
                }
                badge.Visibility = Visibility.Visible;
            }
        }

        private static TextBlock CreateBadge(Button button)
        {
            var badge = new TextBlock
            {
                Text = BadgeText,
                FontSize = 26,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x40)),
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x0C, 0x08)),
                Padding = new Thickness(5, 0, 5, 2),
                IsHitTestVisible = false,
                HorizontalAlignment = button.HorizontalAlignment,
                VerticalAlignment = button.VerticalAlignment,
                // 放在头像按钮区域的右上角。
                Margin = new Thickness(button.Margin.Left + Math.Max(0f, button.Width - 30f), 0, 0,
                    button.Margin.Bottom + Math.Max(0f, button.Height - 34f)),
            };
            Panel.SetZIndex(badge, 50);
            return badge;
        }

        private sealed class RecruitPanel
        {
            public RecruitPanel(string gridName, params string[] units)
            {
                GridName = gridName;
                Units = units;
            }

            public string GridName { get; }
            public string[] Units { get; }
        }
    }
}
