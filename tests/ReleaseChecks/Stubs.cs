namespace SHCDESE.API
{
    public sealed class GameAssetManagerAPI
    {
        public static GameAssetManagerAPI Instance { get; } = new();
        public string CurrentLanguage { get; set; }
    }
}
namespace SHCDESE.Interop
{
    public enum eMappers { MAPPER_WHEATFARM, MAPPER_HOPSFARM, MAPPER_APPLEFARM, MAPPER_CATTLEFARM, MAPPER_QUARRY, MAPPER_IRON_MINE, MAPPER_PITCH_WORKINGS }
}
namespace UnityEngine { public struct Color { public Color(float r, float g, float b) {} } }
namespace GranaryOverview
{
    internal static class FoodLedger
    {
        public const int Wheat = 9, Bread = 10, Cheese = 11, Meat = 12, Apples = 13, Flour = 16;
    }
}
