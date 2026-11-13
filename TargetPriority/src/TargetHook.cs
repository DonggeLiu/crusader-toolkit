using Microsoft.Extensions.Logging.Abstractions;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API;
using System;
using System.Runtime.InteropServices;

namespace TargetPriority
{
    /// <summary>
    /// 在原生选目标函数上挂一个 detour：本地玩家的射手调用时临时换上自定义打分表。
    /// </summary>
    internal sealed unsafe class TargetHook
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int SelectTargetFunction(IntPtr units, int unitIndex);

        // 原生单位数组：每项 0x490 字节。
        private const int UnitStride = 0x490, OwnerOffset = 0x6EE, TypeOffset = 0x6E6, RangedTargetOffset = 0x99C;

        private static TargetHook instance;

        private readonly Settings settings;
        private readonly Action<string> log;

        private SelectTargetFunction callback; // 保持委托存活，防止被回收
        private SelectTargetFunction original;
        private HookTransaction transaction;

        private byte* weights, skirmisher, eligibility;
        private readonly byte[] vanillaWeights, vanillaSkirmisher, vanillaEligibility;
        private readonly byte[] customEligibility = new byte[TargetTables.EligibilityTableLength];
        private int alwaysEligibleCase = -1;

        // 按兵种选公式的分派代码：原字节与替换字节。替换代码查 typeTable（0–127 号兵种 → 分支编号）。
        private byte* weightDispatch, skirmisherDispatch;
        private readonly byte[] vanillaWeightDispatch, vanillaSkirmisherDispatch;
        private byte[] customWeightDispatch, customSkirmisherDispatch;
        private IntPtr typeTable; // 兵种 → 乘数；非托管内存，进程结束前一直保留
        private IntPtr tierTable; // 兵种 → 档位（1–10），火力分配的打分代码用
        private byte* function;

        // 火力分配：分派处换成跳到自建打分代码的 stub。
        private readonly FireAllocation fire;
        private byte[] allocationWeightDispatch, allocationSkirmisherDispatch;

        // 原版弹道检查：(上下文, 射手 x, y, 射手高度, 目标 x, y, 目标高度) → >0 表示直线弹道不被挡。
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int LineOfFireFunction(IntPtr context, int shooterX, int shooterY, int shooterHeight, int targetX, int targetY, int targetHeight);
        private LineOfFireFunction lineOfFire;
        private IntPtr lineOfFireContext;

        public bool Installed { get; private set; }

        public TargetHook(Settings settings, Action<string> log)
        {
            this.settings = settings;
            this.log = log;
            fire = new FireAllocation(settings);
            vanillaWeights = TargetTables.FromHex(TargetTables.OriginalWeights);
            vanillaSkirmisher = TargetTables.FromHex(TargetTables.OriginalSkirmisher);
            vanillaEligibility = TargetTables.FromHex(TargetTables.OriginalEligibility);
            vanillaWeightDispatch = TargetTables.FromHex(TargetTables.OriginalWeightDispatch);
            vanillaSkirmisherDispatch = TargetTables.FromHex(TargetTables.OriginalSkirmisherDispatch);
        }

        public void Install()
        {
            IntPtr module = GetModuleHandle("CrusaderDE.dll");
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("CrusaderDE.dll is not loaded.");

            ScanRegion region = ScanRegion.FromModule(module, "CrusaderDE");
            int at = IndexOf(region.Span, TargetTables.FunctionPrologue);
            if (at < 0)
                throw new InvalidOperationException("Target selection function not found (game version changed?).");

            var function = new IntPtr((long)region.BaseAddress + at);
            this.function = (byte*)function;
            weights = (byte*)function + TargetTables.WeightTableOffset;
            skirmisher = (byte*)function + TargetTables.SkirmisherTableOffset;
            eligibility = (byte*)function + TargetTables.EligibilityTableOffset;
            weightDispatch = (byte*)function + TargetTables.WeightDispatchOffset;
            skirmisherDispatch = (byte*)function + TargetTables.SkirmisherDispatchOffset;
            if (!Matches(weights, vanillaWeights) || !Matches(skirmisher, vanillaSkirmisher) || !Matches(eligibility, vanillaEligibility) ||
                !Matches(weightDispatch, vanillaWeightDispatch) || !Matches(skirmisherDispatch, vanillaSkirmisherDispatch))
                throw new InvalidOperationException("Targeting code differs from the expected game version; mod stays inactive.");

            for (int i = 0; i < TargetTables.ShooterHeightOffsets.Length; i++)
                if (!Matches((byte*)function + TargetTables.ShooterHeightOffsets[i], ShooterHeightCode(TargetTables.OriginalShooterHeights[i])))
                    throw new InvalidOperationException("Unexpected shooter height code; mod stays inactive.");
            if (!Matches((byte*)function + TargetTables.ScoreJoinOffset, TargetTables.OriginalScoreJoin))
                throw new InvalidOperationException("Unexpected targeting code layout; mod stays inactive.");
            byte* lea = (byte*)function + TargetTables.LineOfFireContextLeaOffset, call = (byte*)function + TargetTables.LineOfFireCallOffset;
            if (lea[0] != 0x48 || lea[1] != 0x8D || lea[2] != 0x0D || call[0] != 0xE8)
                throw new InvalidOperationException("Unexpected line-of-fire call layout; mod stays inactive.");
            lineOfFireContext = (IntPtr)(lea + 7 + *(int*)(lea + 3));
            lineOfFire = Marshal.GetDelegateForFunctionPointer<LineOfFireFunction>((IntPtr)(call + 5 + *(int*)(call + 1)));
            fire.CanSee = CanSee;
            alwaysEligibleCase = TargetTables.CaseIndex(function, at, TargetTables.EligibilityCasesOffset, 7, TargetTables.AlwaysEligibleCaseOffset);
            if (alwaysEligibleCase < 0)
                throw new InvalidOperationException("Unexpected eligibility table layout; mod stays inactive.");

            // 可选目标表和两段分派代码都在代码段中：改成可写。
            if (!TargetTables.VirtualProtect((IntPtr)eligibility, (UIntPtr)(ulong)TargetTables.EligibilityTableLength, TargetTables.PageExecuteReadWrite, out _))
                throw new InvalidOperationException("VirtualProtect failed: " + Marshal.GetLastWin32Error());
            long codeLength = weightDispatch + vanillaWeightDispatch.Length - skirmisherDispatch;
            if (!TargetTables.VirtualProtect((IntPtr)skirmisherDispatch, (UIntPtr)(ulong)codeLength, TargetTables.PageExecuteReadWrite, out _))
                throw new InvalidOperationException("VirtualProtect failed: " + Marshal.GetLastWin32Error());

            if (!Matches((byte*)function + TargetTables.UntargetedPreferenceOffset, TargetTables.OriginalUntargetedPreference))
                throw new InvalidOperationException("Unexpected final-choice code; mod stays inactive.");
            if (!TargetTables.VirtualProtect((IntPtr)((byte*)function + TargetTables.UntargetedPreferenceOffset), (UIntPtr)3UL, TargetTables.PageExecuteReadWrite, out _))
                throw new InvalidOperationException("VirtualProtect failed: " + Marshal.GetLastWin32Error());

            int heightsStart = TargetTables.ShooterHeightOffsets[0], heightsEnd = TargetTables.ShooterHeightOffsets[2] + 8;
            if (!TargetTables.VirtualProtect((IntPtr)((byte*)function + heightsStart), (UIntPtr)(ulong)(heightsEnd - heightsStart), TargetTables.PageExecuteReadWrite, out _))
                throw new InvalidOperationException("VirtualProtect failed: " + Marshal.GetLastWin32Error());

            typeTable = Marshal.AllocHGlobal(TargetTables.TypeTableLength);
            customWeightDispatch = TargetTables.BuildDispatch(vanillaWeightDispatch.Length, TargetTables.WeightDispatchOffset, typeTable);
            customSkirmisherDispatch = TargetTables.BuildDispatch(vanillaSkirmisherDispatch.Length, TargetTables.SkirmisherDispatchOffset, typeTable);

            fire.SetImage(module);
            tierTable = Marshal.AllocHGlobal(TargetTables.TypeTableLength);
            byte[] score = TargetTables.BuildAllocationScore(tierTable, fire.PenaltyTable,
                (IntPtr)((byte*)function + TargetTables.ScoreJoinOffset), (IntPtr)((byte*)function + TargetTables.LastTierCaseOffset));
            IntPtr scoreCode = TargetTables.VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)score.Length, TargetTables.MemCommitReserve, TargetTables.PageExecuteReadWrite);
            if (scoreCode == IntPtr.Zero)
                throw new InvalidOperationException("VirtualAlloc failed: " + Marshal.GetLastWin32Error());
            Marshal.Copy(score, 0, scoreCode, score.Length);
            allocationWeightDispatch = TargetTables.BuildJumpStub(vanillaWeightDispatch.Length, scoreCode);
            allocationSkirmisherDispatch = TargetTables.BuildJumpStub(vanillaSkirmisherDispatch.Length, scoreCode);

            RebuildTables();

            instance = this;
            callback = Detour;
            transaction = new HookTransaction(region, NullLoggerFactory.Instance, HookTransactionOptions.Default, "TargetPriority");
            DetourHandle<SelectTargetFunction> handle = transaction.AddDetour(
                HookTarget.FromAddress((ulong)function.ToInt64(), 0), callback, "TargetPriority.SelectRangedTarget");
            CommitResult result = transaction.Commit();
            if (!result.IsCompleteSuccess || !handle.TryGetOriginal(out original))
                throw new InvalidOperationException("Detour install failed: " + string.Join("; ", result.Failures));

            Installed = true;
            log($"Hooked ranged target selection at CrusaderDE.dll+0x{at:X}.");
        }

        /// <summary>按当前设置生成自定义表（设置改动后调用）。</summary>
        public void RebuildTables()
        {
            if (typeTable == IntPtr.Zero) return;
            // 每个兵种 → 所属类别的档位 → 乘数；89 号以后不存在，按最后一档。
            for (int type = 0; type < TargetTables.TypeTableLength; type++)
            {
                int tier = type < 89 ? settings.TierFor(type) : TargetTables.TierCount;
                Marshal.WriteByte(typeTable, type, TargetTables.TierMultipliers[tier - 1]);
                if (tierTable != IntPtr.Zero) Marshal.WriteByte(tierTable, type, (byte)tier);
            }

            Array.Copy(vanillaEligibility, customEligibility, customEligibility.Length);
            if (settings.ShootWildAnimals)
            {
                foreach (int type in new[] { TargetTables.Lion, TargetTables.Hyena })
                {
                    int i = type - TargetTables.FirstEligibilityType;
                    if (i >= 0 && i < customEligibility.Length)
                        customEligibility[i] = (byte)alwaysEligibleCase;
                }
            }
        }

        /// <summary>测试用：按玩家统计活着的各兵种数量。</summary>
        public string SummarizeUnits()
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI.Instance.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null,
                SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            var counts = new System.Collections.Generic.SortedDictionary<string, int[]>();
            foreach (int id in ids)
            {
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                // 按 16 格分块，能看出同一兵种分成几群。
                int bx = (*(short*)(element + 0x70E) >> 3) / 16, by = (*(short*)(element + 0x710) >> 3) / 16;
                string key = $"p{*(short*)(element + OwnerOffset)} {TypeName(*(short*)(element + TypeOffset))} [{bx},{by}] z{*(short*)(element + 0x712)}+{*(short*)(element + 0x714)}";
                if (!counts.TryGetValue(key, out int[] c)) counts[key] = c = new int[3];
                c[0]++;
                c[1] += *(short*)(element + 0x70E) >> 3;
                c[2] += *(short*)(element + 0x710) >> 3;
            }
            var sb = new System.Text.StringBuilder($"{ids.Count} alive: ");
            foreach (var kv in counts) sb.Append($"{kv.Key}×{kv.Value[0]} @({kv.Value[1] / kv.Value[0]},{kv.Value[2] / kv.Value[0]}), ");
            return sb.ToString();
        }

        /// <summary>
        /// 测试用：按规则删掉敌方单位，规则如 "XBOWMAN:tower:4,ARCHER:ground:2"
        /// （兵种名同 eChimps 去掉前缀；tower = 站在建筑上，ground = 地面；数量）。返回删掉的人数。
        /// </summary>
        public int TrimEnemies(string rules)
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int local = settings.LocalPlayer, removed = 0;
            foreach (string rule in rules.Split(','))
            {
                string[] p = rule.Trim().Split(':');
                if (p.Length != 3 || !int.TryParse(p[2], out int count)) continue;
                bool onTower = p[1].Trim() == "tower";
                foreach (int id in ids)
                {
                    if (count <= 0) break;
                    if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                    byte* element = (byte*)u - 0x65C;
                    short owner = *(short*)(element + OwnerOffset);
                    if (owner == local || owner <= 0 || players.IsPlayerAlliedTo(local, owner)) continue;
                    if (TypeName(*(short*)(element + TypeOffset)) != p[0].Trim() || (*(short*)(element + 0x714) > 0) != onTower) continue;
                    if (units.DeleteUnitSafe(id)) { removed++; count--; }
                }
            }
            return removed;
        }

        /// <summary>测试用：(fromX, fromY) 8 格内的敌方地面近战兵走向 (x, y)，到了会自己开打。返回下令的人数。</summary>
        public int ChargeEnemies(int fromX, int fromY, int x, int y)
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int local = settings.LocalPlayer, n = 0;
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                short owner = *(short*)(element + OwnerOffset);
                if (owner == local || owner <= 0 || players.IsPlayerAlliedTo(local, owner) || *(short*)(element + 0x714) > 0) continue;
                int tx = *(short*)(element + 0x70E) >> 3, ty = *(short*)(element + 0x710) >> 3;
                if (Math.Abs(tx - fromX) > 8 || Math.Abs(ty - fromY) > 8) continue;
                units.MoveToTile(id, x + (n % 5 - 2), y + n / 5, 0);
                n++;
            }
            return n;
        }

        /// <summary>测试用：双方的地面远程兵都删到每方 size 人（按编号保留前面的）。返回删掉的人数。</summary>
        public int TrimSides(int size)
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            // 站在建筑上的优先保留（塔上对射时删掉地面上的），其余按编号。
            int OnBuilding(int id) =>
                units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* p) && p != null && *(short*)((byte*)p - 0x65C + 0x714) > 0 ? 0 : 1;
            ids.Sort((a, b) => OnBuilding(a) != OnBuilding(b) ? OnBuilding(a) - OnBuilding(b) : a - b);
            var kept = new int[16];
            int removed = 0;
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                short owner = *(short*)(element + OwnerOffset);
                int type = *(short*)(element + TypeOffset);
                if (owner <= 0 || owner >= 16 || !(type == 22 || type == 23 || type == 70 || type == 74 || type == 82)) continue;
                if (kept[owner] < size) kept[owner]++;
                else if (units.DeleteUnitSafe(id)) removed++;
            }
            return removed;
        }

        /// <summary>测试用：本地玩家和第一个敌方玩家各自的士兵人数与总血量。</summary>
        public void CountSides(out int own, out int ownHealth, out int enemy, out int enemyHealth)
        {
            own = ownHealth = enemy = enemyHealth = 0;
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int local = settings.LocalPlayer;
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                short owner = *(short*)(element + OwnerOffset);
                int type = *(short*)(element + TypeOffset), hp = *(int*)(element + 0xA20);
                if (owner <= 0 || type < 22 || type == 45 || type >= 86 || hp <= 0) continue;
                if (owner == local) { own++; ownHealth += hp; }
                else if (!players.IsPlayerAlliedTo(local, owner)) { enemy++; enemyHealth += hp; }
            }
        }

        /// <summary>测试用：每个玩家的远程兵平均分到该玩家的塔所占的格子上（走上塔）。返回下令的人数和找到的塔数。</summary>
        public string GarrisonTowers()
        {
            var buildings = new System.Collections.Generic.List<int>();
            GameBuildingManagerAPI.Instance.GetAllBuildings(buildings, null, null, null, null);
            var towerTiles = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int x, int y)>>();
            int towers = 0;
            foreach (int b in buildings)
            {
                if (!GameBuildingManagerAPI.Instance.TryGetBuildingById(b, out SHCDESE.Interop.GameBuilding* g) || g == null) continue;
                int owner = (int)g->r_PlayerIdOwner, size = (int)g->r_OccupyTileGridSize;
                if (owner <= 0 || size <= 0) continue;
                towers++;
                if (!towerTiles.TryGetValue(owner, out var tiles)) towerTiles[owner] = tiles = new System.Collections.Generic.List<(int, int)>();
                for (int dx = 0; dx < size; dx++)
                    for (int dy = 0; dy < size; dy++)
                        tiles.Add((g->r_TilePositionXBegin + dx, g->r_TilePositionYBegin + dy));
            }
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            var next = new int[16];
            int ordered = 0;
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                short owner = *(short*)(element + OwnerOffset);
                int type = *(short*)(element + TypeOffset);
                if (owner <= 0 || owner >= 16 || !(type == 22 || type == 23 || type == 70 || type == 74 || type == 82)) continue;
                if (!towerTiles.TryGetValue(owner, out var tiles) || tiles.Count == 0) continue;
                var tile = tiles[next[owner]++ % tiles.Count];
                units.MoveToTile(id, tile.x, tile.y, 0);
                ordered++;
            }
            return $"{ordered} shooters to {towers} buildings";
        }

        /// <summary>测试用：所有活着的单位补满血，开打前保证双方起点一致。</summary>
        public int HealAll()
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            int n = 0;
            foreach (int id in ids)
            {
                int max = units.GetMaxHealth(id);
                if (max > 0 && units.GetCurrentHealth(id) < max) { units.SetCurrentHealth(id, max); n++; }
            }
            return n;
        }

        /// <summary>与原函数相同的弹道检查：射手 (z + 建筑高 + 射高) 到目标 (z + 建筑高 + 26) 的直线没被挡住。</summary>
        private bool CanSee(IntPtr shooterPtr, IntPtr targetPtr)
        {
            byte* shooter = (byte*)shooterPtr, t = (byte*)targetPtr;
            int height = TargetTables.ShooterHeight(*(short*)(shooter + TypeOffset)) + *(short*)(shooter + 0x712) + *(short*)(shooter + 0x714);
            return lineOfFire(lineOfFireContext, *(short*)(shooter + 0x70E), *(short*)(shooter + 0x710), height,
                              *(short*)(t + 0x70E), *(short*)(t + 0x710), *(short*)(t + 0x712) + *(short*)(t + 0x714) + 0x1A) > 0;
        }

        // 测试用（塔对塔）：两座塔塔顶的格子范围与单位高度，第一次重建时从现有塔上单位记下。
        private int towerX1, towerY1, towerX2, towerY2, towerW, towerH, towerHeight1, towerHeight2;

        // 实测得到的塔顶格子（相对各自塔的锚点），两塔中心对称后都有效的才用。
        private System.Collections.Generic.List<(int X, int Y)> towerCells;
        // 实测后：玩家 1 塔顶格（绝对格坐标）与中心对称的玩家 2 塔顶格配对。
        private System.Collections.Generic.List<(int X1, int Y1, int X2, int Y2)> towerPairs;

        /// <summary>
        /// 测试用：在两座塔锚点周围每一格各生成一个弓手，下一帧看它的高度是否等于塔顶高度，记下有效格子后删掉。
        /// 需先调用 RebuildTowerArmies 学到塔的位置。返回有效格子说明。
        /// </summary>
        public string ProbeTowerTops()
        {
            if (towerW == 0) return "learn the towers first (Shift+F4)";
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            probeSpawns.Clear();
            for (int side = 1; side <= 2; side++)
            {
                int x0 = side == 1 ? towerX1 : towerX2, y0 = side == 1 ? towerY1 : towerY2, h = side == 1 ? towerHeight1 : towerHeight2;
                for (int dx = -2; dx < towerW + 2; dx++)
                    for (int dy = -2; dy < towerH + 2; dy++)
                    {
                        long id = units.CreateUnitWorld(side, side, (x0 + dx) * 8, (y0 + dy) * 8, h, SHCDESE.Interop.eChimps.CHIMP_TYPE_ARCHER);
                        if (id > 0) probeSpawns.Add(((int)id, side, dx, dy));
                    }
            }
            return $"spawned {probeSpawns.Count} probes; press Shift+F3 again to read them";
        }

        private readonly System.Collections.Generic.List<(int Id, int Side, int Dx, int Dy)> probeSpawns = new System.Collections.Generic.List<(int, int, int, int)>();

        /// <summary>读取 ProbeTowerTops 生成的单位：高度 ≥ 塔顶高度 − 10 且没被挪走的格子算塔顶。删掉探针单位。</summary>
        public string ReadTowerTops()
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            var valid = new[] { null, new System.Collections.Generic.HashSet<(int, int)>(), new System.Collections.Generic.HashSet<(int, int)>() };
            var sample = new System.Text.StringBuilder();
            int missing = 0;
            foreach (var p in probeSpawns)
            {
                if (!units.TryGetUnitById(p.Id, out SHCDESE.Interop.GameUnit* u) || u == null) { missing++; continue; }
                byte* e = (byte*)u - 0x65C;
                if (sample.Length < 600)
                    sample.Append($"[{p.Side} d({p.Dx},{p.Dy}) id {p.Id} raw ({*(short*)(e + 0x70E)},{*(short*)(e + 0x710)}) z{*(short*)(e + 0x712)}+{*(short*)(e + 0x714)} owner {*(short*)(e + OwnerOffset)} hp {*(int*)(e + 0xA20)}] ");
                int x0 = p.Side == 1 ? towerX1 : towerX2, y0 = p.Side == 1 ? towerY1 : towerY2, h = p.Side == 1 ? towerHeight1 : towerHeight2;
                // 用单位实际所在的格（生成坐标会被游戏取整），高度达到塔顶的算塔顶格。
                int cx = (*(short*)(e + 0x70E) >> 3) - x0, cy = (*(short*)(e + 0x710) >> 3) - y0;
                if (*(short*)(e + 0x712) + *(short*)(e + 0x714) >= h - 10) valid[p.Side].Add((cx, cy));
                units.DeleteUnitSafe(p.Id);
            }
            probeSpawns.Clear();
            // 两座塔各自有效格子的中心（取两倍，避免半格），玩家 1 的格 R 对应玩家 2 的格 2·C2 − (2·R − 2·C1)/2。
            towerCells = new System.Collections.Generic.List<(int X, int Y)>();
            towerPairs = new System.Collections.Generic.List<(int, int, int, int)>();
            if (valid[1].Count > 0 && valid[2].Count > 0)
            {
                int sx1 = 0, sy1 = 0, sx2 = 0, sy2 = 0;
                foreach (var c in valid[1]) { sx1 += towerX1 + c.Item1; sy1 += towerY1 + c.Item2; }
                foreach (var c in valid[2]) { sx2 += towerX2 + c.Item1; sy2 += towerY2 + c.Item2; }
                // 中心 × 2（四舍五入到整数，两塔形状相同时正好是整数）
                int cx1 = (int)Math.Round(2.0 * sx1 / valid[1].Count), cy1 = (int)Math.Round(2.0 * sy1 / valid[1].Count);
                int cx2 = (int)Math.Round(2.0 * sx2 / valid[2].Count), cy2 = (int)Math.Round(2.0 * sy2 / valid[2].Count);
                foreach (var c in valid[1])
                {
                    int x1 = towerX1 + c.Item1, y1 = towerY1 + c.Item2;
                    int x2 = (cx2 - (2 * x1 - cx1)) / 2, y2 = (cy2 - (2 * y1 - cy1)) / 2;
                    if (valid[2].Contains((x2 - towerX2, y2 - towerY2))) { towerPairs.Add((x1, y1, x2, y2)); towerCells.Add(c); }
                }
                towerPairs.Sort();
            }
            return $"missing {missing} | pairs {towerPairs.Count} | tower 1 top {valid[1].Count} cells: {string.Join(" ", valid[1])}; tower 2 top {valid[2].Count} cells: {string.Join(" ", valid[2])}; symmetric usable {towerCells.Count}: {string.Join(" ", towerCells)}";
        }

        /// <summary>
        /// 测试用（塔对塔）：删掉玩家 1、2 的所有单位，在两座塔顶按同一种站位各生成 perSide 名弓手。
        /// 两边中心对称：玩家 1 的第 (i, j) 格对应玩家 2 的 (w-1-i, h-1-j) 格，各自的前排都朝向对方。
        /// seed 决定空出哪些格；stack &gt; 1 时另有 stack 个人叠在同一格。返回说明文字（含每格人数）。
        /// </summary>
        public string RebuildTowerArmies(int seed, int stack, int perSide)
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            if (towerW == 0)
            {
                int[] minX = { 0, int.MaxValue, int.MaxValue }, minY = { 0, int.MaxValue, int.MaxValue }, maxX = { 0, int.MinValue, int.MinValue }, maxY = { 0, int.MinValue, int.MinValue }, height = new int[3];
                foreach (int id in ids)
                {
                    if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                    byte* e = (byte*)u - 0x65C;
                    int owner = *(short*)(e + OwnerOffset);
                    if (owner < 1 || owner > 2 || *(short*)(e + 0x714) <= 0) continue;
                    int tx = *(short*)(e + 0x70E) >> 3, ty = *(short*)(e + 0x710) >> 3;
                    minX[owner] = Math.Min(minX[owner], tx); maxX[owner] = Math.Max(maxX[owner], tx);
                    minY[owner] = Math.Min(minY[owner], ty); maxY[owner] = Math.Max(maxY[owner], ty);
                    height[owner] = *(short*)(e + 0x712) + *(short*)(e + 0x714);
                }
                if (height[1] == 0 || height[2] == 0) return "need units on both towers to learn the tower tops";
                // 塔顶尺寸按玩家 1 的塔量（shoot6 两塔相同）；玩家 2 从最大角往回算，避免个别站在边上的人把范围撑大。
                towerW = maxX[1] - minX[1] + 1;
                towerH = maxY[1] - minY[1] + 1;
                // 某一边某行恰好没人时，按另一边补齐：以最大角为准往回算。
                minX[1] = maxX[1] - towerW + 1; minY[1] = maxY[1] - towerH + 1; minX[2] = maxX[2] - towerW + 1; minY[2] = maxY[2] - towerH + 1;
                towerX1 = minX[1]; towerY1 = minY[1]; towerX2 = minX[2]; towerY2 = minY[2];
                towerHeight1 = height[1]; towerHeight2 = height[2];
            }
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                int owner = *(short*)((byte*)u - 0x65C + OwnerOffset);
                if (owner == 1 || owner == 2) units.DeleteUnitSafe(id);
            }
            if (towerPairs == null || towerPairs.Count == 0) return "probe the tower tops first (Shift+F3 twice)";
            var rng = new System.Random(seed);
            var cells = new System.Collections.Generic.List<int>();
            for (int i = 0; i < towerPairs.Count; i++) cells.Add(i);
            for (int i = cells.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int k = cells[i]; cells[i] = cells[j]; cells[j] = k; }
            int distinct = Math.Min(cells.Count, Math.Max(1, perSide - Math.Max(0, stack - 1)));
            var counts = new int[towerPairs.Count];
            for (int i = 0; i < perSide; i++) counts[cells[Math.Min(i, distinct - 1)]]++;
            for (int c = 0; c < counts.Length; c++)
                for (int k = 0; k < counts[c]; k++)
                {
                    var pair = towerPairs[c];
                    // CreateUnitWorld 的坐标是单位结构里的原始坐标（每格 8）；给格的起点，游戏会取整到该格。
                    units.CreateUnitWorld(1, 1, pair.X1 * 8, pair.Y1 * 8, towerHeight1, SHCDESE.Interop.eChimps.CHIMP_TYPE_ARCHER);
                    units.CreateUnitWorld(2, 2, pair.X2 * 8, pair.Y2 * 8, towerHeight2, SHCDESE.Interop.eChimps.CHIMP_TYPE_ARCHER);
                }
            var grid = new System.Text.StringBuilder();
            for (int c = 0; c < counts.Length; c++)
                if (counts[c] > 0) grid.Append($" ({towerPairs[c].X1},{towerPairs[c].Y1})x{counts[c]}");
            return $"seed {seed}, stack {stack}, tower {towerW}x{towerH} at ({towerX1},{towerY1}) h{towerHeight1} / ({towerX2},{towerY2}) h{towerHeight2}, player-1 grid{grid}";
        }

        /// <summary>
        /// 测试用（塔对塔）：把玩家 1、2 塔顶上的弓手按同一种站位重新排好，两边互为中心对称（各自的前排都朝对方）。
        /// 站位网格取各自塔顶弓手当前占用范围（shoot6 为 6 × 4 格）。seed 决定空出哪些格；stack 大于 1 时另把 stack 个人叠在同一格。
        /// 返回说明文字。
        /// </summary>
        public string ArrangeTowers(int seed, int stack)
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            var sides = new[] { null, new System.Collections.Generic.List<int>(), new System.Collections.Generic.List<int>() };
            var minX = new[] { 0, int.MaxValue, int.MaxValue }; var minY = new[] { 0, int.MaxValue, int.MaxValue };
            var maxX = new[] { 0, int.MinValue, int.MinValue }; var maxY = new[] { 0, int.MinValue, int.MinValue };
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* e = (byte*)u - 0x65C;
                int owner = *(short*)(e + OwnerOffset);
                if (owner < 1 || owner > 2 || *(short*)(e + 0x714) <= 0) continue;
                int tx = *(short*)(e + 0x70E) >> 3, ty = *(short*)(e + 0x710) >> 3;
                sides[owner].Add(id);
                minX[owner] = Math.Min(minX[owner], tx); maxX[owner] = Math.Max(maxX[owner], tx);
                minY[owner] = Math.Min(minY[owner], ty); maxY[owner] = Math.Max(maxY[owner], ty);
            }
            if (sides[1].Count == 0 || sides[2].Count == 0) return "no tower units";
            int w = Math.Max(maxX[1] - minX[1], maxX[2] - minX[2]) + 1, h = Math.Max(maxY[1] - minY[1], maxY[2] - minY[2]) + 1;
            // 删人后某一边可能没占满网格：两边都按较大的网格，从各自的最小角算起。
            // 玩家 1 的格子顺序（随机）；玩家 2 用中心对称的同一组格子。
            var rng = new System.Random(seed);
            var cells = new System.Collections.Generic.List<int>();
            for (int i = 0; i < w * h; i++) cells.Add(i);
            for (int i = cells.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int k = cells[i]; cells[i] = cells[j]; cells[j] = k; }
            int n = Math.Min(sides[1].Count, sides[2].Count);
            var slot = new int[n];
            int distinct = Math.Max(1, n - Math.Max(0, stack - 1));
            for (int i = 0; i < n; i++) slot[i] = cells[Math.Min(i, distinct - 1) % cells.Count];
            // 玩家 1 前排朝玩家 2：判断方向后，让两边按“离对方远近”对称。
            bool flipX = minX[2] < minX[1], flipY = minY[2] < minY[1];
            for (int side = 1; side <= 2; side++)
            {
                // 目标格（世界格坐标），再按“离得最近”贪心分配给本方的人，减少互相挤路。
                var targets = new System.Collections.Generic.List<(int X, int Y)>();
                for (int i = 0; i < n; i++)
                {
                    int cx = slot[i] % w, cy = slot[i] / w;
                    if (side == 2) { cx = w - 1 - cx; cy = h - 1 - cy; }
                    if (flipX) cx = w - 1 - cx;
                    if (flipY) cy = h - 1 - cy;
                    targets.Add((minX[side] + cx, minY[side] + cy));
                }
                var free = new System.Collections.Generic.List<int>(sides[side].GetRange(0, n));
                foreach (var cell in targets)
                {
                    int bestIndex = 0, bestDistance = int.MaxValue;
                    for (int k = 0; k < free.Count; k++)
                    {
                        units.TryGetUnitById(free[k], out SHCDESE.Interop.GameUnit* fu);
                        byte* fe = (byte*)fu - 0x65C;
                        int d = Math.Abs((*(short*)(fe + 0x70E) >> 3) - cell.X) + Math.Abs((*(short*)(fe + 0x710) >> 3) - cell.Y);
                        if (d < bestDistance) { bestDistance = d; bestIndex = k; }
                    }
                    units.MoveToTile(free[bestIndex], cell.X, cell.Y, 0);
                    free.RemoveAt(bestIndex);
                }
            }
            return $"seed {seed}, stack {stack}, grid {w}x{h}, {n} per side, {distinct} cells used";
        }

        /// <summary>测试用：本地玩家的远程兵按 5 列、间隔 2 格排成方阵，走到 (x, y) 格附近。返回下令的人数。</summary>
        public int MoveShootersTo(int x, int y, bool enemies = false)
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            units.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int local = settings.LocalPlayer, n = 0;
            foreach (int id in ids)
            {
                if (!units.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                int type = *(short*)(element + TypeOffset);
                short owner = *(short*)(element + OwnerOffset);
                bool wanted = enemies ? owner > 0 && owner != local && !players.IsPlayerAlliedTo(local, owner) && *(short*)(element + 0x714) == 0
                                      : owner == local;
                if (!wanted || !(type == 22 || type == 23 || type == 70 || type == 74 || type == 82)) continue;
                units.MoveToTile(id, x + (n % 5 - 2) * 2, y + (n / 5) * 2, 0);
                n++;
            }
            return n;
        }

        /// <summary>测试用：按玩家统计活着的士兵（不含平民、动物）人数和总血量。</summary>
        public string SummarizeHealth()
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI.Instance.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null,
                SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            var count = new int[16];
            var health = new long[16];
            foreach (int id in ids)
            {
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                int owner = *(short*)(element + OwnerOffset), type = *(short*)(element + TypeOffset);
                if (owner <= 0 || owner >= 16 || type < 22 || type == 45 || type >= 86) continue;
                count[owner]++;
                health[owner] += *(int*)(element + 0xA20);
            }
            var sb = new System.Text.StringBuilder();
            for (int p = 1; p < 16; p++)
                if (count[p] > 0) sb.Append($"p{p} {count[p]} units {health[p]} hp; ");
            return sb.ToString();
        }

        /// <summary>测试用：每个玩家的士兵人数、总血量，以及受伤者各自剩余血量的百分比（升序），用来看伤害是集中还是摊薄。</summary>
        public string SummarizeWounded()
        {
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI.Instance.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null,
                SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            var wounded = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
            var count = new int[16];
            var health = new long[16];
            foreach (int id in ids)
            {
                if (!GameUnitManagerAPI.Instance.TryGetUnitById(id, out SHCDESE.Interop.GameUnit* u) || u == null) continue;
                byte* element = (byte*)u - 0x65C;
                int owner = *(short*)(element + OwnerOffset), type = *(short*)(element + TypeOffset);
                if (owner <= 0 || owner >= 16 || type < 22 || type == 45 || type >= 86) continue;
                int hp = *(int*)(element + 0xA20), max = *(int*)(element + 0xA24);
                count[owner]++;
                health[owner] += hp;
                if (max > 0 && hp < max)
                {
                    if (!wounded.TryGetValue(owner, out var list)) wounded[owner] = list = new System.Collections.Generic.List<int>();
                    list.Add(hp * 100 / max);
                }
            }
            var sb = new System.Text.StringBuilder();
            for (int p = 1; p < 16; p++)
            {
                if (count[p] == 0) continue;
                sb.Append($"p{p} {count[p]} units {health[p]} hp");
                if (wounded.TryGetValue(p, out var list))
                {
                    list.Sort();
                    sb.Append($", wounded {list.Count}: {string.Join(" ", list)}%");
                }
                sb.Append("; ");
            }
            return sb.ToString();
        }

        // 调试统计：原函数被调用的次数、其中按本 mod 表计算的次数、选到目标的次数。
        public static int Calls, Applied, Picked;

        private static IntPtr lastUnits;
        public static int overrideChecks;

        /// <summary>诊断：只让 target 可选（其余敌人分数超过上限）调用一次选目标函数，看它会不会被选中；调用后恢复射手与计数字段和惩罚表。</summary>
        private bool TryOnly(IntPtr units, int shooterId, int target)
        {
            IntPtr table = fire.PenaltyTable;
            var saved = new int[MaxUnits];
            for (int i = 0; i < MaxUnits; i++) saved[i] = Marshal.ReadInt32(table, i * 4);
            for (int i = 0; i < MaxUnits; i++) Marshal.WriteInt32(table, i * 4, 390000);
            Marshal.WriteInt32(table, target * 4, 0);
            byte* s = (byte*)units + (long)shooterId * UnitStride;
            Marshal.Copy((IntPtr)s, shooterBackup, 0, UnitStride);
            for (int i = 0; i < MaxUnits; i++)
            {
                byte* u = (byte*)units + (long)i * UnitStride;
                attackerCounts[i] = *(ushort*)(u + 0xA34);
                targetFlags[i] = *(ushort*)(u + 0x9F2);
            }
            int result = CallWithTables(units, shooterId, 0);
            int chosen = *(short*)(s + RangedTargetOffset);
            Marshal.Copy(shooterBackup, 0, (IntPtr)s, UnitStride);
            for (int i = 0; i < MaxUnits; i++)
            {
                byte* u = (byte*)units + (long)i * UnitStride;
                *(ushort*)(u + 0xA34) = attackerCounts[i];
                *(ushort*)(u + 0x9F2) = targetFlags[i];
            }
            for (int i = 0; i < MaxUnits; i++) Marshal.WriteInt32(table, i * 4, saved[i]);
            return result != 0 && chosen == target;
        }

        private static int Detour(IntPtr units, int unitIndex)
        {
            TargetHook self = instance;
            lastUnits = units;
            Calls++;
            if (self?.original == null) return 0; // 安装完成前不会被调用
            try
            {
                if (!self.ShouldApply(units, unitIndex))
                {
                    int vanilla = self.original(units, unitIndex);
                    // 测试对比：关闭本 mod 时也记录本地玩家射手的原版选择。
                    if (vanilla != 0 && self.settings.LogTargets && self.IsLocal(units, unitIndex))
                        self.LogChoice(units, unitIndex, "vanilla");
                    return vanilla;
                }

                // 测试对比：先按原版规则“试算”一次，记下原版会选谁，再把试算改动的字段全部恢复。
                string vanillaChoice = null;
                if (self.settings.TestTools && self.settings.LogTargets)
                    vanillaChoice = self.DryRunVanilla(units, unitIndex);

                Applied++;
                bool allocate = self.settings.AllocateFire;
                self.fire.PlannedThisSelect = false;
                if (allocate) self.fire.BeforeSelect(units, unitIndex);
                int result = self.CallWithTables(units, unitIndex, 0);
                // 原版规则找不到目标时，站着的射手再看一次城墙后面（移动中的不看：中途停下射击会打乱原版的走位）。
                if (result == 0 && self.settings.ShootOverWalls && self.IsStanding(units, unitIndex))
                {
                    result = self.CallWithTables(units, unitIndex, TargetTables.WallClearance);
                    if (result != 0) self.AimOverWall(units, unitIndex);
                }
                if (result != 0) Picked++;
                if (result != 0 && allocate)
                {
                    self.fire.LastOverrideBest = 0;
                    self.fire.AfterSelect(units, unitIndex);
                    if (self.fire.LastOverrideBest > 0 && self.settings.LogTargets && overrideChecks++ < 60)
                        self.log($"[override check] shooter {unitIndex}: best {self.fire.LastOverrideBest} alone accepted = {self.TryOnly(units, unitIndex, self.fire.LastOverrideBest)}, " +
                                 $"chosen {self.fire.LastOverrideChosen} alone accepted = {self.TryOnly(units, unitIndex, self.fire.LastOverrideChosen)}");
                }

                if (result != 0 && self.settings.LogTargets)
                    self.LogChoice(units, unitIndex, "mod", vanillaChoice, allocate);
                return result;
            }
            catch (Exception ex)
            {
                // 异常绝不能穿回原生代码。
                TargetPriorityPlugin.ReportError(ex);
                return self?.original?.Invoke(units, unitIndex) ?? 0;
            }
        }

        /// <summary>换上本 mod 的表（射高加 extraHeight）调用原函数，返回后换回原版。</summary>
        private int CallWithTables(IntPtr units, int unitIndex, int extraHeight)
        {
            if (settings.AllocateFire)
                Write(customEligibility, allocationWeightDispatch, allocationSkirmisherDispatch, extraHeight);
            else
                Write(customEligibility, customWeightDispatch, customSkirmisherDispatch, extraHeight);
            byte* untargeted = (byte*)function + TargetTables.UntargetedPreferenceOffset + 2;
            if (settings.AllocateFire && settings.StrictScores && fire.PlannedThisSelect) *untargeted = TargetTables.JumpShort;
            try { return original(units, unitIndex); }
            finally
            {
                *untargeted = TargetTables.JumpIfEqualShort;
                Write(vanillaEligibility, vanillaWeightDispatch, vanillaSkirmisherDispatch, 0);
            }
        }

        // 单位当前动作（+0x918），实测：0、1 = 站着待命，4 = 站着射击；101 = 行走中。
        private const int ActionOffset = 0x918;

        private bool IsStanding(IntPtr units, int unitIndex)
        {
            short action = *(short*)((byte*)units + (long)unitIndex * UnitStride + ActionOffset);
            return action == 0 || action == 1 || action == 4;
        }

        /// <summary>
        /// 选到的目标只在加高后才“可达”时（中间隔着城墙），改成手动攻击被挡时原版的做法：目标记为 -2，朝目标所在位置射。
        /// 否则射箭那一刻原版会按真实射高复查弹道，失败就重新选目标，结果一直在瞄、从不放箭。
        /// </summary>
        private void AimOverWall(IntPtr units, int unitIndex)
        {
            byte* shooter = (byte*)units + (long)unitIndex * UnitStride;
            int target = *(short*)(shooter + RangedTargetOffset);
            if (target <= 0 || target >= MaxUnits) return;
            byte* t = (byte*)units + (long)target * UnitStride;
            int height = TargetTables.ShooterHeight(*(short*)(shooter + TypeOffset)) + *(short*)(shooter + 0x712) + *(short*)(shooter + 0x714);
            if (lineOfFire(lineOfFireContext, *(short*)(shooter + 0x70E), *(short*)(shooter + 0x710), height,
                           *(short*)(t + 0x70E), *(short*)(t + 0x710), *(short*)(t + 0x712) + *(short*)(t + 0x714) + 0x1A) > 0)
                return;
            *(short*)(shooter + RangedTargetOffset) = -2;
            *(short*)(shooter + 0x716) = *(short*)(t + 0x70E);
            *(short*)(shooter + 0x718) = *(short*)(t + 0x710);
            *(short*)(shooter + 0x71A) = *(short*)(t + 0x712);
            OverWall++;
        }

        public static int OverWall;

        /// <summary>按射手所属：自己 / 盟友 / 敌人，分别看三个开关。</summary>
        private bool ShouldApply(IntPtr units, int unitIndex)
        {
            short owner = *(short*)((byte*)units + (long)unitIndex * UnitStride + OwnerOffset);
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            int local = settings.LocalPlayer;
            if (owner == local) return settings.Enabled;
            if (!settings.ApplyToAllies && !settings.ApplyToEnemies) return false;
            if (!players.IsPlayerIdValid(owner)) return false;
            return players.IsPlayerAlliedTo(local, owner) ? settings.ApplyToAllies : settings.ApplyToEnemies;
        }

        private bool IsLocal(IntPtr units, int unitIndex)
        {
            short owner = *(short*)((byte*)units + (long)unitIndex * UnitStride + OwnerOffset);
            return owner == settings.LocalPlayer;
        }

        private void Write(byte[] e, byte[] weightCode, byte[] skirmisherCode, int extraHeight)
        {
            for (int i = 0; i < TargetTables.ShooterHeightOffsets.Length; i++)
                *(int*)(function + TargetTables.ShooterHeightOffsets[i] + 4) = TargetTables.OriginalShooterHeights[i] + extraHeight;
            Marshal.Copy(e, 0, (IntPtr)eligibility, e.Length);
            // 代码字节：同一线程在调用原函数之前/之后改写，x86 对自修改代码保证一致性。
            Marshal.Copy(weightCode, 0, (IntPtr)weightDispatch, weightCode.Length);
            Marshal.Copy(skirmisherCode, 0, (IntPtr)skirmisherDispatch, skirmisherCode.Length);
        }

        private void LogChoice(IntPtr units, int unitIndex, string mode, string vanillaChoice = null, bool allocate = false)
        {
            byte* shooter = (byte*)units + (long)unitIndex * UnitStride;
            int target = *(ushort*)(shooter + RangedTargetOffset);
            if (target <= 0 || target >= MaxUnits) return;
            byte* t = (byte*)units + (long)target * UnitStride;
            log($"Target[{mode}]: {TypeName(*(short*)(shooter + TypeOffset))} #{unitIndex} -> " +
                $"{TypeName(*(short*)(t + TypeOffset))} #{target} (owner {*(short*)(t + OwnerOffset)})" +
                (allocate ? fire.Describe(units, target) : "") + CellRank(units, target) +
                (vanillaChoice != null ? $" | vanilla would pick {vanillaChoice}" : ""));
        }

        /// <summary>测试用：目标所在格里同一玩家的活单位有几个、目标按编号排第几（箭只打中格里编号最小的那个）。</summary>
        private string CellRank(IntPtr units, int target)
        {
            byte* t = (byte*)units + (long)target * UnitStride;
            int tx = *(short*)(t + 0x70E) >> 3, ty = *(short*)(t + 0x710) >> 3;
            short owner = *(short*)(t + OwnerOffset);
            int rank = 1, count = 0;
            for (int i = 1; i < MaxUnits; i++)
            {
                byte* u = (byte*)units + (long)i * UnitStride;
                if (*(short*)(u + OwnerOffset) != owner || *(int*)(u + 0xA20) <= 0) continue;
                if ((*(short*)(u + 0x70E) >> 3) != tx || (*(short*)(u + 0x710) >> 3) != ty) continue;
                count++;
                if (i < target) rank++;
            }
            return $" [cell {rank}/{count}]";
        }

        // 原生单位数组容量（编辑器显示上限 2999）。
        private const int MaxUnits = 3000;
        // 选中目标时原函数改动的字段：射手整条记录，以及目标的“攻击者计数”(+0xA34) 和标记 (+0x9F2)。
        private readonly byte[] shooterBackup = new byte[UnitStride];
        private readonly ushort[] attackerCounts = new ushort[MaxUnits], targetFlags = new ushort[MaxUnits];

        /// <summary>测试用：按原版规则试算一次，返回原版会选的目标，并恢复试算造成的全部改动。</summary>
        /// <summary>
        /// 测试用：对几个本方塔上射手，逐个敌人试“只有它分数可选、其余都超过上限”，调用一次选目标函数，看原函数会不会选它。
        /// 每次都把射手和计数字段恢复原样。用来查原函数在弹道检查之外还排除哪些目标。
        /// </summary>
        public string ScanAcceptable()
        {
            IntPtr units = lastUnits;
            if (units == IntPtr.Zero || !settings.AllocateFire) return "no units / allocation off";
            var ids = new System.Collections.Generic.List<int>();
            GameUnitManagerAPI.Instance.GetAllUnits(ids, SHCDESE.Interop.Enums.AliveState.IsAlive, null, SHCDESE.Interop.Enums.PlayerRelationship.Any, null);
            int local = settings.LocalPlayer;
            var shooters = new System.Collections.Generic.List<int>();
            var enemies = new System.Collections.Generic.List<int>();
            foreach (int id in ids)
            {
                if (id <= 0 || id >= MaxUnits) continue;
                byte* u = (byte*)units + (long)id * UnitStride;
                int owner = *(short*)(u + OwnerOffset);
                if (*(int*)(u + 0xA20) <= 0 || owner <= 0) continue;
                if (owner == local && *(short*)(u + TypeOffset) == 22 && *(short*)(u + 0x714) > 0) shooters.Add(id);
                else if (owner != local && *(short*)(u + 0x714) > 0) enemies.Add(id);
            }
            if (shooters.Count == 0 || enemies.Count == 0) return "no tower units";
            IntPtr table = fire.PenaltyTable;
            var saved = new int[enemies.Count];
            for (int i = 0; i < enemies.Count; i++) saved[i] = Marshal.ReadInt32(table, enemies[i] * 4);
            var sb = new System.Text.StringBuilder();
            foreach (int shooterId in new[] { shooters[0], shooters[shooters.Count / 2], shooters[shooters.Count - 1] })
            {
                byte* s = (byte*)units + (long)shooterId * UnitStride;
                sb.Append($"shooter {shooterId} at ({*(short*)(s + 0x70E) >> 3},{*(short*)(s + 0x710) >> 3}): ");
                int accepted = 0;
                foreach (int e in enemies)
                {
                    foreach (int other in enemies) Marshal.WriteInt32(table, other * 4, 390000);
                    Marshal.WriteInt32(table, e * 4, 0);
                    Marshal.Copy((IntPtr)s, shooterBackup, 0, UnitStride);
                    for (int i = 0; i < MaxUnits; i++)
                    {
                        byte* u = (byte*)units + (long)i * UnitStride;
                        attackerCounts[i] = *(ushort*)(u + 0xA34);
                        targetFlags[i] = *(ushort*)(u + 0x9F2);
                    }
                    int result = CallWithTables(units, shooterId, 0);
                    int chosen = *(short*)(s + RangedTargetOffset);
                    Marshal.Copy(shooterBackup, 0, (IntPtr)s, UnitStride);
                    for (int i = 0; i < MaxUnits; i++)
                    {
                        byte* u = (byte*)units + (long)i * UnitStride;
                        *(ushort*)(u + 0xA34) = attackerCounts[i];
                        *(ushort*)(u + 0x9F2) = targetFlags[i];
                    }
                    bool ok = result != 0 && chosen == e;
                    if (ok) accepted++;
                    byte* t = (byte*)units + (long)e * UnitStride;
                    sb.Append($"{e}@({*(short*)(t + 0x70E) >> 3},{*(short*)(t + 0x710) >> 3}){(ok ? "+" : "-")}{(CanSee((IntPtr)s, (IntPtr)t) ? "" : "L")} ");
                }
                sb.Append($"| accepted {accepted}/{enemies.Count}; ");
            }
            for (int i = 0; i < enemies.Count; i++) Marshal.WriteInt32(table, enemies[i] * 4, saved[i]);
            return sb.ToString();
        }

        private string DryRunVanilla(IntPtr units, int unitIndex)
        {
            byte* shooter = (byte*)units + (long)unitIndex * UnitStride;
            Marshal.Copy((IntPtr)shooter, shooterBackup, 0, UnitStride);
            for (int i = 0; i < MaxUnits; i++)
            {
                byte* u = (byte*)units + (long)i * UnitStride;
                attackerCounts[i] = *(ushort*)(u + 0xA34);
                targetFlags[i] = *(ushort*)(u + 0x9F2);
            }

            int result = original(units, unitIndex); // 此时表是原版的
            string choice = "nothing";
            if (result != 0)
            {
                int target = *(ushort*)(shooter + RangedTargetOffset);
                if (target > 0 && target < MaxUnits)
                    choice = $"{TypeName(*(short*)((byte*)units + (long)target * UnitStride + TypeOffset))} #{target}";
            }

            Marshal.Copy(shooterBackup, 0, (IntPtr)shooter, UnitStride);
            for (int i = 0; i < MaxUnits; i++)
            {
                byte* u = (byte*)units + (long)i * UnitStride;
                *(ushort*)(u + 0xA34) = attackerCounts[i];
                *(ushort*)(u + 0x9F2) = targetFlags[i];
            }
            return choice;
        }

        // eChimps 的底层类型是 ushort；未定义的编号 ToString 会直接输出数字。
        private static string TypeName(int type) =>
            ((SHCDESE.Interop.eChimps)(ushort)type).ToString().Replace("CHIMP_TYPE_", "");

        /// <summary>mov dword ptr [rsp+50h], imm32</summary>
        private static byte[] ShooterHeightCode(int height)
        {
            var code = new byte[] { 0xC7, 0x44, 0x24, 0x50, 0, 0, 0, 0 };
            BitConverter.GetBytes(height).CopyTo(code, 4);
            return code;
        }

        private static bool Matches(byte* at, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (at[i] != expected[i]) return false;
            return true;
        }

        private static int IndexOf(ReadOnlySpan<byte> haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
