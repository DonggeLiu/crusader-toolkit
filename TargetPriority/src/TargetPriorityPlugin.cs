using BepInEx;
using HarmonyLib;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using System;
using UnityEngine;

namespace TargetPriority
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class TargetPriorityPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "TargetPriority_Dongge";
        public const string PluginName = "Smart Shooters";
        public const string PluginVersion = "1.5.0";

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static TargetPriorityPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private Settings settings;
        private TargetHook hook;
        private ThreatProbe probe; // 测试用，保持订阅存活
        private volatile bool libraryLoaded;
        private bool installAttempted;
        private static float nextErrorLogTime;
        private float nextStatsTime, nextHealthTime;
        // 对射测试（测试键 F4 删到每方几人、F9 切换人数、F6 开打）。
        private static readonly int[] duelSizes = { 10, 20, 40 };
        private int duelSize = 1;
        private float duelStart;
        private string duelMode = "";
        private int arrangeCount;
        private bool towerProbePending;
        private string arrangeLabel = "original";

        private void Awake()
        {
            instance = this;
            settings = new Settings(Config);
            hook = new TargetHook(settings, message => Logger.LogInfo(message));

            foreach (var entry in settings.TierEntries)
                entry.SettingChanged += (_, __) => hook.RebuildTables();
            settings.ShootWildAnimalsEntry.SettingChanged += (_, __) => hook.RebuildTables();

            if (settings.ProbeThreat)
            {
                try { probe = new ThreatProbe(message => Logger.LogInfo(message)) { Invulnerable = settings.StartInvulnerable, ShieldedSide = settings.ShieldedSide }; }
                catch (Exception ex) { Logger.LogError($"Threat probe failed: {ex}"); }
            }

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            // 本作会销毁 BepInEx 创建的对象，所以挂在游戏自己的 KeyManager.Update 上取得每帧回调。
            new Harmony(PluginGuid).Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(TargetPriorityPlugin), nameof(AfterKeyManagerUpdate)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new TargetPrioritySettings(settings), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Mine: {settings.Enabled}, allies: {settings.ApplyToAllies}, enemies: {settings.ApplyToEnemies}");
        }

        private static void AfterKeyManagerUpdate()
        {
            instance?.OnFrame();
        }

        private void OnFrame()
        {
            if (!libraryLoaded) return;

            if (!installAttempted)
            {
                installAttempted = true;
                try { hook.Install(); }
                catch (Exception ex) { Logger.LogError($"{PluginName} is inactive: {ex.Message}"); }
            }

            // 记录目标时顺带每 5 秒记一次血量快照（不需要测试键，避免和其他 mod 的测试键冲突）。
            if (settings.LogTargets && hook.Installed && Time.unscaledTime >= nextHealthTime)
            {
                nextHealthTime = Time.unscaledTime + 5f;
                try { Logger.LogInfo($"Health (allocation {(settings.AllocateFire ? "on" : "off")}): {hook.SummarizeWounded()}"); }
                catch (Exception ex) { ReportError(ex); }
            }

            if (!settings.TestTools) return;
            // 对射测试：开打后一方全灭（或超时 300 秒）就记一行结果，并重新打开免伤，方便直接载入下一轮。
            if (duelStart > 0 && hook.Installed)
            {
                hook.CountSides(out int own, out int ownHealth, out int enemy, out int enemyHealth);
                float elapsed = Time.unscaledTime - duelStart;
                try { probe?.Scan(); } catch (Exception ex) { ReportError(ex); }
                if (own == 0 || enemy == 0 || elapsed > 300f)
                {
                    Logger.LogInfo($"[duel result] mode {duelMode} {arrangeLabel}, size {duelSizes[duelSize]}, ours {own} ({ownHealth} hp), enemy {enemy} ({enemyHealth} hp), {elapsed:F0} s" +
                                   (probe != null ? $", hits on intended/other: p1 {probe.HitIntended[1]}/{probe.HitOther[1]}, p2 {probe.HitIntended[2]}/{probe.HitOther[2]}" : ""));
                    if (probe != null) Logger.LogInfo($"[duel shots] {probe.SummarizeShots()}");
                    duelStart = 0;
                    if (probe != null) probe.Invulnerable = true;
                }
            }
            if (Time.unscaledTime >= nextStatsTime)
            {
                nextStatsTime = Time.unscaledTime + 5f;
                Logger.LogInfo($"Test stats: calls {TargetHook.Calls}, mine {TargetHook.Applied}, picked {TargetHook.Picked}, over walls {TargetHook.OverWall}, " +
                               $"fire rebuilds {FireAllocation.Rebuilds}, planned picks {FireAllocation.PlannedPicks} (sparse skipped {FireAllocation.PlanSkippedSparse}, followed {FireAllocation.PlanFollowed}, overridden {FireAllocation.PlanOverridden} (kept previous {FireAllocation.OverrideKeptPrevious}), blocked candidates {FireAllocation.PlanBlocked}), saturated picks {FireAllocation.SaturatedPicks}, " +
                               $"in flight counted {FireAllocation.InFlightCounted} / no target {FireAllocation.InFlightWithoutTarget}");
                if (hook.Installed)
                    Logger.LogInfo($"Test armies (allocation {(settings.AllocateFire ? "on" : "off")}): {hook.SummarizeHealth()}");
            }
            try
            {
                if (Input.GetKeyDown(KeyCode.F10))
                {
                    settings.Enabled = !settings.Enabled;
                    Logger.LogInfo($"Test: target priority {(settings.Enabled ? "ON" : "OFF (vanilla)")}");
                }
                if (Input.GetKeyDown(KeyCode.F8) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    settings.PlanNeedsBlockers = !settings.PlanNeedsBlockers;
                    Logger.LogInfo($"Test: planned shots {(settings.PlanNeedsBlockers ? "only when someone blocks (else spread)" : "always")}");
                }
                else if (Input.GetKeyDown(KeyCode.F8))
                {
                    settings.ShootOverWalls = !settings.ShootOverWalls;
                    Logger.LogInfo($"Test: shoot over walls {(settings.ShootOverWalls ? "ON" : "OFF")}");
                }
                if (Input.GetKeyDown(KeyCode.F2))
                    Logger.LogInfo($"Test: moving {hook.MoveShootersTo(settings.EnemyStandX, settings.EnemyStandY, enemies: true)} enemy ground shooters to ({settings.EnemyStandX},{settings.EnemyStandY})");
                if (Input.GetKeyDown(KeyCode.F3) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    towerProbePending = !towerProbePending;
                    Logger.LogInfo("Test: tower tops " + (towerProbePending ? hook.ProbeTowerTops() : hook.ReadTowerTops()));
                }
                else if (Input.GetKeyDown(KeyCode.F3))
                    Logger.LogInfo($"Test: {hook.ChargeEnemies(settings.ChargeFromX, settings.ChargeFromY, settings.TestStandX, settings.TestStandY)} enemy melee charging from ({settings.ChargeFromX},{settings.ChargeFromY})");
                if (Input.GetKeyDown(KeyCode.F4) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    // 测试：删掉双方单位，在两座塔顶按同一种（中心对称的）站位重新生成。每按一次换一种：
                    // 每 3 种里 2 种铺开（随机空出几格），1 种另把 4 人叠在一格。
                    arrangeCount++;
                    Logger.LogInfo($"Test: rebuilt tower armies ({hook.RebuildTowerArmies(arrangeCount, arrangeCount % 3 == 0 ? 4 : 1, duelSizes[duelSize])})");
                    arrangeLabel = $"layout{arrangeCount}";
                }
                else if (Input.GetKeyDown(KeyCode.F4))
                    Logger.LogInfo($"Test: trimmed {hook.TrimSides(duelSizes[duelSize])} shooters to {duelSizes[duelSize]} per side");
                if (Input.GetKeyDown(KeyCode.F11))
                    Logger.LogInfo($"Test: garrison {hook.GarrisonTowers()}");
                if (Input.GetKeyDown(KeyCode.F9) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    settings.PlanOnFlat = !settings.PlanOnFlat;
                    Logger.LogInfo($"Test: planned shots on flat ground {(settings.PlanOnFlat ? "ON" : "OFF (fire allocation)")}");
                }
                else if (Input.GetKeyDown(KeyCode.F9))
                {
                    duelSize = (duelSize + 1) % duelSizes.Length;
                    Logger.LogInfo($"Test: duel size {duelSizes[duelSize]} per side");
                }
                if (Input.GetKeyDown(KeyCode.F5) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    // 测试：按当前站位编号原样重建（同一站位下对比原版与模组）。
                    Logger.LogInfo($"Test: rebuilt tower armies again ({hook.RebuildTowerArmies(arrangeCount, arrangeCount % 3 == 0 ? 4 : 1, duelSizes[duelSize])})");
                }
                else if (Input.GetKeyDown(KeyCode.F5))
                    Logger.LogInfo($"Test: moving {hook.MoveShootersTo(settings.TestStandX, settings.TestStandY)} shooters to ({settings.TestStandX},{settings.TestStandY})");
                if (Input.GetKeyDown(KeyCode.F6) && probe != null)
                {
                    probe.Invulnerable = !probe.Invulnerable;
                    if (!probe.Invulnerable)
                    {
                        Logger.LogInfo($"Test: healed {hook.HealAll()} units to full");
                        duelStart = Time.unscaledTime;
                        Array.Clear(probe.HitIntended, 0, 16);
                        Array.Clear(probe.HitOther, 0, 16);
                        duelMode = settings.Enabled ? (settings.AllocateFire ? (settings.PlanOnFlat ? "planflat" : settings.PlanTowerShots ? (settings.PlanNeedsBlockers ? "planned3" : settings.PlanAliveAware ? "planned2" : "planned") : "spread") : "mod-tiers") : "vanilla";
                        probe.ResetShots($"{duelMode}-p{settings.LocalPlayer}-{arrangeLabel}");
                        FireAllocation.PlannedPicks = FireAllocation.PlanFollowed = FireAllocation.PlanOverridden = 0;
                        FireAllocation.OverrideKeptPrevious = FireAllocation.PlanOverrideLogs = FireAllocation.PlanBlocked = 0;
                        TargetHook.overrideChecks = 0;
                    }
                    Logger.LogInfo(probe.Invulnerable ? "Test: all units invulnerable" : $"[battle start] t={GameData.Instance?.lastGameState?.game_time} invulnerability off");
                }
                if (Input.GetKeyDown(KeyCode.F7) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    settings.PlanTowerShots = !settings.PlanTowerShots;
                    Logger.LogInfo($"Test: tower shots {(settings.PlanTowerShots ? "PLANNED" : "spread (vanilla-like)")}");
                }
                else if (Input.GetKeyDown(KeyCode.F7))
                {
                    settings.AllocateFire = !settings.AllocateFire;
                    Logger.LogInfo($"Test: fire allocation {(settings.AllocateFire ? "ON" : "OFF (vanilla spreading)")}");
                }
                if (Input.GetKeyDown(KeyCode.F12) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                    Logger.LogInfo("Test acceptable targets: " + hook.ScanAcceptable());
                else if (Input.GetKeyDown(KeyCode.F12))
                {
                    Logger.LogInfo("Test units: " + hook.SummarizeUnits());
                    // 工人是否有近战伤害：对弓箭手的基础伤害（游戏近战伤害表）。
                    var sb = new System.Text.StringBuilder("Test melee damage vs ARCHER: ");
                    foreach (int t in new[] { 1, 3, 4, 6, 7, 8, 10, 11, 18, 19, 20, 21, 24, 26, 55 })
                        sb.Append($"{(eChimps)(ushort)t}={GameUnitManagerAPI.Instance.GetMeleeDamageFromTo((eChimps)(ushort)t, eChimps.CHIMP_TYPE_ARCHER)}, ");
                    Logger.LogInfo(sb.ToString().Replace("CHIMP_TYPE_", ""));
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        internal static void ReportError(Exception ex)
        {
            if (Time.unscaledTime < nextErrorLogTime) return;
            nextErrorLogTime = Time.unscaledTime + 5f;
            Log?.LogError($"Target priority failed: {ex}");
        }
    }
}
