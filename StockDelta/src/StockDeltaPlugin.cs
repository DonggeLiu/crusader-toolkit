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
using Time = UnityEngine.Time;

namespace StockDelta
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class StockDeltaPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "StockDelta_Dongge";
        public const string PluginName = "Stock Delta";
        public const string PluginVersion = "1.0.3";

        private const float PollSeconds = 0.5f;
        private const int GoodsCount = 25;
        private const string BindingPrefix = "AllStoredGoods[";

        // 库房、武器库、粮仓（食物页）三个面板；数量文本都绑定到 AllStoredGoods[物资编号]。
        private static readonly string[] PanelNames = { "StockpilePanel", "ArmouryPanel", "ReportsFoodPanel" };

        private static readonly SolidColorBrush Up = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x62, 0x0A));
        private static readonly SolidColorBrush Down = new SolidColorBrush(Color.FromArgb(0xFF, 0xA0, 0x10, 0x10));
        private static readonly SolidColorBrush Flat = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x33, 0x2A));
        private static readonly SolidColorBrush Minor = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x33, 0x2A));

        private static readonly SolidColorBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xE0, 0xF0, 0xE2, 0xC0));

        // 数量文本离面板底边太近时（库房、武器库），显示变化期间把它上移，给变化行腾出位置。
        private const float MinCountBottom = 30f;
        private const float DeltaLineHeight = 18f;
        private const float LabelWiden = 30f;

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static StockDeltaPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> showDeltas;

        private volatile bool libraryLoaded;
        private volatile bool resetRequested = true;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        // 月度统计：本月月初库存、上月净变化（null 表示还没有完整的一个月）。
        private int currentMonthKey = -1;
        private readonly int[] monthStart = new int[GoodsCount];
        private readonly int?[] lastMonth = new int?[GoodsCount];
        private readonly int[] current = new int[GoodsCount];

        // 每个数量文本旁加的变化显示。
        private readonly Dictionary<TextBlock, DeltaLabel> labels = new Dictionary<TextBlock, DeltaLabel>();
        private float nextPoll;
        private float nextErrorLogTime;

        private void Awake()
        {
            instance = this;

            showDeltas = Config.Bind("General", "Enabled", true,
                "Show net stock changes in storage panels (also controlled by Mod Options).");

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

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(StockDeltaPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new StockDeltaSettings(showDeltas), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {showDeltas.Value}");
        }

        private static void AfterKeyManagerUpdate()
        {
            instance?.OnFrame();
        }

        private void OnFrame()
        {
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
                    Logger.LogError($"Stock delta update failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            EngineInterface.PlayState state = GameData.Instance?.lastGameState;
            bool inGame = GameMap.instance != null && Director.instance != null && Director.instance.SimRunning &&
                          state?.resources != null;
            if (!inGame)
            {
                HideAll();
                return;
            }

            UpdateStatistics(state);

            if (!showDeltas.Value)
            {
                HideAll();
                return;
            }

            UpdateLabels();
        }

        private void UpdateStatistics(EngineInterface.PlayState state)
        {
            for (int i = 0; i < GoodsCount; i++)
                current[i] = state.resources[i];

            int monthKey = state.year * 12 + state.month;
            if (resetRequested || currentMonthKey < 0)
            {
                resetRequested = false;
                currentMonthKey = monthKey;
                Array.Copy(current, monthStart, GoodsCount);
                for (int i = 0; i < GoodsCount; i++)
                    lastMonth[i] = null;
                Logger.LogInfo($"Stock tracking started at {state.year}/{state.month}.");
                return;
            }

            if (monthKey == currentMonthKey)
                return;

            // 进入新的月份：刚结束的这个月的净变化 = 现在的库存 − 月初库存。
            // 若一次跳过了多个月（例如读档后首帧），就不算完整的一个月。
            bool consecutive = monthKey == currentMonthKey + 1;
            for (int i = 0; i < GoodsCount; i++)
                lastMonth[i] = consecutive ? current[i] - monthStart[i] : (int?)null;

            currentMonthKey = monthKey;
            Array.Copy(current, monthStart, GoodsCount);
        }

        private void UpdateLabels()
        {
            HUD_Buildings hud = MainViewModel.Instance?.HUDBuildingPanel;
            var seen = new HashSet<TextBlock>();

            if (hud != null && hud.IsVisible)
            {
                foreach (string panelName in PanelNames)
                {
                    if (!(hud.FindName(panelName) is Grid grid) || !grid.IsVisible)
                        continue;

                    // Adding delta labels changes Children; enumerate a snapshot of counts.
                    var originalChildren = new List<object>();
                    foreach (object child in grid.Children)
                        originalChildren.Add(child);
                    foreach (object child in originalChildren)
                    {
                        if (!(child is TextBlock count) || !count.IsVisible)
                            continue;

                        int good = BoundGood(count);
                        if (good < 0)
                            continue;

                        if (!labels.TryGetValue(count, out DeltaLabel label))
                        {
                            label = new DeltaLabel(count);
                            labels[count] = label;
                            grid.Children.Add(label.Root);
                        }

                        seen.Add(count);
                        label.Show(lastMonth[good], current[good] - monthStart[good]);
                    }
                }
            }

            foreach (KeyValuePair<TextBlock, DeltaLabel> entry in labels)
            {
                if (!seen.Contains(entry.Key))
                    entry.Value.Hide();
            }
        }

        private void HideAll()
        {
            foreach (DeltaLabel label in labels.Values)
                label.Hide();
        }

        // 数量文本绑定的是 AllStoredGoods[N]，N 即物资编号（eGoods）。
        private static int BoundGood(TextBlock text)
        {
            string path = text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding?.Path?.Path;
            if (path == null || !path.StartsWith(BindingPrefix, StringComparison.Ordinal))
                return -1;

            string index = path.Substring(BindingPrefix.Length).TrimEnd(']');
            return int.TryParse(index, out int good) && good >= 0 && good < GoodsCount ? good : -1;
        }

        /// <summary>数量下方的一行：大字上月净变化 + 小字本月至今。</summary>
        private sealed class DeltaLabel
        {
            private readonly TextBlock lastMonthText;
            private readonly TextBlock thisMonthText;
            private readonly TextBlock count;
            private readonly Thickness originalCountMargin;
            private readonly Thickness raisedCountMargin;
            private string shown;

            public DeltaLabel(TextBlock count)
            {
                this.count = count;
                originalCountMargin = count.Margin;
                float raise = Math.Max(0f, MinCountBottom - originalCountMargin.Bottom);
                raisedCountMargin = new Thickness(originalCountMargin.Left, originalCountMargin.Top,
                    originalCountMargin.Right, originalCountMargin.Bottom + raise);

                lastMonthText = new TextBlock { FontSize = 15, FontWeight = FontWeight.Bold };
                thisMonthText = new TextBlock
                {
                    FontSize = 13,
                    Foreground = Minor,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(1, 0, 0, 1),
                };

                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(lastMonthText);
                line.Children.Add(thisMonthText);

                // 浅色羊皮纸底色的圆角背景：面板底部有深色纹理，深色文字直接叠上去看不清。
                Root = new Border
                {
                    Child = line,
                    Background = LabelBackground,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 0),
                    IsHitTestVisible = false,
                    HorizontalAlignment = count.HorizontalAlignment,
                    VerticalAlignment = count.VerticalAlignment,
                    // 与数量文本同列，放在它（上移后）的正下方。左右边距各收窄 LabelWiden：中心不变，
                    // 但留出更宽的排版空间，否则库房里“+32 · +16”这类较长的变化会被截掉。
                    Margin = new Thickness(originalCountMargin.Left - LabelWiden, originalCountMargin.Top,
                        originalCountMargin.Right - LabelWiden, Math.Max(2f, raisedCountMargin.Bottom - DeltaLineHeight)),
                };
                Grid.SetColumn(Root, Grid.GetColumn(count));
                Grid.SetColumnSpan(Root, Grid.GetColumnSpan(count));
                Grid.SetRow(Root, Grid.GetRow(count));
                Panel.SetZIndex(Root, 5);
            }

            public Border Root { get; }

            public void Show(int? lastMonth, int thisMonth)
            {
                string key = $"{lastMonth}|{thisMonth}";
                if (key != shown)
                {
                    shown = key;
                    lastMonthText.Text = lastMonth.HasValue ? Signed(lastMonth.Value) : "—";
                    lastMonthText.Foreground = !lastMonth.HasValue || lastMonth.Value == 0 ? Flat : lastMonth.Value > 0 ? Up : Down;
                    thisMonthText.Text = "·" + Signed(thisMonth);
                }

                count.Margin = raisedCountMargin;
                Root.Visibility = Visibility.Visible;
            }

            public void Hide()
            {
                Root.Visibility = Visibility.Collapsed;
                count.Margin = originalCountMargin;
            }

            private static string Signed(int value)
            {
                return value > 0 ? "+" + value : value < 0 ? "−" + (-value) : "0";
            }
        }
    }
}
