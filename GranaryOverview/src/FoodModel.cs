using System;
using System.Collections.Generic;

namespace GranaryOverview
{
    /// <summary>缺口要补的建筑：图标资源名和座数。</summary>
    internal struct BuildHint
    {
        public string Icon;
        public int Count;
    }

    /// <summary>
    /// 按原版规则估算各配给等级的食物收支（实测确认）：
    /// 开饭进度每个游戏刻增加 人口 × 3 × 配给系数（半份 0.5、全 1、额外 1.5、双倍 2，无配给不开饭），
    /// 超过 15000 就开饭一次、进度清零；每次开饭吃掉 1 份食物，在正在吃的几种食物之间轮流。
    /// 一个游戏月约 800 刻。原版的“可供应 N 个月”= 总库存 ÷ 当前配给的消耗，不算产量。
    /// </summary>
    internal sealed class FoodModel
    {
        public const int Levels = 5;
        private static readonly double[] RationFactor = { 0, 0.5, 1, 1.5, 2 };
        private const int MealClock = 15000;

        /// <summary>各配给等级的人气（界面显示值）。</summary>
        public static readonly int[] RationPopularity = { -8, -4, 0, 4, 8 };

        public static readonly string[] RationNames = { "无配给", "少量配给", "全配给", "额外配给", "双倍配给" };

        // 面包、奶酪、肉、苹果（与 food_types_not_eatable 的下标一致）。
        public static readonly int[] Foods = { FoodLedger.Bread, FoodLedger.Cheese, FoodLedger.Meat, FoodLedger.Apples };

        // 输入
        public int Population;
        public int Ration;
        public double TicksPerMonth = 800;
        public readonly int[] Stock = new int[4];
        public readonly bool[] Enabled = new bool[4];
        public readonly double[] Production = new double[4]; // 每月
        public int WheatStock, FlourStock;

        // 各建筑每月实际产出（每座）和面包链的转换率；没有数据时用默认值。
        public double AppleFarmRate, DairyRate, HunterRate, BakeryRate, MillRate, WheatFarmRate;
        public double BreadPerFlour, FlourPerWheat;

        // 输出
        public readonly double[] Net = new double[Levels];     // 每月净变化（只算正在吃的食物）
        public readonly double?[] Months = new double?[Levels]; // 净变化为负时库存能撑几个月
        public readonly BuildHint?[] Hints = new BuildHint?[4]; // 长期缺口要补的建筑
        public readonly bool[] Sustained = new bool[4];          // 正在吃且长期持平
        public int TypesEaten;

        public static double MealsPerMonth(int population, int level, double ticksPerMonth)
        {
            double inc = population * 3 * RationFactor[level];
            if (inc <= 0)
                return 0;
            int ticks = (int)((MealClock - 1) / inc) + 1;
            return ticksPerMonth / ticks;
        }

        public void Compute()
        {
            // 正在吃的食物：没被关掉，而且有库存或有产出（原版在有库存的几种之间轮流）。
            var eaten = new List<int>();
            double production = 0, stock = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!Enabled[i])
                    continue;
                production += Production[i];
                stock += Stock[i];
                if (Stock[i] > 0 || Production[i] > 0.05)
                    eaten.Add(i);
            }
            TypesEaten = eaten.Count;

            for (int level = 0; level < Levels; level++)
            {
                double use = MealsPerMonth(Population, level, TicksPerMonth);
                Net[level] = production - use;
                Months[level] = Net[level] < 0 ? stock / -Net[level] : (double?)null;
            }


            // 各食物按当前配给平分开饭次数；产量跟不上自己那一份的就是长期维持不了。
            double share = eaten.Count > 0 ? MealsPerMonth(Population, Ration, TicksPerMonth) / eaten.Count : 0;
            for (int i = 0; i < 4; i++)
            {
                Hints[i] = null;
                Sustained[i] = false;
                if (!eaten.Contains(i) || share <= 0)
                    continue;
                double deficit = share - Production[i];
                if (deficit <= 0.5)
                {
                    Sustained[i] = true;
                    continue;
                }
                Hints[i] = HintFor(Foods[i], deficit);
            }
        }

        private BuildHint HintFor(int food, double deficit)
        {
            switch (food)
            {
                case FoodLedger.Apples:
                    return Hint("UI-Buildings E005", deficit, AppleFarmRate);
                case FoodLedger.Cheese:
                    return Hint("UI-Buildings E003", deficit, DairyRate);
                case FoodLedger.Meat:
                    return Hint("UI-Buildings E001", deficit, HunterRate);
                default:
                    // 面包：面粉有存货说明面包房不够；没有面粉但有小麦说明磨坊不够；都没有就是小麦不够。
                    if (FlourStock >= 5)
                        return Hint("UI-Buildings J003", deficit, BakeryRate);
                    if (WheatStock >= 5)
                        return Hint("UI-Buildings J005", deficit, MillRate * BreadPerFlour);
                    return Hint("UI-Buildings E007", deficit, WheatFarmRate * FlourPerWheat * BreadPerFlour);
            }
        }

        private static BuildHint Hint(string icon, double deficit, double perBuilding)
        {
            return new BuildHint { Icon = icon, Count = perBuilding > 0 ? Math.Max(1, (int)Math.Ceiling(deficit / perBuilding - 0.05)) : 1 };
        }
    }
}
