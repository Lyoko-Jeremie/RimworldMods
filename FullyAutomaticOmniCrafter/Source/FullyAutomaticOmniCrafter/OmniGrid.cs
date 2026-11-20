using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 地图格子容器的尺寸工具。
    ///
    /// 本 mod 所有按格数组的容量统一取 map.Size 的格数，而不是 map.cellIndices.NumGridCells：
    /// 原版两者相等；RimLayers 会把 Size 改成含全部逻辑层与隔离带的物理尺寸（两者仍相等）；
    /// TrueLevels 则保持逻辑单层 Size、只在物理存储上变高（NumGridCells 更大）。
    /// 用 Size 做容量时，三种环境下 CellToIndex 给出的下标都落在数组内，也不需要探测任何第三方 mod。
    /// </summary>
    public static class OmniGrid
    {
        /// <summary>逻辑（即 map.Size）格数，也是本 mod 按格数组的容量。</summary>
        public static int CellCount(Map map)
        {
            return map == null ? 0 : map.Size.x * map.Size.z;
        }

        /// <summary>调用方记录的尺寸是否仍等于地图当前 Size。</summary>
        public static bool SameSize(IntVec3 cached, Map map)
        {
            return map != null
                && cached.x == map.Size.x
                && cached.y == map.Size.y
                && cached.z == map.Size.z;
        }

        /// <summary>取格下标；调用方必须先确认 cell.InBounds(map)。</summary>
        public static int IndexOf(Map map, IntVec3 cell)
        {
            return map.cellIndices.CellToIndex(cell);
        }

        /// <summary>取格下标；cell 不在 Size 内时返回 -1。</summary>
        public static int IndexOfInBounds(Map map, IntVec3 cell)
        {
            return map != null && cell.InBounds(map) ? map.cellIndices.CellToIndex(cell) : -1;
        }
    }
}
