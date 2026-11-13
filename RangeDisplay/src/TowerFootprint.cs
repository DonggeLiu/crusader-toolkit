using SHCDESE.API;
using SHCDESE.Interop;
using System.Collections.Generic;

namespace RangeDisplay
{
    /// <summary>
    /// 判断某格是否属于一座塔（TOWER1–5，与军医同塔治疗规则里的类型一致），并求出这座塔占的格子范围。
    /// 占地从该格出发、沿“属于同一建筑”的相邻格扩展，不依赖建筑结构体里的占地字段。
    /// </summary>
    internal static class TowerFootprint
    {
        // 最大的塔也只占几格；限制搜索范围，防止数据异常时扩到整张地图。
        private const int MaxReach = 8;

        private static readonly Queue<(int X, int Y)> open = new Queue<(int X, int Y)>();
        private static readonly HashSet<(int X, int Y)> seen = new HashSet<(int X, int Y)>();

        public static unsafe bool TryFind(int x, int y, out int buildingId, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = maxX = x;
            minY = maxY = y;
            buildingId = BuildingAt(GameTileManagerAPI.Instance, x, y);
            if (buildingId <= 0 ||
                !GameBuildingManagerAPI.Instance.TryGetBuildingById(buildingId, out GameBuilding* building) ||
                building == null || !IsTower(building->r_BuildingType))
                return false;

            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            open.Clear();
            seen.Clear();
            open.Enqueue((x, y));
            seen.Add((x, y));
            while (open.Count > 0)
            {
                (int cx, int cy) = open.Dequeue();
                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;

                Visit(tiles, buildingId, x, y, cx + 1, cy);
                Visit(tiles, buildingId, x, y, cx - 1, cy);
                Visit(tiles, buildingId, x, y, cx, cy + 1);
                Visit(tiles, buildingId, x, y, cx, cy - 1);
            }

            return true;
        }

        private static void Visit(GameTileManagerAPI tiles, int buildingId, int originX, int originY, int x, int y)
        {
            if (System.Math.Abs(x - originX) > MaxReach || System.Math.Abs(y - originY) > MaxReach)
                return;
            if (!seen.Add((x, y)))
                return;
            if (BuildingAt(tiles, x, y) == buildingId)
                open.Enqueue((x, y));
        }

        private static int BuildingAt(GameTileManagerAPI tiles, int x, int y)
        {
            if (tiles == null || !tiles.IsTileInsideMapBounds(x, y))
                return 0;

            int tileId = tiles.GetTileId(x, y);
            return tiles.IsValidTileId(tileId) ? tiles.GetTileBuildingId(tileId) : 0;
        }

        private static bool IsTower(eStructs type) =>
            type >= eStructs.STRUCT_TOWER1 && type <= eStructs.STRUCT_TOWER5;
    }
}
