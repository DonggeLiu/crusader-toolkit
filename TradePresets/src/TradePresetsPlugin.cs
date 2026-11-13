using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TradePresets
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class TradePresetsPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "TradePresets_Dongge";
        public const string PluginName = "Trade Presets";
        public const string PluginVersion = "1.0.2";

        // 轮询间隔：开局时用于找到进入游戏的时机并套用预设。
        private const float PollSeconds = 0.5f;
        // 对局中每隔这么久在内存里记一次阈值快照（不写配置）。对局结束时游戏可能已经清空交易表
        // （实测“重新开始”时读到全 0），所以结束时用最后一次快照，而不是当时读到的值。
        private const float SnapshotSeconds = 5f;
        // 读档后等这么久再取基准值。
        private const float LoadSettleSeconds = 2f;

        // 阈值不设的一侧：只卖的物资买入阈值为 0（永不买），只买的物资卖出阈值为最大值（永不卖）。
        private const int NeverBuy = 0;
        private const int NeverSell = 1000;

        /// <summary>
        /// 市场里可交易的物资（游戏 eGoods 编号）及初始预设。
        /// 分组与市场的食物 / 资源 / 武器三页一致；“其余食物”即食物页除啤酒花外的各项。
        /// </summary>
        private static readonly Good[] Goods =
        {
            // 资源
            new Good(2, "Wood", "木材", 30, NeverSell),
            new Good(4, "Stone", "石头", NeverBuy, 48),
            new Good(6, "Iron", "铁", NeverBuy, 10),
            new Good(7, "Pitch", "沥青", NeverBuy, 1),
            // 食物
            new Good(3, "Hops", "啤酒花", 2, NeverSell),
            new Good(12, "Meat", "肉", NeverBuy, 200),
            new Good(11, "Cheese", "奶酪", NeverBuy, 200),
            new Good(13, "Apples", "苹果", NeverBuy, 200),
            new Good(10, "Bread", "面包", NeverBuy, 200),
            new Good(9, "Wheat", "小麦", NeverBuy, 200),
            new Good(16, "Flour", "面粉", NeverBuy, 200),
            new Good(14, "Ale", "麦酒", NeverBuy, 200),
            // 武器
            new Good(17, "Bows", "弓", NeverBuy, 1),
            new Good(18, "Crossbows", "弩", NeverBuy, 20),
            new Good(19, "Spears", "长矛", NeverBuy, 1),
            new Good(20, "Pikes", "长枪", NeverBuy, 1),
            new Good(21, "Maces", "锤", NeverBuy, 1),
            new Good(22, "Swords", "剑", NeverBuy, 1),
            new Good(23, "LeatherArmour", "皮甲", NeverBuy, 20),
            new Good(24, "MetalArmour", "铁甲", NeverBuy, 1),
        };

        private enum Phase
        {
            Idle,            // 不在对局中
            PendingApply,    // 新对局：等进入游戏后套用预设
            PendingBaseline, // 读档：等一会儿取基准值
            Watching,        // 对局进行中；结束时把改动存为新预设
        }

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static TradePresetsPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> presetsEnabled;
        private readonly Dictionary<int, ConfigEntry<int>> buyEntries = new Dictionary<int, ConfigEntry<int>>();
        private readonly Dictionary<int, ConfigEntry<int>> sellEntries = new Dictionary<int, ConfigEntry<int>>();
        private readonly Dictionary<int, (int Buy, int Sell)> observed = new Dictionary<int, (int, int)>();
        private readonly Dictionary<int, (int Buy, int Sell)> snapshot = new Dictionary<int, (int, int)>();
        private float nextSnapshot;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        private volatile bool libraryLoaded;
        // 地图事件可能不在主线程触发，只在这里记下请求，由主线程的轮询处理。
        private volatile int requestedPhase = -1;
        private Phase phase = Phase.Idle;
        private float phaseStarted;
        private float nextPoll;
        private float nextErrorLogTime;

        private void Awake()
        {
            instance = this;

            presetsEnabled = Config.Bind("General", "Enabled", true,
                "Apply saved thresholds to new single-player games. Disabling does not stop recording changes.");
            foreach (Good good in Goods)
            {
                buyEntries[good.Id] = Config.Bind("Buy", good.Key, good.DefaultBuy,
                    $"{good.Key}: buy below this stock level (0 = never buy).");
                sellEntries[good.Id] = Config.Bind("Sell", good.Key, good.DefaultSell,
                    $"{good.Key}: sell above this stock level ({NeverSell} = never sell).");
            }

            // 对局结束（回主菜单、开新局、读档、退出游戏）时，把本局改过的阈值存为新预设。
            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre)
                    SaveChangesAtGameEnd("new game");
                else if (e.Phase == EventHookPhase.Post)
                    requestedPhase = (int)Phase.PendingApply;
            }));
            subscriptions.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre)
                    SaveChangesAtGameEnd("load save");
                else if (e.Phase == EventHookPhase.Post)
                    requestedPhase = (int)Phase.PendingBaseline;
            }));
            subscriptions.Add(MapLoaderR3EventHooks.OnUnloadMap.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre)
                    SaveChangesAtGameEnd("exit map");
            }));
            Application.quitting += () => SaveChangesAtGameEnd("quit");

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(TradePresetsPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new TradePresetsSettings(presetsEnabled), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {presetsEnabled.Value}");
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
                    Logger.LogError($"Trade presets update failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            int request = System.Threading.Interlocked.Exchange(ref requestedPhase, -1);
            if (request >= 0)
                EnterPhase((Phase)request);

            bool inGame = GameMap.instance != null && Director.instance != null && Director.instance.SimRunning;
            if (!inGame)
                return;

            // 多人游戏里不改动交易设置，避免与其他玩家不同步。
            if (Director.instance.MultiplayerGame)
                return;

            switch (phase)
            {
                case Phase.PendingApply:
                    if (!presetsEnabled.Value)
                    {
                        EnterPhase(Phase.PendingBaseline);
                        break;
                    }
                    ApplyPresets();
                    StartWatching();
                    break;

                case Phase.Watching:
                    if (Time.unscaledTime >= nextSnapshot)
                    {
                        nextSnapshot = Time.unscaledTime + SnapshotSeconds;
                        TakeSnapshot();
                    }
                    break;

                case Phase.PendingBaseline:
                    if (Time.unscaledTime - phaseStarted > LoadSettleSeconds)
                        StartWatching();
                    break;

                case Phase.Idle:
                    // 在对局中但没收到地图事件（例如 mod 加载晚于开局）：只记录，不套用。
                    EnterPhase(Phase.PendingBaseline);
                    break;
            }
        }

        private void EnterPhase(Phase next)
        {
            phase = next;
            phaseStarted = Time.unscaledTime;
        }

        // 买入/卖出阈值存在玩家管理器里，每个玩家 25 项（按 eGoods 编号），各 2 字节；偏移与扩展器
        // GamePlayerManagerAPI.SetAutoTrade 相同（已在市场界面核对）。不能读 GameData.lastGameState：
        // 它的这几项只在打开市场交易页时才有值，平时全是 0。直接读写只在单人游戏里进行。
        // 开关不写内存（扩展器用的开关偏移实测不对），改用游戏打开交易页时自己用的命令：
        // Autotrade_OnOff（1051），参数为物资编号和 0/1。
        private const int BuyOffset = 2284182;
        private const int SellOffset = 2284632;

        private static int SlotIndex(int goodId)
        {
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            if (player < 1 || player > 8)
                player = 1;
            return player * 25 + goodId;
        }

        private static unsafe (int Buy, int Sell) ReadThresholds(int goodId)
        {
            byte* manager = (byte*)GamePlayerManagerAPI.Instance.GetPlayerManager();
            int index = SlotIndex(goodId);
            return (*(ushort*)(manager + BuyOffset + index * 2), *(ushort*)(manager + SellOffset + index * 2));
        }

        private static unsafe void WriteTrade(int goodId, bool on, int buy, int sell)
        {
            byte* manager = (byte*)GamePlayerManagerAPI.Instance.GetPlayerManager();
            int index = SlotIndex(goodId);
            *(ushort*)(manager + BuyOffset + index * 2) = (ushort)buy;
            *(ushort*)(manager + SellOffset + index * 2) = (ushort)sell;
            EngineInterface.GameAction(Enums.GameActionCommand.Autotrade_OnOff, goodId, on ? 1 : 0);
        }

        private void ApplyPresets()
        {
            foreach (Good good in Goods)
            {
                int buy = buyEntries[good.Id].Value;
                int sell = Math.Max(sellEntries[good.Id].Value, buy + 1);
                WriteTrade(good.Id, false, buy, sell);
            }

            Logger.LogInfo("Applied trade presets for new game (auto-trade left off): " + DescribeState());
        }

        private string DescribeState()
        {
            var parts = new List<string>();
            foreach (Good good in Goods)
            {
                (int buy, int sell) = ReadThresholds(good.Id);
                parts.Add($"{good.Key}<{buy}/>{sell}");
            }
            return string.Join(" ", parts);
        }

        private void StartWatching()
        {
            lock (saveLock)
            {
                observed.Clear();
                snapshot.Clear();
                foreach (Good good in Goods)
                {
                    observed[good.Id] = ReadThresholds(good.Id);
                    snapshot[good.Id] = observed[good.Id];
                }
                nextSnapshot = Time.unscaledTime + SnapshotSeconds;
                EnterPhase(Phase.Watching);
            }
        }

        // 读到的阈值全为 0 说明交易表已被清空（对局正在结束），这次不记。
        private void TakeSnapshot()
        {
            var current = new Dictionary<int, (int Buy, int Sell)>();
            bool allZero = true;
            foreach (Good good in Goods)
            {
                (int buy, int sell) = ReadThresholds(good.Id);
                current[good.Id] = (buy, sell);
                if (buy != 0 || sell != 0)
                    allZero = false;
            }

            if (allZero)
                return;

            lock (saveLock)
            {
                foreach (var entry in current)
                    snapshot[entry.Key] = entry.Value;
            }
        }

        // 对局结束时调用：与开局基准比较，玩家改过的阈值（市场滑块、快捷键都算）存为新预设。
        // 地图事件可能不在主线程触发，加锁避免与主线程同时读写。
        private readonly object saveLock = new object();

        private void SaveChangesAtGameEnd(string reason)
        {
            lock (saveLock)
            {
                try
                {
                    if (phase != Phase.Watching)
                        return;

                    int saved = RecordChanges();
                    Logger.LogInfo($"Game ended ({reason}): saved {saved} changed preset(s).");
                    EnterPhase(Phase.Idle);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to save trade presets at game end: {ex}");
                }
            }
        }

        private int RecordChanges()
        {
            int saved = 0;
            foreach (Good good in Goods)
            {
                (int buy, int sell) = snapshot[good.Id];
                (int Buy, int Sell) previous = observed[good.Id];
                if (buy == previous.Buy && sell == previous.Sell)
                    continue;

                observed[good.Id] = (buy, sell);
                buyEntries[good.Id].Value = buy;
                sellEntries[good.Id].Value = sell;
                saved++;
                Logger.LogInfo($"Saved new preset for {good.Key}: buy < {buy}, sell > {sell}");
            }

            return saved;
        }

        private readonly struct Good
        {
            public Good(int id, string key, string label, int defaultBuy, int defaultSell)
            {
                Id = id;
                Key = key;
                Label = label;
                DefaultBuy = defaultBuy;
                DefaultSell = defaultSell;
            }

            public int Id { get; }
            public string Key { get; }
            public string Label { get; }
            public int DefaultBuy { get; }
            public int DefaultSell { get; }
        }
    }
}
