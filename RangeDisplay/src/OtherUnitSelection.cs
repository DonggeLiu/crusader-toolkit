using HarmonyLib;
using SHCDESE.API;
using SHCDESE.Interop;
using System.Collections.Generic;
using UnityEngine;

namespace RangeDisplay
{
    /// <summary>
    /// 友方、敌方单位的“选中”。游戏本身不能选中非己方单位（点击没有任何反应），
    /// 所以按游戏选己方部队的手感在这里实现一份，只作用于非己方单位：
    /// 左键点选、左键框选、Shift 加选，左键点空地、右键、Esc 取消，单位死亡自动移除。
    /// 命中判定用单位身体精灵在屏幕上的实际矩形，与玩家看到的一致。
    /// </summary>
    internal sealed class OtherUnitSelection
    {
        // 按下与松开相距不到这么多像素算点击，否则算框选。
        private const float DragThresholdPixels = 6f;

        private static readonly AccessTools.FieldRef<GameMap, Dictionary<int, Chimp>> ChimpsRef =
            AccessTools.FieldRefAccess<GameMap, Dictionary<int, Chimp>>("chimps");

        private readonly List<int> selected = new List<int>();
        private readonly List<int> scratch = new List<int>();
        private Vector2 pressPosition;
        private bool pressedInGame;

        public IReadOnlyList<int> Selected => selected;

        /// <summary>选择在本帧发生变化时为 true（用于写诊断日志）。</summary>
        public bool ChangedThisFrame { get; private set; }

        public void Update(GameMap map)
        {
            ChangedThisFrame = false;
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;

            if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
                Clear();

            if (Input.GetMouseButtonDown(0))
            {
                pressPosition = Input.mousePosition;
                pressedInGame = players.IsCursorInGame();
            }

            if (Input.GetMouseButtonUp(0) && pressedInGame)
            {
                pressedInGame = false;
                bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                Vector2 release = Input.mousePosition;

                scratch.Clear();
                if (Vector2.Distance(pressPosition, release) < DragThresholdPixels)
                {
                    int hit = HitTest(map, players, release);
                    if (hit > 0)
                        scratch.Add(hit);
                }
                else
                {
                    BoxSelect(map, players, Rect.MinMaxRect(
                        Mathf.Min(pressPosition.x, release.x), Mathf.Min(pressPosition.y, release.y),
                        Mathf.Max(pressPosition.x, release.x), Mathf.Max(pressPosition.y, release.y)), scratch);
                }

                if (!additive)
                    selected.Clear();
                foreach (int id in scratch)
                {
                    if (!selected.Contains(id))
                        selected.Add(id);
                }

                ChangedThisFrame = true;
            }

            if (selected.RemoveAll(id => !IsAlive(id)) > 0)
                ChangedThisFrame = true;
        }

        public void Clear()
        {
            if (selected.Count == 0)
                return;

            selected.Clear();
            ChangedThisFrame = true;
        }

        /// <summary>单位身体精灵的屏幕矩形；取不到时返回 false。</summary>
        public static bool TryGetScreenRect(GameMap map, int unitId, out Rect rect)
        {
            rect = default;
            Camera camera = Camera.main;
            if (camera == null || !ChimpsRef(map).TryGetValue(unitId, out Chimp chimp) || chimp == null)
                return false;

            SpriteRenderer body = chimp.sprRenderer;
            if (body == null || body.sprite == null || !body.gameObject.activeInHierarchy)
                return false;

            Bounds bounds = body.bounds;
            Vector3 a = camera.WorldToScreenPoint(bounds.min);
            Vector3 b = camera.WorldToScreenPoint(bounds.max);
            rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return true;
        }

        /// <summary>游戏放置该单位的位置（脚下，getSpritePosVector 的结果）在屏幕上的坐标。</summary>
        public static bool TryGetScreenAnchor(GameMap map, int unitId, out Vector2 anchor)
        {
            anchor = default;
            Camera camera = Camera.main;
            if (camera == null || !ChimpsRef(map).TryGetValue(unitId, out Chimp chimp) || chimp == null || chimp.gameObject == null)
                return false;

            anchor = camera.WorldToScreenPoint(chimp.gameObject.transform.position);
            return true;
        }

        // 点击：取精灵矩形包含鼠标的非己方单位中，矩形中心离鼠标最近的一个。
        private static int HitTest(GameMap map, GamePlayerManagerAPI players, Vector2 point)
        {
            int localPlayer = players.GetLocalPlayerId();
            int best = 0;
            float bestDistance = float.MaxValue;

            foreach (KeyValuePair<int, Chimp> entry in ChimpsRef(map))
            {
                int id = entry.Key;
                if (!TryGetScreenRect(map, id, out Rect rect) || !rect.Contains(point))
                    continue;
                if (GameUnitManagerAPI.Instance.GetOwner(id) == localPlayer || !IsAlive(id))
                    continue;

                float distance = Vector2.Distance(rect.center, point);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = id;
                }
            }

            return best;
        }

        // 框选：精灵中心落在框内的非己方远程单位。
        private static void BoxSelect(GameMap map, GamePlayerManagerAPI players, Rect box, List<int> results)
        {
            int localPlayer = players.GetLocalPlayerId();
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;

            foreach (KeyValuePair<int, Chimp> entry in ChimpsRef(map))
            {
                int id = entry.Key;
                if (!TryGetScreenRect(map, id, out Rect rect) || !box.Contains(rect.center))
                    continue;
                if (units.GetOwner(id) == localPlayer || !IsAlive(id))
                    continue;
                if (RangedUnitTypes.Classify(units.GetType(id)) == RangedKind.None)
                    continue;

                results.Add(id);
            }
        }

        private static unsafe bool IsAlive(int unitId)
        {
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            GameUnit* unit = null;
            return units.IsValidId(unitId) && units.TryGetUnitById(unitId, out unit) &&
                   unit != null && unit->r_AliveState == SHCDESE.Interop.Enums.AliveState.IsAlive;
        }
    }
}
