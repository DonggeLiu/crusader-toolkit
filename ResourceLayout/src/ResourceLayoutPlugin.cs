using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Detours;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ResourceLayout
{
    [BepInDependency(ScriptExtenderGuid, "2.10.1")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ResourceLayoutPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        public const string PluginGuid = "ResourceLayout_Dongge";
        public const string PluginName = "Resource Layout";
        public const string PluginVersion = "1.0.3";

        private enum Mode { None, Farm, Industry }

        // 本作会在启动后销毁插件的 GameObject，所以用引用比较（is null / ?.）。
        private static ResourceLayoutPlugin instance;
        internal static BepInEx.Logging.ManualLogSource Log => instance?.Logger;

        private ConfigEntry<bool> enabledSetting;
        private ConfigEntry<string> farmSelection;
        private ConfigEntry<int> timeLimitMs;
        private ConfigEntry<float> lineWidth;
        private ConfigEntry<string> farmMinimums;
        private ConfigEntry<bool> debugTools;

        private volatile bool libraryLoaded;
        private volatile bool resetRequested;
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        private Mode mode = Mode.None;
        private readonly bool[] farmSelected = { true, true, false, false };
        private readonly int[] farmMins = new int[4];

        private Region region;
        private int regionHx = -1, regionHy = -1;
        private string regionKey;
        private float nextRegionAttempt;
        private CancellationTokenSource solveCancel;
        private volatile Optimizer.Result result;
        // 采石场布局对应的系牛桩（主线程在结果出来后计算）。
        private TetherPlanner.Result tethers;
        private Optimizer.Result tethersFor;
        private volatile bool solving;

        private struct Ghost
        {
            public LayoutType Type;
            public int X, Y;
        }

        private readonly List<Ghost> pinned = new List<Ghost>();
        private float nextPrune;

        private readonly GhostRenderer ghosts = new GhostRenderer();
        private readonly PlannerPanel panel = new PlannerPanel();
        private readonly GhostIcons icons = new GhostIcons();
        // 调试：玩家亲手建造时看到的建造参数（按放置映射），用于“把规划的建筑全部建出来”的测试。
        private readonly Dictionary<SHCDESE.Interop.eMappers, int> buildUnknown1 = new Dictionary<SHCDESE.Interop.eMappers, int>();
        private float nextErrorLogTime;
        private int dbgX = -1, dbgY = -1;
        private int industryHx = -1, industryHy = -1;
        private LayoutType industryHere;

        private void Awake()
        {
            instance = this;
            enabledSetting = Config.Bind("General", "Enabled", true, "Enable resource-area layout planning.");
            farmSelection = Config.Bind("General", "FarmSelection", "1100", "Last farm selection: wheat, hops, apples, dairy (1 = selected).");
            timeLimitMs = Config.Bind("General", "TimeLimitMs", 5000,
                new ConfigDescription("Solver time limit in milliseconds. Timed-out solutions are not claimed optimal.", new AcceptableValueRange<int>(200, 30000)));
            lineWidth = Config.Bind("Appearance", "LineWidthPixels", 2.5f, "Ghost outline width in screen pixels.");
            farmMinimums = Config.Bind("General", "FarmMinimums", "0,0,0,0", "Minimum farm counts: wheat, hops, apples, dairy.");
            debugTools = Config.Bind("Debug", "DebugTools", false,
                "Developer tools, single-player only: F10 builds pinned ghosts; F11 deletes own farms/mines/tethers within 40 tiles; F12 logs workers; F9 toggles ox sampling. Default off.");

            string[] minText = (farmMinimums.Value ?? "").Split(',');
            for (int i = 0; i < farmMins.Length && i < minText.Length; i++)
                int.TryParse(minText[i], out farmMins[i]);
            string sel = farmSelection.Value ?? "";
            for (int i = 0; i < farmSelected.Length && i < sel.Length; i++)
                farmSelected[i] = sel[i] == '1';

            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e => { if (e.Phase == EventHookPhase.Post) resetRequested = true; }));
            subscriptions.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(e => { if (e.Phase == EventHookPhase.Post) resetRequested = true; }));
            subscriptions.Add(BuildingR3EventHooks.OnBuildStructure.Observable.Subscribe(e =>
            {
                if (e.Phase == EventHookPhase.Pre)
                {
                    buildUnknown1[e.Mappers] = e.Unknown1;
                    if (debugTools.Value)
                        Logger.LogInfo($"build event {e.Mappers} at ({e.TileX},{e.TileY}) scale={e.BuildingScaleUnknown} u1={e.Unknown1} free={e.IsFree}");
                }
            }));

            CrusaderLibrary.Instance.LibraryLoaded += _ => libraryLoaded = true;
            if (CrusaderLibrary.Instance.IsLibraryLoaded())
                libraryLoaded = true;

            var harmony = new Harmony(PluginGuid);
            harmony.Patch(AccessTools.Method(typeof(KeyManager), "Update"),
                postfix: new HarmonyMethod(typeof(ResourceLayoutPlugin), nameof(AfterKeyManagerUpdate)));
            // 规划时让原版认为鼠标在界面上：地图点击（选中、下令、放置）都不生效。
            harmony.Patch(AccessTools.Method(typeof(FatControler), nameof(FatControler.overNoesisGUI)),
                prefix: new HarmonyMethod(typeof(ResourceLayoutPlugin), nameof(BeforeOverNoesisGUI)));

            try
            {
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(this, PluginName,
                    new ResourceLayoutSettings(enabledSetting), ReleaseText.SettingsPath, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to register Mod Options panel: {ex}");
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private static bool BeforeOverNoesisGUI(ref bool __result)
        {
            if (instance == null || instance.mode == Mode.None)
                return true;
            __result = true;
            return false;
        }

        private static void AfterKeyManagerUpdate() => instance?.OnFrame();

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
                    Logger.LogError($"Resource layout frame failed: {ex}");
                }
            }
        }

        private void Tick()
        {
            bool inGame = GameMap.instance != null && MainControls.instance != null && Director.instance != null &&
                          Director.instance.SimRunning && GameData.Instance?.lastGameState != null;
            if (resetRequested)
            {
                resetRequested = false;
                pinned.Clear();
                ExitMode();
            }
            if (!inGame || !enabledSetting.Value)
            {
                ExitMode();
                ghosts.HideAll();
                icons.HideAll();
                return;
            }

            HandleKeys();
            if (debugTools.Value && !Director.instance.MultiplayerGame)
            {
                DebugKeys();
                if (dbgSampling)
                    DebugSampleQuarryUnits();
            }

            if (mode != Mode.None)
                Plan();
            else
                panel.Hide();

            if (mode == Mode.None)
                HandleGhostDoubleClick();
            PrunePinned();
            DrawAll();
        }

        private void HandleKeys()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.X))
            {
                if (shift) Toggle(Mode.Farm);
                else ExitMode(); // 直接按 X 开始普通放置
            }
            if (Input.GetKeyDown(KeyCode.N))
            {
                if (shift) Toggle(Mode.Industry);
                else ExitMode();
            }

            // Shift+数字：该类型最少个数 +1（未选中则选中）；Alt+Shift+数字：-1，已是 0 时取消选中。
            if (mode == Mode.Farm && shift)
            {
                bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                for (int i = 0; i < farmSelected.Length; i++)
                {
                    if (!Input.GetKeyDown(KeyCode.Alpha1 + i))
                        continue;
                    if (!alt)
                    {
                        if (!farmSelected[i]) ToggleFarm(i);
                        else SetMin(i, farmMins[i] + 1);
                    }
                    else if (farmMins[i] > 0)
                        SetMin(i, farmMins[i] - 1);
                    else if (farmSelected[i])
                        ToggleFarm(i);
                }
            }
        }

        private void Toggle(Mode m)
        {
            if (mode == m)
            {
                ExitMode();
                return;
            }
            ExitMode();
            mode = m;
            // 正在放置建筑时进入规划：先取消原版的放置预览，免得两套预览叠在一起。
            if (MainControls.instance != null && MainControls.instance.CurrentAction == 5)
                EditorDirector.instance?.CancelPlacement();
            Logger.LogInfo($"Planning mode: {m}");
        }

        private void ExitMode()
        {
            if (mode == Mode.None)
                return;
            mode = Mode.None;
            CancelSolve();
            region = null;
            regionKey = null;
            result = null;
            industryHx = industryHy = -1;
            panel.Hide();
        }

        private void ToggleFarm(int i)
        {
            farmSelected[i] = !farmSelected[i];
            char[] s = new char[farmSelected.Length];
            for (int k = 0; k < s.Length; k++)
                s[k] = farmSelected[k] ? '1' : '0';
            farmSelection.Value = new string(s);
            regionKey = null; // 类型变了，重算
        }

        private void SetMin(int i, int value)
        {
            farmMins[i] = Math.Max(0, Math.Min(50, value));
            farmMinimums.Value = string.Join(",", Array.ConvertAll(farmMins, v => v.ToString()));
            regionKey = null;
        }

        private int[] ActiveMins(LayoutType[] types)
        {
            var m = new int[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                int idx = Array.IndexOf(LayoutType.Farms, types[i]);
                if (idx >= 0)
                    m[i] = farmMins[idx];
            }
            return m;
        }

        /// <summary>鼠标所在格的资源对应的工业建筑：先看逻辑层的资源位，没有再看哪种建筑能覆盖这一格。</summary>
        private static LayoutType IndustryTypeAt(int x, int y)
        {
            int logic = GameGrid.Logic(x, y);
            foreach (LayoutType t in LayoutType.Industry)
                if (t.ResourceMask != 0 && (logic & t.ResourceMask) != 0)
                    return t;
            // 资源格旁边的空地（如沼泽里没有沥青的部分）：占地覆盖这一格的某个位置能放下就算。
            foreach (LayoutType t in LayoutType.Industry)
                for (int dx = 0; dx < t.Size; dx++)
                for (int dy = 0; dy < t.Size; dy++)
                    if (GameGrid.CanPlace(t, x - dx, y - dy))
                        return t;
            return null;
        }

        private LayoutType[] ActiveTypes()
        {
            if (mode == Mode.Industry)
                return LayoutType.Industry;
            var list = new List<LayoutType>();
            for (int i = 0; i < LayoutType.Farms.Length; i++)
                if (farmSelected[i])
                    list.Add(LayoutType.Farms[i]);
            return list.ToArray();
        }

        private void Plan()
        {
            LayoutType[] shownTypes = mode == Mode.Farm ? LayoutType.Farms : LayoutType.Industry;
            bool[] shownSelected = mode == Mode.Farm ? farmSelected : new[] { true, true, true };

            // 点击面板上的类型开关。
            bool overPanel = panel.IsMouseOver;
            if (Input.GetMouseButtonDown(0) && overPanel && mode == Mode.Farm)
            {
                int t = panel.ToggleUnderMouse();
                if (t >= 0)
                    ToggleFarm(t);
            }

            LayoutType[] types = ActiveTypes();
            int hx = 0, hy = 0;
            bool haveMouse = !overPanel && GameGrid.MouseTile(out hx, out hy);
            // 工业：只规划鼠标下这种资源对应的建筑（石矿、铁矿相邻时不会被混成一片去平衡数量）。
            if (mode == Mode.Industry)
            {
                if (haveMouse && (hx != industryHx || hy != industryHy))
                {
                    industryHx = hx;
                    industryHy = hy;
                    industryHere = IndustryTypeAt(hx, hy);
                }
                LayoutType here = haveMouse ? industryHere : null;
                if (debugTools.Value && haveMouse && (hx != dbgX || hy != dbgY))
                {
                    dbgX = hx;
                    dbgY = hy;
                    Logger.LogInfo($"industry mouse ({hx},{hy}) logic={GameGrid.Logic(hx, hy):X} type={here?.Name ?? "-"} structure={GameGrid.HasStructure(hx, hy)} ironValid={GameGrid.CanPlace(LayoutType.Industry[1], hx, hy)}");
                }
                types = here != null ? new[] { here } : region?.Types ?? new[] { LayoutType.Industry[0] };
            }
            int[] activeMins = ActiveMins(types);
            string key = string.Join(",", Array.ConvertAll(types, t => t.Name)) + "|" + string.Join(",", activeMins);

            if (types.Length > 0 && haveMouse && Time.unscaledTime >= nextRegionAttempt &&
                (key != regionKey || region == null || !region.Covers(hx, hy)) && (hx != regionHx || hy != regionHy || key != regionKey))
            {
                nextRegionAttempt = Time.unscaledTime + 0.15f;
                regionHx = hx;
                regionHy = hy;
                regionKey = key;
                StartPlan(types, activeMins, hx, hy);
            }

            if (Input.GetMouseButtonDown(1))
            {
                pinned.Clear();
                ExitMode();
                return;
            }

            if (Input.GetMouseButtonDown(0) && !overPanel)
            {
                Pin();
                ExitMode();
                return;
            }

            // 面板内容。
            Optimizer.Result r = result;
            if (r != null && !solving && region != null && !ReferenceEquals(tethersFor, r))
            {
                tethersFor = r;
                tethers = null;
                if (region.Types.Length > 0 && region.Types[0].HasQuarryPile && r.Placements.Count > 0)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    tethers = TetherPlanner.Plan(region, r.Placements);
                    Logger.LogInfo($"Tethers: {tethers.Tethers.Count} (needed per quarry [{string.Join(",", tethers.Needed)}], placed [{string.Join(",", tethers.PerQuarry)}]) in {sw.Elapsed.TotalMilliseconds:0} ms");
                }
            }
            int[] counts = new int[shownTypes.Length];
            string status;
            if (types.Length == 0)
                status = ReleaseText.Pick("请至少选择一种建筑", "Select at least one building type.");
            else if (region == null || region.Candidates.Count == 0)
                status = mode == Mode.Farm ? ReleaseText.Pick("把鼠标移到绿洲（可建农场的草地）上", "Move the cursor onto an oasis / farmland.") : ReleaseText.Pick("把鼠标移到石矿、铁矿或沥青地上", "Move the cursor onto stone, iron or pitch.");
            else if (r == null || solving)
                status = ReleaseText.Pick("计算中…", "Calculating…");
            else
            {
                int total = 0;
                for (int i = 0; i < region.Types.Length; i++)
                {
                    int idx = Array.IndexOf(shownTypes, region.Types[i]);
                    if (idx >= 0) counts[idx] = r.Counts[i];
                    total += r.Counts[i];
                }
                status = ReleaseText.Pick($"共 {total} 个 · ", $"{total} buildings · ") + (r.Optimal ? ReleaseText.Pick("已证明最优", "Proven optimal") : ReleaseText.Pick($"限时内的最好布局，未证明最优（理论上最多 {r.UpperBoundTotal} 个）", $"Best found within time limit; not proven optimal (upper bound {r.UpperBoundTotal})."));
                TetherPlanner.Result tp = tethers;
                if (tp != null)
                {
                    status += ReleaseText.Pick(
                        $"\n系牛桩 ×{tp.Tethers.Count}；每个采石场估算 {string.Join("/", tp.Needed)} 个" + (tp.HasStockpile ? "（按最近库房距离）" : "（未找到库房，按每个 3 个）"),
                        $"\nOx tethers ×{tp.Tethers.Count}; estimated per quarry: {string.Join("/", tp.Needed)}" + (tp.HasStockpile ? " (nearest-stockpile distance)" : " (no stockpile found; 3 per quarry)"));
                    if (tp.Shortfall > 0)
                        status += ReleaseText.Pick($"，还差 {tp.Shortfall} 个没有位置", $"; no space for {tp.Shortfall} more.");
                }
                var missing = new List<string>();
                for (int i = 0; i < region.Types.Length; i++)
                    if (!r.Present[i])
                        missing.Add(region.Types[i].DisplayName);
                if (missing.Count > 0 && mode == Mode.Farm)
                    status += ReleaseText.Pick($"\n{string.Join("、", missing)}：这片区域放不下", $"\nCannot fit here: {string.Join(", ", missing)}.");
                if (!r.MinimumsMet)
                    status += ReleaseText.Pick("\n放不下设定的最少个数，显示的是最接近的布局", "\nRequested minimums do not fit; showing the closest layout found.");
            }

            string heading = mode == Mode.Farm ? ReleaseText.Pick("农场智能排布", "Farm layout") : ReleaseText.Pick("工业智能排布", "Industry layout");
            string hint = (mode == Mode.Farm ? ReleaseText.Pick("点图标选择类型 · Shift+数字 最少个数+1 · Alt+Shift+数字 -1\n", "Click icons to select · Shift+number: minimum +1 · Alt+Shift+number: -1\n") : "") +
                ReleaseText.Pick("左键固定 · 右键取消并清除 · ", "Left-click: pin · Right-click: cancel and clear · ") +
                (mode == Mode.Farm ? "Shift+X" : "Shift+N") + ReleaseText.Pick(" 退出", ": exit");
            panel.Show(heading, shownTypes, shownSelected, counts, mode == Mode.Farm ? farmMins : null, status, hint);
        }

        private void StartPlan(LayoutType[] types, int[] mins, int hx, int hy)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Region built = Region.Build(types, hx, hy);
            sw.Stop();
            // 移到不能放的地方时保留上一片区域的结果（类型变了则清空）。
            if (built.Candidates.Count == 0)
            {
                if (region != null && !SameTypes(region.Types, types))
                {
                    CancelSolve();
                    region = null;
                    result = null;
                }
                return;
            }

            CancelSolve();
            region = built;
            result = null;

            Logger.LogInfo($"Region at ({hx},{hy}): {region.Origins.Count} origins, {region.Candidates.Count} candidates, " +
                           $"bbox {region.MaxX - region.MinX + 1}x{region.MaxY - region.MinY + 1}, built in {sw.Elapsed.TotalMilliseconds:0} ms");

            var cts = new CancellationTokenSource();
            solveCancel = cts;
            Region r = region;
            int limit = timeLimitMs.Value;
            solving = true;
            var thread = new Thread(() =>
            {
                try
                {
                    Optimizer.Result res = new Optimizer(r, mins, limit, cts.Token).Solve();
                    if (!cts.IsCancellationRequested)
                    {
                        result = res;
                        solving = false;
                        Logger.LogInfo($"Solved: counts=[{string.Join(",", res.Counts)}] optimal={res.Optimal} nodes={res.Nodes} {res.Milliseconds:0} ms");
                    }
                }
                catch (Exception ex)
                {
                    solving = false;
                    Logger.LogError($"Layout solve failed: {ex}");
                }
            }, 16 * 1024 * 1024) { IsBackground = true, Name = "ResourceLayoutSolver" };
            thread.Start();
        }

        private static bool SameTypes(LayoutType[] a, LayoutType[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (!ReferenceEquals(a[i], b[i]))
                    return false;
            return true;
        }

        private void CancelSolve()
        {
            solveCancel?.Cancel();
            solveCancel = null;
            solving = false;
        }

        private void Pin()
        {
            Optimizer.Result r = result;
            Region reg = region;
            if (r == null || reg == null || solving)
                return;

            // 同一片区域再次固定时替换旧的幽灵。
            pinned.RemoveAll(g => reg.Origins.Contains(Region.Key(g.X, g.Y)));
            foreach (Placement p in r.Placements)
                pinned.Add(new Ghost { Type = reg.Types[p.Type], X = p.X, Y = p.Y });
            if (tethers != null && ReferenceEquals(tethersFor, r))
                foreach (Placement p in tethers.Tethers)
                    pinned.Add(new Ghost { Type = TetherPlanner.Tether, X = p.X, Y = p.Y });
            Logger.LogInfo($"Pinned {r.Placements.Count} ghosts ({pinned.Count} total).");
        }

        // 建成（占地上有了建筑）的幽灵自动消失。
        private void PrunePinned()
        {
            if (pinned.Count == 0 || Time.unscaledTime < nextPrune)
                return;
            nextPrune = Time.unscaledTime + 0.5f;
            pinned.RemoveAll(g =>
            {
                for (int dx = 0; dx < g.Type.Size; dx++)
                for (int dy = 0; dy < g.Type.Size; dy++)
                    if (GameGrid.HasStructure(g.X + dx, g.Y + dy))
                        return true;
                return false;
            });
        }

        private void DrawAll()
        {
            ghosts.Begin();
            icons.Begin();
            float w = lineWidth.Value;
            foreach (Ghost g in pinned)
            {
                ghosts.Draw(g.Type, g.X, g.Y, mode == Mode.None ? 0.8f : 0.45f, w);
                icons.Draw(g.Type, g.X, g.Y, mode == Mode.None ? 0.75f : 0.4f);
            }

            Optimizer.Result r = result;
            Region reg = region;
            if (mode != Mode.None && r != null && reg != null && !solving)
            {
                foreach (Placement p in r.Placements)
                {
                    ghosts.Draw(reg.Types[p.Type], p.X, p.Y, 0.95f, w);
                    icons.Draw(reg.Types[p.Type], p.X, p.Y, 0.85f);
                }
                TetherPlanner.Result tp = tethers;
                if (tp != null && ReferenceEquals(tethersFor, r))
                    foreach (Placement p in tp.Tethers)
                    {
                        ghosts.Draw(TetherPlanner.Tether, p.X, p.Y, 0.95f, w);
                        icons.Draw(TetherPlanner.Tether, p.X, p.Y, 0.85f);
                    }
            }
            ghosts.End();
            icons.End();
        }

        // ---- 双击幽灵建造 ----

        private const float DoubleClickSeconds = 0.5f;
        private int lastClickGhost = -1;
        private float lastClickTime;

        // 不在放置建筑时，0.5 秒内在同一个幽灵上点两下左键：用原版建造函数在幽灵位置下单（与手动放置一样扣资源、校验合法性）。
        private void HandleGhostDoubleClick()
        {
            if (Director.instance == null || Director.instance.MultiplayerGame || pinned.Count == 0 || !Input.GetMouseButtonDown(0))
                return;
            MainControls controls = MainControls.instance;
            bool overUi = FatControler.instance != null && FatControler.instance.overNoesisGUI();
            if (controls == null || controls.CurrentAction != 0)
                return;
            if (overUi)
                return;
            if (!GameGrid.MouseTile(out int mx, out int my))
                return;

            int hit = -1;
            for (int i = 0; i < pinned.Count; i++)
            {
                Ghost g = pinned[i];
                if (mx >= g.X && my >= g.Y && mx < g.X + g.Type.Size && my < g.Y + g.Type.Size)
                {
                    hit = i;
                    break;
                }
            }
            if (debugTools.Value)
                Logger.LogInfo($"ghost click: tile=({mx},{my}) hit={hit} dt={Time.unscaledTime - lastClickTime:0.00}");
            if (hit < 0)
            {
                lastClickGhost = -1;
                return;
            }

            Ghost target = pinned[hit];
            int key = target.X * 1000 + target.Y;
            if (lastClickGhost == key && Time.unscaledTime - lastClickTime <= DoubleClickSeconds)
            {
                lastClickGhost = -1;
                bool ok = BuildGhost(target);
                Logger.LogInfo($"double-click build {target.Type.Name} at ({target.X},{target.Y}): {(ok ? "built" : "failed")}");
                return;
            }
            lastClickGhost = key;
            lastClickTime = Time.unscaledTime;
        }

        private const int DefaultVariant = 15;
        private BulkBuildingDetours.c_game_player_build_structure_delegate buildFn;

        private bool BuildGhost(Ghost g)
        {
            if (Director.instance == null || Director.instance.MultiplayerGame) return false;
            if (buildFn == null)
            {
                var field = typeof(BulkBuildingDetours).GetField("c_game_player_build_structure_hook",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                object handle = field.GetValue(null);
                buildFn = (BulkBuildingDetours.c_game_player_build_structure_delegate)
                    handle.GetType().GetProperty("Original").GetValue(handle);
            }
            // 原版内部（AI 等）建造时这个参数都传 15（默认外观）；玩家手动放置时是界面算出的旋转值。
            // 玩家手动放过这种建筑就沿用那次的值，否则用 15。传 0 会得到错误的外观（沥青坑变成别的贴图）。
            if (!buildUnknown1.TryGetValue(g.Type.Mapper, out int u1))
                u1 = DefaultVariant;
            bool valid = GameGrid.Validate(g.Type, g.X, g.Y, out long vret);
            if (!valid) return false;
            long ret = buildFn(GameTileManagerAPI.Instance.GetTileManager(), GamePlayerManagerAPI.Instance.GetLocalPlayerId(),
                g.X, g.Y, g.Type.Mapper, g.Type.Size, u1, 0);
            bool built = AnyStructure(g);
            if (debugTools.Value)
                Logger.LogInfo($"build {g.Type.Name} at ({g.X},{g.Y}) u1={u1}: validBefore={valid}({vret}) ret={ret} built={built}");
            return built;
        }

        // ---- 开发测试工具（配置 Debug.DebugTools 打开） ----

        private void DebugKeys()
        {
            if (Input.GetKeyDown(KeyCode.F10))
                DebugBuildPinned();
            if (Input.GetKeyDown(KeyCode.F11) && GameGrid.MouseTile(out int mx, out int my))
                DebugClearAround(mx, my);
            if (Input.GetKeyDown(KeyCode.F12))
                DebugWorkers();
            if (Input.GetKeyDown(KeyCode.F9))
            {
                dbgSampling = !dbgSampling;
                Logger.LogInfo($"quarry sampling {(dbgSampling ? "on" : "off")}");
            }
        }

        private readonly List<int> dbgUnits = new List<int>();
        private readonly Dictionary<int, string> dbgLast = new Dictionary<int, string>();
        private float nextDbgSample;
        private bool dbgSampling;

        // 调试：每 0.2 秒采样搬运工和牛，状态或目标变化时记一行（带游戏时间），用来测走路速度。
        private unsafe void DebugSampleQuarryUnits()
        {
            if (Time.unscaledTime < nextDbgSample)
                return;
            nextDbgSample = Time.unscaledTime + 0.2f;
            int t = GameData.Instance?.lastGameState?.game_time ?? 0;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            foreach (var chimp in new[] { SHCDESE.Interop.eChimps.CHIMP_TYPE_QUARRY_GRUNT, SHCDESE.Interop.eChimps.CHIMP_TYPE_QUARRY_OX })
            {
                dbgUnits.Clear();
                units.GetAllUnits(dbgUnits, SHCDESE.Interop.Enums.AliveState.IsAlive, chimp, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
                foreach (int id in dbgUnits)
                {
                    if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null)
                        continue;
                    byte* raw = (byte*)u;
                    string key = $"{*(ushort*)(raw + 0x2BC)} {*(short*)(raw + 0x332)} {*(short*)(raw + 0x384)}";
                    if (dbgLast.TryGetValue(id, out string last) && last == key)
                        continue;
                    dbgLast[id] = key;
                    Logger.LogInfo($"qs t={t} {(chimp == SHCDESE.Interop.eChimps.CHIMP_TYPE_QUARRY_OX ? "ox" : "gr")} #{id} teth={u->r_LinkedProductionBuildingId} " +
                                   $"state/target/load={key} pos=({u->r_CurrentTilePositionX},{u->r_CurrentTilePositionY})");
                }
            }
        }

        // 采石相关单位（石匠 7、搬运工 8、牛 9）的状态：所属建筑、状态机、载货、目标建筑、目标格、位置。
        private unsafe void DebugQuarryUnits()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            foreach (var chimp in new[] { SHCDESE.Interop.eChimps.CHIMP_TYPE_QUARRY_MASON, SHCDESE.Interop.eChimps.CHIMP_TYPE_QUARRY_GRUNT, SHCDESE.Interop.eChimps.CHIMP_TYPE_QUARRY_OX })
            {
                dbgUnits.Clear();
                units.GetAllUnits(dbgUnits, SHCDESE.Interop.Enums.AliveState.IsAlive, chimp, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
                foreach (int id in dbgUnits)
                {
                    if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null)
                        continue;
                    byte* raw = (byte*)u;
                    int linked = u->r_LinkedProductionBuildingId;
                    string lt = "-";
                    if (GameBuildingManagerAPI.Instance.TryGetBuildingById(linked, out SHCDESE.Interop.GameBuilding* lb) && lb != null)
                    {
                        if (lb->r_PlayerIdOwner != player)
                            continue;
                        lt = $"{lb->r_BuildingType}#{linked} stone={lb->r_StoneBlocksAmount}";
                    }
                    int target = *(short*)(raw + 0x332);
                    string tt = "-";
                    if (GameBuildingManagerAPI.Instance.TryGetBuildingById(target, out SHCDESE.Interop.GameBuilding* tb) && tb != null)
                        tt = $"{tb->r_BuildingType}#{target} stone={tb->r_StoneBlocksAmount}";
                    Logger.LogInfo($"unit {chimp} #{id} pos=({u->r_CurrentTilePositionX},{u->r_CurrentTilePositionY}) state={*(ushort*)(raw + 0x2BC)} " +
                                   $"load={*(short*)(raw + 0x384)} linked={lt} target={tt} home=({*(ushort*)(raw + 0x336)},{*(ushort*)(raw + 0x338)}) f40C={raw[0x40C]}");
                }
            }
            foreach (int id in GameBuildingManagerAPI.Instance.GetAllAliveBuildings())
            {
                if (GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out SHCDESE.Interop.GameBuilding* b) && b != null &&
                    b->r_PlayerIdOwner == player && ((int)b->r_BuildingType == 21 || (int)b->r_BuildingType == 4 || (int)b->r_BuildingType == 20))
                    Logger.LogInfo($"bld {b->r_BuildingType}#{id} at ({b->r_TilePositionXBegin},{b->r_TilePositionYBegin}) stone={b->r_StoneBlocksAmount} pile={b->r_StoneQuarry_StockPileBuildingId}");
            }
        }

        // 列出自己的矿场、采石场、农场的工人数和当前入口。
        private unsafe void DebugWorkers()
        {
            DebugQuarryUnits();
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            int[] types = { 30, 31, 32, 33, 20, 5, 6 };
            foreach (int id in GameBuildingManagerAPI.Instance.GetAllAliveBuildings())
            {
                if (!GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out SHCDESE.Interop.GameBuilding* b) || b == null)
                    continue;
                if (b->r_PlayerIdOwner != player || Array.IndexOf(types, (int)b->r_BuildingType) < 0)
                    continue;
                Logger.LogInfo($"workers {b->r_BuildingType} #{id} at ({b->r_TilePositionXBegin},{b->r_TilePositionYBegin}) " +
                               $"{b->r_TotalCurrentWorkers}/{b->r_TotalWorkersRequired} missing={b->r_TotalMissingWorkers} " +
                               $"access=({b->r_TileAccessPositionX},{b->r_TileAccessPositionY}) cursor={b->r_AccessCandidateCursor} sleeping={b->r_IsSleeping}");
            }
        }

        // 用原版建造函数（与玩家点击放置相同，正常扣资源）把已固定的幽灵逐个建出来，记录每个是否成功。
        private void DebugBuildPinned()
        {
            int ok = 0, total = pinned.Count;
            foreach (Ghost g in pinned.ToArray())
            {
                bool valid = GameGrid.CanPlace(g.Type, g.X, g.Y);
                bool built = BuildGhost(g);
                if (built)
                    ok++;
                Logger.LogInfo($"debug build {g.Type.Name} at ({g.X},{g.Y}) validBefore={valid} built={built}");
                if (!valid)
                {
                    var chimps = GameTileManagerAPI.Instance.GetChimpLayer();
                    var sb = new System.Text.StringBuilder();
                    for (int yy = g.Y - 1; yy <= g.Y + g.Type.Size; yy++)
                    {
                        for (int xx = g.X - 1; xx <= g.X + g.Type.Size; xx++)
                        {
                            int id = GameTileManagerAPI.Instance.GetTileId(xx, yy);
                            char ch = GameGrid.HasStructure(xx, yy) ? 'B' : chimps[id] != 0 ? 'u' : '.';
                            int lg = GameGrid.Logic(xx, yy);
                            if (ch == '.' && (lg & 0x8000) == 0) ch = '~';
                            sb.Append(ch);
                        }
                        sb.AppendLine();
                    }
                    Logger.LogInfo("  footprint (+1 border): B=building u=unit ~=no 0x8000 bit" + Environment.NewLine + sb);
                }
            }
            Logger.LogInfo($"debug build: {ok}/{total} built");
        }

        private static bool AnyStructure(Ghost g)
        {
            for (int dx = 0; dx < g.Type.Size; dx++)
            for (int dy = 0; dy < g.Type.Size; dy++)
                if (GameGrid.HasStructure(g.X + dx, g.Y + dy))
                    return true;
            return false;
        }

        // 删掉鼠标附近自己的农场、矿场、采石场（含石料堆）、系牛桩，给测试腾地方。
        private unsafe void DebugClearAround(int cx, int cy)
        {
            int player = GamePlayerManagerAPI.Instance.GetLocalPlayerId();
            int[] types = { 30, 31, 32, 33, 20, 21, 5, 6, 4 };
            int n = 0;
            foreach (int id in GameBuildingManagerAPI.Instance.GetAllAliveBuildings())
            {
                if (!GameBuildingManagerAPI.Instance.TryGetBuildingById(id, out SHCDESE.Interop.GameBuilding* b) || b == null)
                    continue;
                if (b->r_PlayerIdOwner != player || Array.IndexOf(types, (int)b->r_BuildingType) < 0)
                    continue;
                if (Math.Abs(b->r_TilePositionXBegin - cx) > 40 || Math.Abs(b->r_TilePositionYBegin - cy) > 40)
                    continue;
                if (GameBuildingManagerAPI.Instance.DeleteBuildingSafe(id))
                    n++;
            }
            Logger.LogInfo($"debug clear: deleted {n} buildings around ({cx},{cy})");
        }
    }
}
