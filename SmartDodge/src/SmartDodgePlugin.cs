using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SmartDodge
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SmartDodgePlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "SmartDodge_Dongge";
        public const string PluginName = "Artful Dodger";
        public const string PluginVersion = "1.0.1";

        internal const int HorseArcher = (int)eChimps.CHIMP_TYPE_ARAB_HORSEMAN;

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static SmartDodgePlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> masterEnabled, kiteMelee, autoEnableNew, verbose, testTools;
        private ConfigEntry<KeyCode> toggleKey;
        private ConfigEntry<int> rangeWorld;

        private volatile bool libraryLoaded, resetRequested = true;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private readonly HashSet<int> knownHorseArchers = new HashSet<int>();
        private readonly List<int> ids = new List<int>();
        private bool knownInitialized;
        private int lastGameTime = -1;
        private float nextErrorLogTime;
        private DodgeController dodge;
        private TestBench bench;

        private void Awake()
        {
            instance = this;

            masterEnabled = Config.Bind("General", "Enabled", true,
                "Master switch (also in Mod Options). When off, no horse archer dodges.");
            kiteMelee = Config.Bind("General", "KiteMelee", true,
                "Also back away from approaching melee enemies while staying in range.");
            autoEnableNew = Config.Bind("General", "NewHorseArchersDodge", false,
                "Horse archers recruited during the game start with dodging on.");
            toggleKey = Config.Bind("General", "ToggleKey", KeyCode.KeypadPeriod,
                "Turn dodging on or off for the selected horse archers.");
            rangeWorld = Config.Bind("General", "RangeWorld", 0,
                "Horse archer range in world units (8 per tile), used to stay in range while dodging. 0 = the game's range.");
            verbose = Config.Bind("Debug", "LogDodges", false,
                "Log every dodge decision.");
            testTools = Config.Bind("Test", "TestTools", false,
                "Developer test keys and statistics. Leave off for normal play.");

            dodge = new DodgeController(message => Logger.LogInfo(message)) { IsDamaging = Damaging };
            bench = new TestBench(Config, dodge, Logger, OwnHorseArchers, Damaging);

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new SmartDodgeSettings(masterEnabled, kiteMelee, autoEnableNew), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e => { if (e.Phase == EventHookPhase.Post) resetRequested = true; }));
            subscriptions.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(e => { if (e.Phase == EventHookPhase.Post) resetRequested = true; }));

            // 地面射手的飞行物走生成事件（最快）；塔上射手的不走，由控制器每刻扫描飞行物表补上。
            subscriptions.Add(ProjectileR3EventHooks.OnProjectileSpawn.Observable.Subscribe(e =>
            {
                if (e.Phase != EventHookPhase.Post) return;
                try { OnSpawn((int)e.ReturnValue, e.ProjectileType, e.SourceUnitId, e.SourceWorldTileX, e.SourceWorldTileY, e.TargetWorldTileX, e.TargetWorldTileY); }
                catch (Exception ex) { Logger.LogError(ex); }
            }));
            subscriptions.Add(ProjectileR3EventHooks.OnProjectileDelete.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre) dodge.OnProjectileGone(e.ProjectileId);
            }));
            subscriptions.Add(UnitR3EventHooks.OnUnitTakeProjectileDamageEx.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre && testTools.Value) bench.OnProjectileDamage(e.ProjectileId, e.AttackedUnitId, e.AttackingUnitId, e.Damage);
            }));
            // 玩家框选后右键的移动命令走部队（tribe）命令；单个单位的命令走单位事件。躲完后恢复这些目的地。
            subscriptions.Add(TribeR3EventHooks.OnTribeIssueOrderMoveHere.Observable.Subscribe(e =>
            {
                if (e.Phase != EventHookPhase.Pre || dodge.IssuingOwnOrder) return;
                try { dodge.OnPlayerTribeMoveOrder(e.TribeId, e.TileX, e.TileY); }
                catch (Exception ex) { Logger.LogError(ex); }
            }));
            subscriptions.Add(UnitR3EventHooks.OnUnitMoveHere.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre && !dodge.IssuingOwnOrder) dodge.OnPlayerMoveOrder(e.UnitId, e.TileX, e.TileY);
            }));

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(SmartDodgePlugin), nameof(AfterKeyManagerUpdate)));

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Enabled: {masterEnabled.Value}, toggle key: {toggleKey.Value}");
        }

        internal static int GameTime => GameData.Instance?.lastGameState?.game_time ?? 0;

        internal static int LocalPlayer => GamePlayerManagerAPI.Instance.GetLocalPlayerId();

        /// <summary>会伤到弓骑兵的飞行物：箭、弩箭、各种石头、弩炮箭、投石兵的石头、掷火兵的火罐、标枪等。</summary>
        private static bool Damaging(ProjectileType type)
        {
            switch (type)
            {
                case ProjectileType.ArcherArrow:
                case ProjectileType.CrossbowBolt:
                case ProjectileType.CatapultRocks:
                case ProjectileType.TrebutchetRocks:
                case ProjectileType.MangongelRocks:
                case ProjectileType.BallistaBolt:
                case ProjectileType.ArabBallistaBolt:
                case ProjectileType.SlingerStone:
                case ProjectileType.GrenadierGrenade:
                case ProjectileType.ArabGrenadierOrBedouinAmbusherOrDiseaseCloud:
                case ProjectileType.BedouinLance:
                case ProjectileType.UnkJavelin39:
                    return true;
                default:
                    return false;
            }
        }

        private void OnSpawn(int id, ProjectileType type, int source, int sx, int sy, int tx, int ty)
        {
            if (id <= 0 || !Damaging(type)) return;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            if (source > 0 && units.IsValid(source) && units.GetOwner(source) == LocalPlayer) return; // 自己射的
            dodge.OnProjectileSpawn(id, type, GameTime, new Vector2(sx, sy), new Vector2(tx, ty));
        }

        private static void AfterKeyManagerUpdate() => instance?.OnFrame();

        private void OnFrame()
        {
            if (!libraryLoaded) return;
            try { Tick(); }
            catch (Exception ex)
            {
                if (Time.unscaledTime < nextErrorLogTime) return;
                nextErrorLogTime = Time.unscaledTime + 5f;
                Logger.LogError($"{PluginName} frame failed: {ex}");
            }
        }

        private void Tick()
        {
            bool inGame = GameMap.instance != null && Director.instance != null && Director.instance.SimRunning;
            if (!inGame) return;

            if (resetRequested)
            {
                resetRequested = false;
                dodge.Clear();
                bench.Reset();
                knownHorseArchers.Clear();
                knownInitialized = false;
            }

            bool keyboardFree = FatControler.instance == null || !FatControler.instance.NoesisHasKeyboard;
            if (keyboardFree && toggleKey.Value != KeyCode.None && Input.GetKeyDown(toggleKey.Value))
                ToggleSelected();
            if (testTools.Value && keyboardFree) bench.OnKeys();

            int now = GameTime;
            if (now == lastGameTime) return; // 暂停中
            lastGameTime = now;

            if (autoEnableNew.Value) EnableNewHorseArchers();

            dodge.Verbose = verbose.Value;
            // 0 = 用游戏的射程（API 单位为地块，实测弓骑兵最远射击距离 ≈ 54 × 8）。
            dodge.RangeWorld = rangeWorld.Value > 0 ? rangeWorld.Value
                : GameUnitManagerAPI.Instance.GetUnitAttackRange(eChimps.CHIMP_TYPE_ARAB_HORSEMAN) * DodgeController.WorldPerTile;
            dodge.KiteMelee = kiteMelee.Value;
            if (masterEnabled.Value) dodge.Tick(now);

            if (testTools.Value) bench.OnTick(now);
        }

        /// <summary>对局中新出现的己方弓骑兵自动开启躲避（读档/开局时已有的不算）。</summary>
        private void EnableNewHorseArchers()
        {
            foreach (int id in OwnHorseArchers())
            {
                if (knownHorseArchers.Add(id) && knownInitialized)
                    dodge.Enabled.Add(id);
            }
            knownInitialized = true;
        }

        /// <summary>选中的己方弓骑兵：只要有一个没开，就全部打开；否则全部关闭。</summary>
        private void ToggleSelected()
        {
            var selected = new List<int>();
            var state = GameData.Instance?.lastGameState;
            if (state?.selectedChimps != null)
            {
                GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
                int count = Math.Min(state.numSelectedChimps, state.selectedChimps.Length);
                for (int i = 0; i < count; i++)
                {
                    int id = state.selectedChimps[i];
                    if (id > 0 && units.IsValid(id) && (int)units.GetType(id) == HorseArcher && units.GetOwner(id) == LocalPlayer)
                        selected.Add(id);
                }
            }
            if (selected.Count == 0) return;
            bool turnOn = selected.Exists(id => !dodge.Enabled.Contains(id));
            foreach (int id in selected)
            {
                if (turnOn) dodge.Enabled.Add(id);
                else dodge.Disable(id);
            }
            Logger.LogInfo($"Dodging {(turnOn ? "ON" : "OFF")} for {selected.Count} selected horse archers ({dodge.Enabled.Count} dodging in total).");
        }

        internal List<int> OwnHorseArchers()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int local = LocalPlayer;
            ids.Clear();
            units.GetAllUnits(ids, AliveState.IsAlive, eChimps.CHIMP_TYPE_ARAB_HORSEMAN, PlayerRelationship.Any, null);
            return ids.FindAll(id => units.GetOwner(id) == local);
        }
    }
}
