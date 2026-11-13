// Runtime boundary fixtures: exercise production refresh logic without launching Unity.
namespace BepInEx.Logging { public class ManualLogSource { public void LogInfo(object x){} public void LogError(object x){} } }
namespace BepInEx.Configuration {
 public class ConfigEntry<T> { T value; public event EventHandler SettingChanged; public ConfigEntry(T v){value=v;} public T Value {get=>value; set {this.value=value;SettingChanged?.Invoke(this,EventArgs.Empty);}} }
 public class ConfigFile { public ConfigEntry<T> Bind<T>(string a,string b,T v,string d)=>new(v); }
}
namespace BepInEx {
 public class BepInDependency:Attribute { public BepInDependency(string a,string b){} }
 public class BepInPlugin:Attribute { public BepInPlugin(string a,string b,string c){} }
 public class BaseUnityPlugin { public Configuration.ConfigFile Config=new(); public Logging.ManualLogSource Logger=new(); }
}
namespace HarmonyLib {
 public class Harmony {public Harmony(string s){} public void Patch(object m,HarmonyMethod postfix){} }
 public class HarmonyMethod { public HarmonyMethod(Type t,string s){} }
 public static class AccessTools {public static object Method(Type t,string s)=>new();}
}
namespace UnityEngine { public static class Time {public static float unscaledTime=100;} }
namespace SHCDESE.API.LowLevel { }
namespace SHCDESE.API {
 public class GameXAMLManagerAPI {public static GameXAMLManagerAPI Instance=new(); public void RegisterLobbyModSettings(object p,string n,object s,string x,bool b){} }
}
namespace CrusaderDE {public class CrusaderLibrary {public static CrusaderLibrary Instance=new();public event Action<object> LibraryLoaded;public bool IsLibraryLoaded()=>true;} }
namespace Noesis {
 public class UIElement {public bool IsVisible=true;}
 public class FrameworkElement:UIElement {public object Parent;}
 public class Panel:FrameworkElement { }
}
public class KeyManager { }
public class HUD_Buildings:Noesis.FrameworkElement {
 public object RefBarracksPanel,RefMercPostPanel,RefBedouinStockadePanel,RefShowEngineersGuildPanel;
 public object FindName(string s)=>null;
}
public class HUD_Troops:Noesis.FrameworkElement {public object FindName(string s)=>null;}
public class HUD_Main:Noesis.FrameworkElement {public object FindName(string s)=>null;}
public class MainViewModel {public static MainViewModel Instance=new();public HUD_Buildings HUDBuildingPanel;public HUD_Troops HUDTroopPanel;public HUD_Main HUDmain;}
namespace UnitStats {
 public class UnitStatsSettings {public UnitStatsSettings(BepInEx.Configuration.ConfigEntry<bool> e){} }
 internal class StatsTable {
  Noesis.Panel host;public bool Visible;public int Shows;public bool IsAttached=>host!=null;
  public bool AttachTo(Noesis.Panel h){if(ReferenceEquals(h,host))return false;host=h;Visible=false;return true;}
  public void SetOffset(float r,float b){} public void Hide(){Visible=false;}
  public void Show(IReadOnlyList<UnitColumn> c){Visible=true;Shows++;}
 }
}
