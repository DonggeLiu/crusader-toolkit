using System;
using System.Runtime.InteropServices;

namespace TargetPriority
{
    /// <summary>
    /// 原版远程单位自动选目标（CrusaderDE.dll RVA 0x18E9A0）的打分表。
    ///
    /// 原版对每个射程内、弹道可达的候选目标算一个分数，分数最低的被选中：
    ///   s = 与目标的切比雪夫距离（世界坐标，1 格 = 8）+ 50 × 已在打它的射手数
    /// 然后按目标兵种套用下面 6 个公式之一（弓、弩、投石兵、骑射、掷火兵等共用一张表，
    /// 散兵另有一张内容相同的表）。哪个兵种用哪个公式由两张按兵种编号索引的字节表决定；
    /// 另有一张“是否可作为目标”的表，狮子、鬣狗只有在特定状态下才会被选。
    ///
    /// 本 mod 只在启用的射手（默认只有本地玩家的）选目标时，临时换上自己的分派代码和可选目标表，
    /// 返回后立即换回；其他射手照常使用原版。启用前逐字节核对，与本游戏版本不符就不启用。
    /// </summary>
    internal static class TargetTables
    {
        // 选目标函数开头 32 字节（在 CrusaderDE.dll 中唯一）。
        public static readonly byte[] FunctionPrologue =
        {
            0x48, 0x89, 0x4C, 0x24, 0x08, 0x53, 0x55, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41,
            0x57, 0x48, 0x81, 0xEC, 0x88, 0x00, 0x00, 0x00, 0x45, 0x33, 0xD2, 0x4C, 0x63, 0xFA, 0x49, 0x69,
        };

        // 相对函数开头的偏移。
        public const int WeightTableOffset = 0x121C;      // 普通射手：按 (兵种 - 22) 索引，64 项
        public const int WeightCasesOffset = 0x1204;
        public const int SkirmisherTableOffset = 0x11C4;  // 散兵：同上
        public const int SkirmisherCasesOffset = 0x11AC;
        public const int EligibilityTableOffset = 0x1090; // 可选目标：按 (兵种 - 44) 索引，44 项
        public const int EligibilityCasesOffset = 0x1074;
        public const int FirstWeightedType = 22, WeightTableLength = 64;
        public const int FirstEligibilityType = 44, EligibilityTableLength = 44;

        public const int AlwaysEligibleCaseOffset = 0xA56;

        // 按兵种选公式的分派代码（add r9d,-22 / cmp r9d,63 / ja 最后一档 / 查字节表 / 查跳转表 / jmp）。
        // 原版只有 6 个公式，且字节表只覆盖 22–85 号兵种（农民、工人等共用最后一档）。本 mod 在自己的射手
        // 选目标时把这段代码临时换成等长的新代码：按兵种查一张 0–127 号的自建乘数表，
        //   分数 = (s + 40) × 乘数
        // 然后跳到各公式共用的比较处（+0xCF3，cmp ebx,r14d），返回后立即换回原字节。
        // r9d 在分派后的所有路径上都会重新赋值；eax 在比较处之后不再使用。
        public const int WeightDispatchOffset = 0xCB2;      // 普通射手，33 字节
        public const int SkirmisherDispatchOffset = 0xC5A;  // 散兵，37 字节
        public const string OriginalWeightDispatch = "4183c1ea4183f93f772f4963c10fb68402bcfb18008b8c82a4fb18004803caffe1";
        public const string OriginalSkirmisherDispatch = "4183c1ea4183f93f0f87830000004963c10fb6840264fb18008b8c824cfb18004803caffe1";
        public const int LastTierCaseOffset = 0xCEB;        // 越界兵种仍走原版最后一档
        public const int ScoreJoinOffset = 0xCF3;           // 各公式算完后的共同入口
        // 循环结束后（+0xE44）：若“还没人瞄的目标”（+0x9F2 == 0）中的最佳分数低于全体最佳的 1.5 倍，原版改选它。
        // 本 mod 按自己的分数选目标时把这里的 je 改成 jmp，直接用全体最佳（2026-10-08 查明）。
        public const int UntargetedPreferenceOffset = 0xE44;
        public static readonly byte[] OriginalUntargetedPreference = { 0x85, 0xFF, 0x74 }; // test edi,edi / je
        public const byte JumpShort = 0xEB, JumpIfEqualShort = 0x74;
        public static readonly byte[] OriginalScoreJoin = { 0x41, 0x3B, 0xDE }; // cmp ebx, r14d
        public const int TypeTableLength = 128;

        // 自动选目标时的“弹道可达”检查（RVA 0xA06F0）：从射手 (z + 射高) 到目标 (z + 26) 拉一条直线，
        // 沿途有城墙等高于这条线就不选。射高是函数开头写入 [rsp+50h] 的常数：一般 30，骑射 45，83 号兵种 60。
        // 实测：紧贴城墙外的骑射打城内地面目标还差约 40，打主堡顶上的单位差约 50；手动下令攻击时照样能打中。
        // 本 mod 的射手按原版规则找不到目标、且站着不动时，把这三个常数各加 WallClearance 再选一次，返回后换回。
        public static readonly int[] ShooterHeightOffsets = { 0x3E, 0xFB, 0x10A }; // 每处 8 字节：mov dword [rsp+50h], imm32
        public static readonly int[] OriginalShooterHeights = { 0x1E, 0x2D, 0x3C };
        public const int WallClearance = 60;

        /// <summary>某兵种原版的射高（选目标函数按兵种写入 [rsp+50h] 的值；射箭时的复查也用同一个值）。</summary>
        public static int ShooterHeight(int type) => type == 74 ? 0x2D : type == 83 ? 0x3C : 0x1E;

        // 选目标函数里调用弹道检查的两条指令：lea rcx,[rip+上下文] 与 call 检查函数。从这里取地址，不写死 RVA。
        public const int LineOfFireContextLeaOffset = 0xD26; // 48 8D 0D disp32
        public const int LineOfFireCallOffset = 0xD44;       // E8 rel32

        // 10 个档位的乘数（档位 1 最优先）。同档先打近的；低一档的目标要近很多才会先被打。
        public static readonly byte[] TierMultipliers = { 2, 3, 4, 6, 8, 10, 12, 16, 20, 24 };
        public const int TierCount = 10;

        /// <summary>
        /// 生成替换代码：cmp r9d,127 / ja 原版最后一档 / mov rax,乘数表 / movzx eax,byte [rax+r9] /
        /// add ebx,40 / imul ebx,eax / jmp 共同入口；剩余字节填 nop。
        /// </summary>
        public static byte[] BuildDispatch(int originalLength, int dispatchOffset, IntPtr multiplierTable)
        {
            var code = new System.Collections.Generic.List<byte> { 0x41, 0x83, 0xF9, TypeTableLength - 1 };
            int rel = LastTierCaseOffset - (dispatchOffset + code.Count + 2);
            if (rel >= -128 && rel <= 127) code.AddRange(new byte[] { 0x77, (byte)(sbyte)rel });
            else
            {
                code.AddRange(new byte[] { 0x0F, 0x87 });
                code.AddRange(BitConverter.GetBytes(LastTierCaseOffset - (dispatchOffset + code.Count + 4)));
            }
            code.AddRange(new byte[] { 0x48, 0xB8 });
            code.AddRange(BitConverter.GetBytes(multiplierTable.ToInt64()));
            code.AddRange(new byte[] { 0x42, 0x0F, 0xB6, 0x04, 0x08 }); // movzx eax, byte [rax+r9]
            code.AddRange(new byte[] { 0x83, 0xC3, 0x28 });             // add ebx, 40
            code.AddRange(new byte[] { 0x0F, 0xAF, 0xD8 });             // imul ebx, eax
            int jump = ScoreJoinOffset - (dispatchOffset + code.Count + 2);
            code.AddRange(new byte[] { 0xEB, checked((byte)(sbyte)jump) });
            while (code.Count < originalLength) code.Add(0x90);
            if (code.Count != originalLength)
                throw new InvalidOperationException("Dispatch patch length mismatch.");
            return code.ToArray();
        }

        // 火力分配打分：档位是严格分段，每档占 TierBand 分；同档内的先后由惩罚表决定。
        // 原版分数 ≥ 400000 的目标不会被选，所以 10 档 × TierBand 加上饱和惩罚必须低于它（见 FireAllocation）。
        public const int TierBand = 15000;

        /// <summary>
        /// 火力分配用的打分代码，放在自己分配的可执行内存里；分派处只放一条跳过来的 stub（BuildJumpStub）。
        /// 进来时：r9d = 目标兵种，ebx = s（距离 + 50 × 攻击者数），rdi + r8 = 目标单位，[rsp+4Ch] = 目标编号。
        /// rdx 是原函数后面查跳转表用的模块基址，不能动；rax、rcx 可用（原分派代码也会改）。
        ///   去掉 50 × 攻击者数 → s + 40 + 档位 × TierBand → 加惩罚表（同档排序、饱和）→ 跳回共同入口。
        /// </summary>
        public static byte[] BuildAllocationScore(IntPtr tierTable, IntPtr penaltyTable, IntPtr scoreJoin, IntPtr lastTier)
        {
            var code = new System.Collections.Generic.List<byte> { 0x41, 0x83, 0xF9, TypeTableLength - 1 }; // cmp r9d,127
            code.AddRange(new byte[] { 0x77, 0 });                                  // ja last（下面回填）
            int jaAt = code.Count - 1;
            code.AddRange(new byte[] { 0x42, 0x0F, 0xBF, 0x84, 0x07, 0x34, 0x0A, 0x00, 0x00 }); // movsx eax, word [rdi+r8+0A34h]
            code.AddRange(new byte[] { 0x6B, 0xC0, 0x32 });                         // imul eax, eax, 50
            code.AddRange(new byte[] { 0x29, 0xC3 });                               // sub ebx, eax
            code.AddRange(new byte[] { 0x79, 0x02, 0x31, 0xDB });                   // jns +2 / xor ebx, ebx
            code.AddRange(new byte[] { 0x48, 0xB8 });                               // mov rax, 档位表
            code.AddRange(BitConverter.GetBytes(tierTable.ToInt64()));
            code.AddRange(new byte[] { 0x42, 0x0F, 0xB6, 0x04, 0x08 });             // movzx eax, byte [rax+r9]
            code.AddRange(new byte[] { 0x69, 0xC0 });                               // imul eax, eax, TierBand
            code.AddRange(BitConverter.GetBytes(TierBand));
            code.AddRange(new byte[] { 0x01, 0xC3 });                               // add ebx, eax
            code.AddRange(new byte[] { 0x83, 0xC3, 0x28 });                         // add ebx, 40
            code.AddRange(new byte[] { 0x48, 0x63, 0x44, 0x24, 0x4C });             // movsxd rax, dword [rsp+4Ch]
            code.AddRange(new byte[] { 0x48, 0xB9 });                               // mov rcx, 惩罚表
            code.AddRange(BitConverter.GetBytes(penaltyTable.ToInt64()));
            code.AddRange(new byte[] { 0x03, 0x1C, 0x81 });                         // add ebx, [rcx+rax*4]
            code.AddRange(JumpTo(scoreJoin));
            code[jaAt] = checked((byte)(code.Count - (jaAt + 1)));
            code.AddRange(JumpTo(lastTier));
            return code.ToArray();
        }

        /// <summary>分派处的跳转 stub：mov rax, 地址 / jmp rax，余下填 nop。</summary>
        public static byte[] BuildJumpStub(int originalLength, IntPtr destination)
        {
            var code = new System.Collections.Generic.List<byte>(JumpTo(destination));
            while (code.Count < originalLength) code.Add(0x90);
            return code.ToArray();
        }

        private static byte[] JumpTo(IntPtr address)
        {
            var code = new System.Collections.Generic.List<byte> { 0x48, 0xB8 };
            code.AddRange(BitConverter.GetBytes(address.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });
            return code.ToArray();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);
        public const uint MemCommitReserve = 0x3000;

        // 本游戏版本（2.8.0.2）的原始表，用于启用前核对。
        public const string OriginalWeights = "00000101010101020305050505050505050303040505050505050505050505050503050505050504050505050505050500010101000104040104030000000103";
        public const string OriginalSkirmisher = "00000101010101020305050505050505050303040505050505050505050505050503050505050404050505050505050500010101000104040104030000000103";
        public const string OriginalEligibility = "0001020606060606060606060606060606060606060606030606060606040606060606060605060606060001";

        public const int Lion = 45, Hyena = 87;

        public static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>在跳转表里找指向某个分支的编号；表项是相对模块基址的 RVA。</summary>
        public static int CaseIndex(IntPtr function, long functionRva, int casesOffset, int caseCount, int wantedCaseOffset)
        {
            for (int i = 0; i < caseCount; i++)
            {
                int rva = Marshal.ReadInt32(function, casesOffset + i * 4);
                if (rva - functionRva == wantedCaseOffset)
                    return i;
            }
            return -1;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        public const uint PageExecuteReadWrite = 0x40;
    }
}
