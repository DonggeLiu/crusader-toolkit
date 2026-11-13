using System.Collections.Generic;

namespace BuildingProduction
{
    /// <summary>面板里额外显示的存量条。</summary>
    internal enum StockBar
    {
        None,
        QuarryPile, // 采石场的石料堆：快满说明牛运不过来
        DairyHerd,  // 奶牛场攒着的牛：没有皮革作坊时显示
    }

    /// <summary>每种生产建筑（eStructs）消耗和产出的物资（eGoods 编号）以及面板布局的特殊处理。</summary>
    internal sealed class ProductionGoods
    {
        public const int Wood = 2, Hops = 3, Stone = 4, Cows = 5, Iron = 6, Pitch = 7, Wheat = 9, Bread = 10,
            Cheese = 11, Meat = 12, Apples = 13, Ale = 14, Flour = 16, Bows = 17, Crossbows = 18, Spears = 19,
            Pikes = 20, Maces = 21, Swords = 22, LeatherArmour = 23, MetalArmour = 24;

        public const int QuarryType = 20, QuarryPileType = 21, OxTetherType = 4, DairyType = 33, TannerType = 16,
            InnType = 22;

        /// <summary>奶牛场最多养的牛数。</summary>
        public const int DairyHerdMax = 3;

        /// <summary>一头牛在皮革作坊记为几张牛皮（做几件皮甲）。</summary>
        public const int HidesPerCow = 3;

        private static readonly int[] None = new int[0];

        private static readonly Dictionary<int, ProductionGoods> ByType = new Dictionary<int, ProductionGoods>
        {
            [3] = new ProductionGoods(None, new[] { Wood }),                   // 伐木小屋
            [OxTetherType] = new ProductionGoods(new[] { Stone }, new[] { Stone }), // 系牛桩：从石料堆运走、送进库房
            [QuarryType] = new ProductionGoods(None, new[] { Stone }) { Bar = StockBar.QuarryPile }, // 采石场（数据记在它的石料堆上）
            [5] = new ProductionGoods(None, new[] { Iron }),                   // 铁矿
            [6] = new ProductionGoods(None, new[] { Pitch }),                  // 沥青坑
            [7] = new ProductionGoods(None, new[] { Meat }),                   // 猎人小屋
            [30] = new ProductionGoods(None, new[] { Wheat }),                 // 小麦农场
            [31] = new ProductionGoods(None, new[] { Hops }),                  // 啤酒花农场
            [32] = new ProductionGoods(None, new[] { Apples }),                // 苹果园
            [DairyType] = new ProductionGoods(None, new[] { Cheese, Cows }) { Bar = StockBar.DairyHerd }, // 奶牛场：奶酪 + 被皮革作坊牵走的牛
            [34] = new ProductionGoods(new[] { Wheat }, new[] { Flour }),      // 磨坊
            [17] = new ProductionGoods(new[] { Flour }, new[] { Bread }),      // 面包房
            [18] = new ProductionGoods(new[] { Hops }, new[] { Ale }),         // 酿酒房
            [InnType] = new ProductionGoods(new[] { Ale }, None) { Left = 352f }, // 酒馆：中间有酒壶、人口覆盖等文字，表格右移
            // 作坊：产出行的图标就是游戏自己的选择产品按钮（顺序与按钮一致）。
            [12] = new ProductionGoods(new[] { Wood }, new[] { Bows, Crossbows }) { Buttons = new[] { "ProducingBows", "ProducingXBows" } },
            [14] = new ProductionGoods(new[] { Wood }, new[] { Spears, Pikes }) { Buttons = new[] { "ProducingSpears", "ProducingPikes" } },
            [13] = new ProductionGoods(new[] { Iron }, new[] { Swords, Maces }) { Buttons = new[] { "ProducingSwords", "ProducingMaces" } },
            [15] = new ProductionGoods(new[] { Iron }, new[] { MetalArmour }), // 盔甲作坊
            [TannerType] = new ProductionGoods(new[] { Cows }, new[] { LeatherArmour }), // 皮革作坊：从奶牛场牵牛
        };

        // 库房、粮仓、武器库面板里各物资按钮的图标；牛用投石机“投牛”按钮的图标。
        private static readonly Dictionary<int, string> Icons = new Dictionary<int, string>
        {
            [Wood] = "UISprites I001", [Hops] = "UISprites I003", [Stone] = "UISprites I005",
            [Iron] = "UISprites I007", [Pitch] = "UISprites I009", [Wheat] = "UISprites I011",
            [Bread] = "UISprites I013", [Cheese] = "UISprites I015", [Meat] = "UISprites I017",
            [Apples] = "UISprites I019", [Ale] = "UISprites I021", [Flour] = "UISprites I025",
            [Bows] = "UISprites I027", [Crossbows] = "UISprites I029", [Spears] = "UISprites I031",
            [Pikes] = "UISprites I033", [Maces] = "UISprites I035", [Swords] = "UISprites I037",
            [LeatherArmour] = "UISprites I039", [MetalArmour] = "UISprites I041",
            [Cows] = "UI-Buttons J007",
        };

        private ProductionGoods(int[] inputs, int[] outputs)
        {
            Inputs = inputs;
            Outputs = outputs;
        }

        public int[] Inputs { get; }
        public int[] Outputs { get; }

        /// <summary>表格左边距（面板单位）。</summary>
        public float Left { get; private set; } = 318f;

        /// <summary>作坊里与 Outputs 一一对应的产品选择按钮名称；null 表示没有。</summary>
        public string[] Buttons { get; private set; }

        public StockBar Bar { get; private set; }

        public static bool TryGet(int type, out ProductionGoods goods) => ByType.TryGetValue(type, out goods);

        public static string IconKey(int good) => Icons.TryGetValue(good, out string key) ? key : null;
    }
}
