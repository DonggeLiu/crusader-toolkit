using SHCDESE.API;
using SHCDESE.Detours;
using System;
using System.Reflection;
using UnityEngine;

namespace ResourceLayout
{
    /// <summary>
    /// 与游戏地图打交道：原版放置合法性校验、结构层、游戏格 ↔ 渲染格 ↔ 世界坐标。
    /// 游戏格（引擎坐标）是固定的，渲染格随视角旋转；原版提供游戏格 → 渲染格的正向换算，反向这里就近搜索。
    /// </summary>
    internal static class GameGrid
    {
        private static BulkBuildingDetours.c_game_player_build_placement_validator_delegate validator;

        /// <summary>
        /// 调用原版放置校验（绕过扩展器的钩子，不触发其它 mod 的校验事件）。坐标为占地左上角的游戏格，
        /// size 为边长，与原版放置时传的参数一致。只在主线程调用。
        /// </summary>
        public static bool CanPlace(LayoutType type, int x, int y) => Validate(type, x, y, out _);

        /// <summary>同 CanPlace，并返回校验函数的返回值。</summary>
        public static bool Validate(LayoutType type, int x, int y, out long ret)
        {
            if (validator == null)
            {
                FieldInfo field = typeof(BulkBuildingDetours).GetField("c_game_player_build_placement_validator_hook",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                object handle = field?.GetValue(null) ?? throw new InvalidOperationException("placement validator hook not found");
                validator = (BulkBuildingDetours.c_game_player_build_placement_validator_delegate)
                    handle.GetType().GetProperty("Original").GetValue(handle);
            }

            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            ret = validator(tiles.GetTileManager(), GamePlayerManagerAPI.Instance.GetLocalPlayerId(), x, y, type.Mapper, type.Size, 1);
            return !tiles.TileManager.IsPlacementBlocked;
        }

        public static bool HasStructure(int x, int y)
        {
            if (x < 0 || y < 0 || x >= 800 || y >= 800)
                return true;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            int id = tiles.GetTileId(x, y);
            var layer = tiles.GetStructureLayer();
            return id < 0 || id >= layer.Length || layer[id] != 0;
        }

        public static int Logic(int x, int y)
        {
            if (x < 0 || y < 0 || x >= 800 || y >= 800)
                return 0;
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            int id = tiles.GetTileId(x, y);
            var layer = tiles.GetLogicLayer();
            return id >= 0 && id < layer.Length ? layer[id] : 0;
        }

        private static int lastGx = 400, lastGy = 400;

        /// <summary>鼠标下的游戏格：从上次结果开始，按步长减半在渲染格距离上做贪心搜索。</summary>
        public static bool MouseTile(out int gx, out int gy)
        {
            gx = gy = 0;
            MainControls controls = MainControls.instance;
            GameMap map = GameMap.instance;
            if (controls == null || map == null)
                return false;

            // 规划时鼠标被当作在界面上，原版不再更新鼠标格，这里自己算。
            Vector3 mapVector = Vector3.zero;
            Vector3Int tileVector = Vector3Int.zero;
            int depth = -32767;
            map.CalcMapTileFromMousePos(Input.mousePosition, ref mapVector, ref tileVector, ref depth, useBuildingHeight: false);
            int tx = tileVector.x, ty = tileVector.y;

            int bx = lastGx, by = lastGy, best = Distance(map, bx, by, tx, ty);
            for (int step = 128; step >= 1; step /= 2)
            {
                bool improved = true;
                while (improved && best > 0)
                {
                    improved = false;
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = bx + dx * step, ny = by + dy * step;
                        if (nx < 0 || ny < 0 || nx >= 800 || ny >= 800)
                            continue;
                        int d = Distance(map, nx, ny, tx, ty);
                        if (d < best)
                        {
                            best = d;
                            bx = nx;
                            by = ny;
                            improved = true;
                        }
                    }
                }
            }

            lastGx = gx = bx;
            lastGy = gy = by;
            return best == 0;
        }

        private static int Distance(GameMap map, int gx, int gy, int tx, int ty)
        {
            map.mapGameTileToTilemapCoord(gx, gy, out int mx, out int my);
            return Math.Abs(mx - tx) + Math.Abs(my - ty);
        }

        /// <summary>游戏格中心的世界坐标（含地形高度），以及该格所在渲染格的 z。</summary>
        public static Vector3 World(int gx, int gy, out bool valid)
        {
            GameMap map = GameMap.instance;
            MainControls controls = MainControls.instance;
            map.mapGameTileToTilemapCoord(gx, gy, out int tx, out int ty);
            GameMapTile tile = map.getMapTile(tx, ty);
            valid = tile != null;
            Vector3 p = controls.getCellCentre(tx, ty);
            if (tile != null)
            {
                p.y += tile.height;
                p.z = map.getSpritePosVector(tx, ty).z;
            }
            return p;
        }

        /// <summary>游戏格坐标轴在世界平面上的方向（随视角旋转）。</summary>
        public static void Axes(int gx, int gy, out Vector3 ax, out Vector3 ay)
        {
            GameMap map = GameMap.instance;
            MainControls controls = MainControls.instance;
            map.mapGameTileToTilemapCoord(gx, gy, out int x0, out int y0);
            map.mapGameTileToTilemapCoord(gx + 1, gy, out int x1, out int y1);
            map.mapGameTileToTilemapCoord(gx, gy + 1, out int x2, out int y2);
            Vector3 o = controls.getCellCentre(x0, y0);
            ax = controls.getCellCentre(x1, y1) - o;
            ay = controls.getCellCentre(x2, y2) - o;
        }
    }
}
