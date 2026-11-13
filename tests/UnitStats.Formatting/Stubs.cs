// Offline fixtures only: these do not validate the game's live balance data or UI.
namespace SHCDESE.Interop.Enums { public enum eChimps { CHIMP_TYPE_ARAB_ASSASIN, CHIMP_TYPE_ARAB_BALLISTA, CHIMP_TYPE_ARAB_BOW, CHIMP_TYPE_ARAB_GRENADIER, CHIMP_TYPE_ARAB_HORSEMAN, CHIMP_TYPE_ARAB_SLAVE, CHIMP_TYPE_ARAB_SLINGER, CHIMP_TYPE_ARAB_SWORDSMAN, CHIMP_TYPE_ARCHER, CHIMP_TYPE_BALLISTA, CHIMP_TYPE_BATTERING_RAM, CHIMP_TYPE_BEDOUIN_AMBUSHER, CHIMP_TYPE_BEDOUIN_CAMEL_LANCER, CHIMP_TYPE_BEDOUIN_DEMOLISHER, CHIMP_TYPE_BEDOUIN_EUNUCH, CHIMP_TYPE_BEDOUIN_HEALER, CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL, CHIMP_TYPE_BEDOUIN_SAPPER, CHIMP_TYPE_BEDOUIN_SKIRMISHER, CHIMP_TYPE_CATAPULT, CHIMP_TYPE_ENGINEER, CHIMP_TYPE_KNIGHT, CHIMP_TYPE_LADDERMAN, CHIMP_TYPE_MACEMAN, CHIMP_TYPE_MANGONEL, CHIMP_TYPE_MONK, CHIMP_TYPE_PIKEMAN, CHIMP_TYPE_PORTABLE_SHIELD, CHIMP_TYPE_SIEGE_TOWER, CHIMP_TYPE_SPEARMAN, CHIMP_TYPE_SWORDSMAN, CHIMP_TYPE_TREBUCHET, CHIMP_TYPE_TUNNELER, CHIMP_TYPE_XBOWMAN } }
namespace SHCDESE.Interop { public enum ProjectileType { ArcherArrow,CrossbowBolt,SlingerStone,BedouinLance,Other } }
namespace CrusaderDE {
 public static class Enums { public enum eChimps { CHIMP_TYPE_ARAB_ASSASIN, CHIMP_TYPE_ARAB_BALLISTA, CHIMP_TYPE_ARAB_BOW, CHIMP_TYPE_ARAB_GRENADIER, CHIMP_TYPE_ARAB_HORSEMAN, CHIMP_TYPE_ARAB_SLAVE, CHIMP_TYPE_ARAB_SLINGER, CHIMP_TYPE_ARAB_SWORDSMAN, CHIMP_TYPE_ARCHER, CHIMP_TYPE_BALLISTA, CHIMP_TYPE_BATTERING_RAM, CHIMP_TYPE_BEDOUIN_AMBUSHER, CHIMP_TYPE_BEDOUIN_CAMEL_LANCER, CHIMP_TYPE_BEDOUIN_DEMOLISHER, CHIMP_TYPE_BEDOUIN_EUNUCH, CHIMP_TYPE_BEDOUIN_HEALER, CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL, CHIMP_TYPE_BEDOUIN_SAPPER, CHIMP_TYPE_BEDOUIN_SKIRMISHER, CHIMP_TYPE_CATAPULT, CHIMP_TYPE_ENGINEER, CHIMP_TYPE_KNIGHT, CHIMP_TYPE_LADDERMAN, CHIMP_TYPE_MACEMAN, CHIMP_TYPE_MANGONEL, CHIMP_TYPE_MONK, CHIMP_TYPE_PIKEMAN, CHIMP_TYPE_PORTABLE_SHIELD, CHIMP_TYPE_SIEGE_TOWER, CHIMP_TYPE_SPEARMAN, CHIMP_TYPE_SWORDSMAN, CHIMP_TYPE_TREBUCHET, CHIMP_TYPE_TUNNELER, CHIMP_TYPE_XBOWMAN } public enum eTextSections { TEXT_CHIMP_NAMES } }
 public class Translate { public static Translate Instance=new Translate(); public string lookUpText(Enums.eTextSections s,int id) => SHCDESE.API.GameAssetManagerAPI.Instance.CurrentLanguage.StartsWith("zh") ? "测试单位" : "Long Localized Unit Name"; }
}
namespace SHCDESE.API {
 using SHCDESE.Interop.Enums; using SHCDESE.Interop;
 public class GameAssetManagerAPI { public static GameAssetManagerAPI Instance=new GameAssetManagerAPI(); public string CurrentLanguage="en"; }
 public class GameUnitManagerAPI {
  public static GameUnitManagerAPI Instance=new GameUnitManagerAPI();
  public int GetDefaultHealth(eChimps t)=>1000;
  public int GetMeleeDamageFromTo(eChimps a,eChimps b)=>(int)b%3==0 ? 9 : 3;
  public int GetRangedArrowDamageTo(eChimps t)=>t==eChimps.CHIMP_TYPE_BALLISTA ? 0 : 300;
  public int GetRangedBoltDamageTo(eChimps t)=>400;
  public int GetRangedSlingerDamageTo(eChimps t)=>100;
  public int GetRangedJavelinDamageTo(eChimps t)=>200;
  public int GetUnitAttackRange(eChimps t)=>54;
  public int GetDefaultSpeed(eChimps t)=>(int)t%4+1;
  public int GetDefaultCavalryRunSpeedBonus(eChimps t)=>t==eChimps.CHIMP_TYPE_KNIGHT ? 2 : 1;
  public static ProjectileType GetDefaultAttackRangeProjectileType(eChimps t)=>t switch {
    eChimps.CHIMP_TYPE_XBOWMAN=>ProjectileType.CrossbowBolt,
    eChimps.CHIMP_TYPE_ARAB_SLINGER=>ProjectileType.SlingerStone,
    eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER=>ProjectileType.BedouinLance,
    eChimps.CHIMP_TYPE_CATAPULT=>ProjectileType.Other,
    _=>ProjectileType.ArcherArrow };
 }
}
