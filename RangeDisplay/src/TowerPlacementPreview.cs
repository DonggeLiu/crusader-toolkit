using SHCDESE.API;
using SHCDESE.Interop;

namespace RangeDisplay
{
    /// <summary>
    /// 玩家正在放置哪种塔楼、放在哪里。
    /// 放置状态读游戏自己的 MainControls（CurrentAction 5 = 放置建筑，CurrentSubAction = 建筑的 eMappers 编号），
    /// 位置读原生光标管理器的放置格，占地取游戏在预览格上挂的施工贴图（与 Build Grid 判断预览占地的方法一致）。
    /// </summary>
    internal static class TowerPlacementPreview
    {
        private const int PlacingBuildingAction = 5;

        // Enums.eMappers.MAPPER_TOWER1–5；与 eStructs.STRUCT_TOWER1–5（74–78）一一对应：
        // 1 瞭望塔、2 角塔、3 防御塔、4 方塔、5 圆塔（2026-10-07 地图编辑器里逐个放置核对，名称取游戏文本）。
        // 占地：角塔 4×4、防御塔 5×5、方塔与圆塔 6×6。
        private const int MapperTower1 = 110;
        private const int MapperTower5 = 114;

        // 预览占地最多几格；扫描范围留余量。
        private const int FootprintScan = 6;

        /// <param name="tower">1–5，对应 TOWER1–5。</param>
        public static unsafe bool TryGet(out int tower, out int minX, out int minY, out int maxX, out int maxY)
        {
            tower = 0;
            minX = minY = maxX = maxY = 0;

            MainControls controls = MainControls.instance;
            GameMap map = GameMap.instance;
            if (controls == null || map == null || controls.CurrentAction != PlacingBuildingAction)
                return false;

            int mapper = controls.CurrentSubAction;
            if (mapper < MapperTower1 || mapper > MapperTower5)
                return false;
            tower = mapper - MapperTower1 + 1;

            var cursor = GamePlayerManagerAPI.Instance.GetCursorManager();
            if (cursor.IsNull)
                return false;

            int ax = (int)cursor.Pointer->r_PlacingBuildingTileX;
            int ay = (int)cursor.Pointer->r_PlacingBuildingTileY;
            if (ax <= 0 || ay <= 0)
                return false;

            // 在放置格附近找带施工贴图的预览格，取它们的外接矩形。
            bool found = false;
            for (int x = ax - FootprintScan; x <= ax + FootprintScan; x++)
            {
                for (int y = ay - FootprintScan; y <= ay + FootprintScan; y++)
                {
                    if (!IsPreviewTile(map, x, y))
                        continue;

                    if (!found)
                    {
                        minX = maxX = x;
                        minY = maxY = y;
                        found = true;
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            // 预览格在放置格移动后的头一帧还没挂上贴图，这时沿用这种塔上次量到的相对占地，避免圈跳动。
            // 移动中的过渡帧也可能只挂了一部分预览格，比已知占地小时同样沿用上次的。
            int slot = tower - 1;
            if (found && measured[slot])
            {
                var known = offsets[slot];
                if (maxX - minX < known.MaxX - known.MinX || maxY - minY < known.MaxY - known.MinY)
                    found = false;
            }

            if (found)
            {
                offsets[slot] = (minX - ax, minY - ay, maxX - ax, maxY - ay);
                measured[slot] = true;
            }
            else if (measured[slot])
            {
                var o = offsets[slot];
                minX = ax + o.MinX;
                minY = ay + o.MinY;
                maxX = ax + o.MaxX;
                maxY = ay + o.MaxY;
            }
            else
            {
                minX = maxX = ax;
                minY = maxY = ay;
            }

            return true;
        }

        private static readonly (int MinX, int MinY, int MaxX, int MaxY)[] offsets = new (int, int, int, int)[5];
        private static readonly bool[] measured = new bool[5];

        /// <summary>
        /// 能架设器械（散射机、弩炮）的塔：方塔、圆塔（TOWER4、TOWER5）。
        /// 游戏说明：“大型石塔……这类塔楼可部署散射机或弩炮”；编辑器实测圆塔可放散射机、方塔可放弩炮，防御塔两种都不行。
        /// </summary>
        public static bool SupportsEngines(int tower) => tower == 4 || tower == 5;

        private static bool IsPreviewTile(GameMap map, int gameX, int gameY)
        {
            map.mapGameTileToTilemapCoord(gameX, gameY, out int x, out int y);
            GameMapTile tile = map.getMapTile(x, y);
            return tile != null && tile.constructionOrigImage != null;
        }
    }
}
