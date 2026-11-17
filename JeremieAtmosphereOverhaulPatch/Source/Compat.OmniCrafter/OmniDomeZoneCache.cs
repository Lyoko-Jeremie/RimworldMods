using System.Collections.Generic;
using FullyAutomaticOmniCrafter;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 穹顶覆盖格缓存：Map → 该地图所有「活动穹顶」覆盖格的扁平索引数组。
    ///
    /// 绝对隔离每模拟步都要按这组格写六通道，绝不能每步重新枚举建筑与矩形。
    /// 索引与 AO 引擎一致（x + z * Width），可直接下标访问 AO 数组。
    ///
    /// 线程约束：AO 的模拟在独立 Task 里跑，Task 期间主线程仍在动建筑列表，
    /// 所以枚举建筑只允许在主线程（EnsureBuilt），模拟线程只做 GetCells 的只读查表。
    /// </summary>
    internal static class OmniDomeZoneCache
    {
        private static readonly Dictionary<Map, int[]> Cache = new Dictionary<Map, int[]>();
        private static readonly HashSet<Map> Dirty = new HashSet<Map>();
        private static readonly object Sync = new object();

        /// <summary>标记某地图的穹顶集合已变化，下一次主线程取用时重建。</summary>
        public static void MarkDirty(Map map)
        {
            if (map == null)
            {
                return;
            }
            lock (Sync)
            {
                Dirty.Add(map);
            }
        }

        /// <summary>地图卸载时清缓存，避免 Map 对象被静态字典钉住。</summary>
        public static void Forget(Map map)
        {
            if (map == null)
            {
                return;
            }
            lock (Sync)
            {
                Cache.Remove(map);
                Dirty.Remove(map);
            }
        }

        /// <summary>
        /// 主线程调用：缓存缺失或已标脏时重建。返回该地图的格数组（可能为空）。
        ///
        /// 建筑与区域遍历放在锁外：模拟线程每步都会 GetCells，
        /// 若把一次 Area 全遍历压在锁内，会把模拟 Task 卡在主线程后面。
        /// </summary>
        public static int[] EnsureBuilt(Map map)
        {
            if (map == null)
            {
                return System.Array.Empty<int>();
            }

            lock (Sync)
            {
                int[] cached;
                if (!Dirty.Contains(map) && Cache.TryGetValue(map, out cached))
                {
                    return cached;
                }
            }

            int[] built = Build(map);

            lock (Sync)
            {
                Cache[map] = built;
                Dirty.Remove(map);
            }
            return built;
        }

        /// <summary>
        /// 模拟线程调用：只读缓存。未构建时返回空数组，绝不在 Task 里枚举建筑。
        /// </summary>
        public static int[] GetCells(Map map)
        {
            if (map == null)
            {
                return System.Array.Empty<int>();
            }
            lock (Sync)
            {
                int[] cells;
                if (Cache.TryGetValue(map, out cells))
                {
                    return cells;
                }
                return System.Array.Empty<int>();
            }
        }

        private static int[] Build(Map map)
        {
            List<Building> buildings = map.listerBuildings == null
                ? null
                : map.listerBuildings.allBuildingsColonist;
            if (buildings == null || buildings.Count == 0)
            {
                return System.Array.Empty<int>();
            }

            HashSet<int> set = new HashSet<int>();
            for (int i = 0; i < buildings.Count; i++)
            {
                Building building = buildings[i];

                // 穹顶：固定矩形。
                CompOmniForceFieldDome dome = building.GetComp<CompOmniForceFieldDome>();
                if (dome != null && dome.Active)
                {
                    CellRect rect = dome.OccupiedRect;
                    foreach (IntVec3 cell in rect)
                    {
                        if (cell.InBounds(map))
                        {
                            set.Add(map.cellIndices.CellToIndex(cell));
                        }
                    }
                }

                // 生物圈：玩家画的区域，形状任意，对 AO 来说就是异形穹顶。
                CompBiosphere biosphere = building.GetComp<CompBiosphere>();
                if (biosphere != null)
                {
                    Area area = biosphere.SelectedArea;
                    if (area != null)
                    {
                        foreach (IntVec3 cell in area.ActiveCells)
                        {
                            if (cell.InBounds(map))
                            {
                                set.Add(map.cellIndices.CellToIndex(cell));
                            }
                        }
                    }
                }
            }

            if (set.Count == 0)
            {
                return System.Array.Empty<int>();
            }

            int[] result = new int[set.Count];
            set.CopyTo(result);
            return result;
        }
    }
}
