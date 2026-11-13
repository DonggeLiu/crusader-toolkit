using UnitStats;
using SHCDESE.API;
using System.Text.RegularExpressions;
static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
int checks=0;
foreach(string lang in new[]{"en","de","fr","zh-CN","zh-TW"}) {
 GameAssetManagerAPI.Instance.CurrentLanguage=lang;
 bool zh=lang.StartsWith("zh");
 Check(UnitStatsText.IsChinese==zh,"Language fallback");
 Check(UnitStatsText.SettingsPath.EndsWith(zh ? "Settings.zh.xaml":"Settings.xaml"),"Settings path");
 Check(UnitStatsData.Build(StatsContext.None).Count==0,"No menu");
 foreach(var context in Enum.GetValues<StatsContext>().Where(x=>x!=StatsContext.None)) {
  var columns=UnitStatsData.Build(context);
  Check(columns.Count>0,"Missing context "+context);
  foreach(var col in columns) {
   Check(col.Values.Length==7,"Row count");
   Check(col.Values[0]=="10","Health conversion");
   Check(col.Values[3]=="-" || Regex.IsMatch(col.Values[3],@"^\d+$"),"Range must have no projectile prefix");
   if(!zh) Check(!Regex.IsMatch(col.Name+string.Join("",col.Values)+string.Join("",UnitStatsText.RowLabels)+UnitStatsText.Notes,@"[㐀-鿿]"),"Chinese leaked into fallback");
   checks++;
  }
 }
 var barracks=UnitStatsData.Build(StatsContext.Barracks);
 Check(barracks[3].Values[3]=="54","Crossbow numeric range");
 Check(barracks[0].Values[4]=="4" && barracks[0].Values[5]=="3","Hits round up");
 Check(barracks[0].Values[1]=="3","Melee modal damage");
 Check(barracks[3].Values[2]=="4","Bolt damage conversion");
 Check(barracks[6].Values[6].Contains(zh ? "冲":"+charge"),"Charge marker");
 var tower=UnitStatsData.Build(StatsContext.TowerEngines);
 Check(tower[1].Values[4]==(zh ? "免疫":"Immune"),"Immunity");
 Check(tower[1].Values[6]==(zh ? "固定":"Fixed"),"Stationary");
 Check(UnitStatsData.Build(StatsContext.SiegeEngines)[0].Values[2]=="-","Unavailable ranged damage");
}
Console.WriteLine($"PASS: {checks} unit/language cases across all 8 menus; numeric range, translated states, damage mode, rounding, unavailable data. Fixtures only; live game/UI not tested.");

var plugin = new UnitStatsPlugin();
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
void Invoke(string name) => typeof(UnitStatsPlugin).GetMethod(name, flags)!.Invoke(plugin, null);
T Field<T>(string name) => (T)typeof(UnitStatsPlugin).GetField(name,flags)!.GetValue(plugin)!;
var menu = new Noesis.UIElement();
MainViewModel.Instance.HUDBuildingPanel = new HUD_Buildings {RefBarracksPanel=menu, Parent=new Noesis.FrameworkElement {Parent=new Noesis.Panel()}};
GameAssetManagerAPI.Instance.CurrentLanguage="en";
Invoke("Awake"); Invoke("Tick");
var table=Field<StatsTable>("table");
Check(table.Visible,"Initial table");
var enabled=Field<BepInEx.Configuration.ConfigEntry<bool>>("showStats");
enabled.Value=false; Invoke("Tick"); Check(!table.Visible,"Disabling must hide table immediately");
enabled.Value=true; Invoke("Tick"); Check(table.Visible,"Re-enable must restore table");
int shown=table.Shows;Invoke("Tick");Check(table.Shows==shown,"No rebuild when unchanged");
GameAssetManagerAPI.Instance.CurrentLanguage="zh-CN";Invoke("Tick");Check(table.Shows==shown+1,"Rebuild after language change");
((Noesis.FrameworkElement)MainViewModel.Instance.HUDBuildingPanel.Parent).Parent=new Noesis.Panel();
Invoke("Tick");Check(table.Visible && table.Shows==shown+2,"Reattach and rebuild after HUD replacement");
menu.IsVisible=false;Invoke("Tick");Check(!table.Visible,"Hide outside supported menus");
Console.WriteLine("PASS: production plugin refresh logic: disable/re-enable, unchanged menu, language change, HUD replacement and closing menu. Rendering boundary is stubbed.");
