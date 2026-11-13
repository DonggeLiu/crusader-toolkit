using SHCDESE.Interop;
using UnityEngine;

namespace ResourceLayout
{
    /// <summary>可参与排布的建筑：放置映射、边长（与原版放置时传给合法性校验的参数一致）、显示用的颜色和图标。</summary>
    internal sealed class LayoutType
    {
        public LayoutType(string name, eMappers mapper, int size, int structType, Color color, string icon, int resourceMask = 0)
        {
            ResourceMask = resourceMask;
            Name = name;
            Mapper = mapper;
            Size = size;
            StructType = structType;
            Color = color;
            Icon = icon;
        }

        public string Name { get; }
        public string DisplayName
        {
            get
            {
                if (ReleaseText.IsChinese) return Name;
                switch (Name)
                {
                    case "小麦": return "Wheat";
                    case "啤酒花": return "Hops";
                    case "苹果": return "Apples";
                    case "奶牛": return "Dairy";
                    case "采石场": return "Quarry";
                    case "铁矿场": return "Iron mine";
                    case "沥青坑": return "Pitch rig";
                    case "系牛桩": return "Ox tether";
                    default: return Name;
                }
            }
        }
        public eMappers Mapper { get; }
        public int Size { get; }
        public int StructType { get; }
        public Color Color { get; }
        public string Icon { get; }

        /// <summary>地形逻辑层（LogicLayer）里表示该资源的位（石 0x20000、铁 0x80000）。资源要求由原版校验判断，这里仅作说明。</summary>
        public int ResourceMask { get; }

        // 原版放置校验按这些位数格子：采石场占地里至少 8 格石头，铁矿至少 4 格铁，沥青坑至少 1 格沥青（符号位）。
        public const int StoneBit = 0x20000, IronBit = 0x80000, PitchBit = unchecked((int)0x80000000);

        /// <summary>采石场会在旁边自动生成 2×2 的石料堆（相对采石场左上角 +7,+2），规划时一并预留。</summary>
        public bool HasQuarryPile => StructType == 20;

        public const int PileDx = 7, PileDy = 2, PileSize = 2;

        /// <summary>实心的工业建筑需要工人能走到占地四周的某一格（见 AccessCheck）；农场的田地可以走，不需要。</summary>
        public bool NeedsAccess => ResourceMask != 0;

        public static readonly LayoutType[] Farms =
        {
            new LayoutType("小麦", eMappers.MAPPER_WHEATFARM, 9, 30, new Color(1f, 0.85f, 0.25f), "UI-Buildings E007"),
            new LayoutType("啤酒花", eMappers.MAPPER_HOPSFARM, 9, 31, new Color(0.45f, 0.95f, 0.35f), "UI-Buildings E009"),
            new LayoutType("苹果", eMappers.MAPPER_APPLEFARM, 10, 32, new Color(1f, 0.35f, 0.3f), "UI-Buildings E005"),
            new LayoutType("奶牛", eMappers.MAPPER_CATTLEFARM, 10, 33, new Color(0.55f, 0.8f, 1f), "UI-Buildings E003"),
        };

        public static readonly LayoutType[] Industry =
        {
            new LayoutType("采石场", eMappers.MAPPER_QUARRY, 6, 20, new Color(0.9f, 0.9f, 0.9f), "UI-Buildings D005", StoneBit),
            new LayoutType("铁矿场", eMappers.MAPPER_IRON_MINE, 4, 5, new Color(0.55f, 0.75f, 1f), "UI-Buildings D009", IronBit),
            new LayoutType("沥青坑", eMappers.MAPPER_PITCH_WORKINGS, 4, 6, new Color(0.85f, 0.5f, 1f), "UI-Buildings D011", PitchBit),
        };
    }
}
