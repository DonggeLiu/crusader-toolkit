using GranaryOverview;
using SHCDESE.API;
using System.Reflection;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
var names = new[] { "BuildGrid", "StockDelta", "GranaryOverview", "BuildingProduction", "ContinuousRecruit", "TradePresets", "ResourceLayout" };
foreach (string language in new[] { "zh-CN", "zh-TW", "ZH", "en", "de", "fr", "", null })
{
    GameAssetManagerAPI.Instance.CurrentLanguage = language;
    bool zh = language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
    foreach (string name in names)
    {
        Type type = Assembly.GetExecutingAssembly().GetType(name + ".ReleaseText");
        Check((string)type.GetProperty("SettingsPath").GetValue(null) == name + (zh ? "/Settings.zh.xaml" : "/Settings.xaml"), name + " settings " + language);
        Check((string)type.GetMethod("Pick").Invoke(null, new object[] { "中文", "English" }) == (zh ? "中文" : "English"), name + " fallback " + language);
    }
    foreach (var layout in ResourceLayout.LayoutType.Farms.Concat(ResourceLayout.LayoutType.Industry))
    {
        string key = layout.Name;
        Check(zh ? layout.DisplayName == key : layout.DisplayName.All(c => c < 128), "Layout display language");
        Check(layout.Name == key, "Internal layout key stable");
    }
}
Check(FoodModel.MealsPerMonth(0, 4, 800) == 0, "No population means no food consumption");
Check(FoodModel.MealsPerMonth(154, 0, 800) == 0, "No rations means no consumption");
Check(Math.Abs(FoodModel.MealsPerMonth(154, 2, 800) - 800.0 / 33) < 1e-9, "Documented full-ration example");
for (int level = 1; level < 5; level++)
    Check(FoodModel.MealsPerMonth(154, level, 800) > FoodModel.MealsPerMonth(154, level - 1, 800), "Ration consumption monotonic");
var model = new FoodModel { Population = 154, Ration = 2, AppleFarmRate = 4, BakeryRate = 8, MillRate = 12, WheatFarmRate = 8, DairyRate = 4, HunterRate = 4, BreadPerFlour = 1, FlourPerWheat = 1 };
model.Enabled[3] = true; model.Stock[3] = 100; model.Compute();
Check(model.Hints[3]?.Icon == "UI-Buildings E005" && model.Hints[3]?.Count == 7, "Apple deficit estimates seven farms");
Check(Math.Abs(model.Months[2].Value - 4.125) < 1e-9, "Stock lifetime accounts for net deficit");
model.Production[3] = 30; model.Compute();
Check(model.Months[2] == null && model.Hints[3] == null && model.Sustained[3], "Sufficient production removes deficit hint");
model.Enabled[3] = false; model.Enabled[0] = true; model.Stock[0] = 50; model.FlourStock = 5; model.Compute();
Check(model.Hints[0]?.Icon == "UI-Buildings J003", "Flour available: bakery bottleneck");
model.FlourStock = 0; model.WheatStock = 5; model.Compute();
Check(model.Hints[0]?.Icon == "UI-Buildings J005", "Wheat available: mill bottleneck");
model.WheatStock = 0; model.Compute();
Check(model.Hints[0]?.Icon == "UI-Buildings E007", "No inputs: wheat farm bottleneck");
Console.WriteLine($"PASS: {checks} checks against production localization, layout display names and food model. No game rendering or native writes exercised.");
