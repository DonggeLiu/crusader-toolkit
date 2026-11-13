using SHCDESE.Interop;
using UnityEngine;

namespace RangeDisplay
{
    internal enum RangedKind
    {
        None,
        Unit,
        Siege,
        Healer
    }

    /// <summary>虚线画攻击射程；点线画军医搜索范围；长虚线画塔上器械的预览射程。</summary>
    internal enum RingStyle
    {
        Dashed,
        Dotted,
        LongDash
    }

    /// <summary>放置塔楼时贴在预览圈上的图标，说明这个圈是谁的射程。</summary>
    internal enum RingIcon
    {
        None,
        Archer,
        Mangonel,
        Ballista
    }

    /// <summary>
    /// 地面上的一个圈：以某格为圆心、半径为若干格的圆；
    /// 或者一座建筑占地的外框（半径 0，画在塔顶，军医站在塔上时用）；
    /// 或者占地内各格同半径圆的外包络（半径 > 0，画在地面，放置塔楼时预览射程用）。坐标都是格坐标。
    /// </summary>
    internal readonly struct RangeRing
    {
        private RangeRing(int tileX, int tileY, float radius, int maxX, int maxY, bool footprint, Color color, RingStyle style,
            RingIcon icon = RingIcon.None)
        {
            Icon = icon;
            TileX = tileX;
            TileY = tileY;
            Radius = radius;
            MaxX = maxX;
            MaxY = maxY;
            IsFootprint = footprint;
            Color = color;
            Style = style;
        }

        public static RangeRing Circle(int tileX, int tileY, float radius, Color color, RingStyle style) =>
            new RangeRing(tileX, tileY, radius, tileX, tileY, false, color, style);

        /// <summary>覆盖 (minX, minY)–(maxX, maxY) 这些格（含两端）的外框。</summary>
        public static RangeRing Footprint(int minX, int minY, int maxX, int maxY, Color color, RingStyle style) =>
            new RangeRing(minX, minY, 0f, maxX, maxY, true, color, style);

        /// <summary>以 (minX, minY)–(maxX, maxY) 每一格为圆心、半径 radius 的圆的外包络（圆角矩形）。</summary>
        public static RangeRing Envelope(int minX, int minY, int maxX, int maxY, float radius, Color color, RingStyle style, RingIcon icon) =>
            new RangeRing(minX, minY, radius, maxX, maxY, true, color, style, icon);

        /// <summary>圆心格；外框时为左下角格。</summary>
        public int TileX { get; }
        public int TileY { get; }
        public float Radius { get; }
        public int MaxX { get; }
        public int MaxY { get; }
        public bool IsFootprint { get; }
        public Color Color { get; }
        public RingStyle Style { get; }
        public RingIcon Icon { get; }
    }

    internal static class RangedUnitTypes
    {
        // 远程部队与器械：与原生目标选择函数（CrusaderDE.dll RVA 0x18E9A0）里带射程表查询的单位类型一致。
        public static RangedKind Classify(eChimps type)
        {
            switch (type)
            {
                case eChimps.CHIMP_TYPE_ARCHER:
                case eChimps.CHIMP_TYPE_XBOWMAN:
                case eChimps.CHIMP_TYPE_ARAB_BOW:
                case eChimps.CHIMP_TYPE_ARAB_SLINGER:
                case eChimps.CHIMP_TYPE_ARAB_HORSEMAN:
                case eChimps.CHIMP_TYPE_ARAB_GRENADIER:
                case eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER:
                case eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER:
                case eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL:
                    return RangedKind.Unit;

                case eChimps.CHIMP_TYPE_CATAPULT:
                case eChimps.CHIMP_TYPE_TREBUCHET:
                case eChimps.CHIMP_TYPE_MANGONEL:
                case eChimps.CHIMP_TYPE_BALLISTA:
                case eChimps.CHIMP_TYPE_ARAB_BALLISTA:
                    return RangedKind.Siege;

                // 军医（兵种 79）；城里的药剂师是另一个兵种。
                case eChimps.CHIMP_TYPE_BEDOUIN_HEALER:
                    return RangedKind.Healer;

                default:
                    return RangedKind.None;
            }
        }
    }
}
