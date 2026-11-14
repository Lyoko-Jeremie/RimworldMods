using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Patches
{
    /// <summary>
    /// 修复 Building.DeSpawn 后同格其它 edifice 从 edificeGrid 消失的问题。
    ///
    /// 原版 EdificeGrid 每格只存一个 Building：
    ///   - Building.SpawnSetup 的 Register 无条件覆盖整格（后注册者胜出）；
    ///   - Building.DeSpawn 的 DeRegister 无条件把本建筑占格写成 null，不判断该格当前
    ///     记录的是谁，也不回填同格其它建筑。
    ///
    /// 于是「墙 + 壁挂通风口」这类同格双建筑，在拆掉任意一个后，另一个仍留在
    /// thingGrid 里，却从 edificeGrid 消失。AO 的 obstacleMask 直接读 edificeGrid，
    /// 会把该格当成空气可自由通行的空地（AirCanMoveThrough(null) == true）。
    ///
    /// 本补丁只做一件事：DeSpawn 清空后，若某格 edificeGrid 为 null，而 thingGrid
    /// 里仍有 Spawned 的 edifice 覆盖该格，就把它写回。
    /// 不依赖 Replace Stuff，也没有同格双建筑时完全不写任何格子。
    /// </summary>
    public static class EdificeGridRestorePatch
    {
        /// <summary>
        /// Prefix 缓存的信息。
        ///
        /// 不能等到 Postfix 再取 __instance.Map：Thing.DeSpawn 会把 mapIndexOrState
        /// 置为 -1，之后 Thing.Map 返回 null（Verse/Thing.cs）。
        /// 缓存 CellRect 的边界而不是 IntVec3[]，是为了单格建筑也零分配。
        /// </summary>
        public struct RestoreState
        {
            public Map Map;
            public int MinX;
            public int MaxX;
            public int MinZ;
            public int MaxZ;
            public bool Valid;
        }

        public static bool Install(Harmony harmony)
        {
            if (harmony == null)
            {
                return false;
            }

            MethodInfo target = AccessTools.Method(
                typeof(Building),
                nameof(Building.DeSpawn),
                new[] { typeof(DestroyMode) });
            if (target == null)
            {
                return false;
            }

            harmony.Patch(
                target,
                new HarmonyMethod(typeof(EdificeGridRestorePatch), nameof(Prefix)),
                new HarmonyMethod(typeof(EdificeGridRestorePatch), nameof(Postfix)));
            return true;
        }

        public static void Prefix(Building __instance, out RestoreState __state)
        {
            __state = default;
            if (__instance == null || !__instance.Spawned)
            {
                // 非法调用（重复 DeSpawn）：原版会自己报错并返回，这里不介入。
                return;
            }

            // 非 edifice（Frame、蓝图、ReplaceFrame 等）不注册 edificeGrid，
            // 也没有需要回填的对象，直接跳过。
            if (!__instance.def.IsEdifice())
            {
                return;
            }

            Map map = __instance.Map;
            if (map == null)
            {
                return;
            }

            CellRect rect = __instance.OccupiedRect();
            __state.Map = map;
            __state.MinX = rect.minX;
            __state.MaxX = rect.maxX;
            __state.MinZ = rect.minZ;
            __state.MaxZ = rect.maxZ;
            __state.Valid = true;
        }

        public static void Postfix(Building __instance, RestoreState __state)
        {
            if (!__state.Valid)
            {
                return;
            }

            Map map = __state.Map;
            Building[] edificeGrid = map.edificeGrid.InnerArray;
            CellIndices cellIndices = map.cellIndices;

            for (int z = __state.MinZ; z <= __state.MaxZ; z++)
            {
                for (int x = __state.MinX; x <= __state.MaxX; x++)
                {
                    int index = cellIndices.CellToIndex(x, z);

                    // 已被其它逻辑注册（或同格本来就有别的 edifice）时不覆盖。
                    if (edificeGrid[index] != null)
                    {
                        continue;
                    }

                    IntVec3 cell = new IntVec3(x, 0, z);
                    Building restorable = FindRestorableEdifice(map, cell, __instance);
                    if (restorable != null)
                    {
                        // 只写单格：不能调 Register，否则会把 restorable 的整个矩形
                        // 再覆盖一遍，误伤它的其它占格。
                        edificeGrid[index] = restorable;
                    }
                }
            }
        }

        /// <summary>
        /// 在指定格子的 thingGrid 里找仍可代表该格的 edifice。
        ///
        /// 必须回 thingGrid 找，不能读 Prefix 时 edificeGrid 的原值：
        /// 例如 Vent_Over 注册时已经把同格的墙覆盖掉，原值只剩 Vent_Over 自己。
        /// 被拆建筑此刻已由 Thing.DeSpawn 的 thingGrid.Deregister 移除，
        /// 但仍用 __instance 做一次防御性排除。
        /// </summary>
        private static Building FindRestorableEdifice(Map map, IntVec3 cell, Building removed)
        {
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is Building building))
                {
                    continue;
                }

                if (building == removed || !building.Spawned || !building.def.IsEdifice())
                {
                    continue;
                }

                // 多格建筑的 Position 未必就在这一格，必须用占格矩形判断。
                if (building.OccupiedRect().Contains(cell))
                {
                    return building;
                }
            }

            return null;
        }
    }
}
