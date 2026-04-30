using System.Collections.Generic;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 跨帧脏标记：当蓝图或施工框在地图上首次出现时置位，
    /// 供 RealityWeaver / BlueprintRealizer 在 TickRare 时快速判断是否需要扫描。
    /// </summary>
    public static class BlueprintFrameTracker
    {
        // 用 Map 的 uniqueID 作 key，避免直接持有 Map 引用造成 GC 问题
        private static readonly HashSet<int> _dirtyMapIds = new HashSet<int>();

        /// <summary>标记指定地图"有待处理的蓝图/施工框"。</summary>
        public static void MarkDirty(Map map)
        {
            if (map != null)
                _dirtyMapIds.Add(map.uniqueID);
        }

        /// <summary>检查地图是否有待处理项。</summary>
        public static bool IsDirty(Map map) =>
            map != null && _dirtyMapIds.Contains(map.uniqueID);

        /// <summary>清除地图的脏标记（处理完毕后调用）。</summary>
        public static void ClearDirty(Map map)
        {
            if (map != null)
                _dirtyMapIds.Remove(map.uniqueID);
        }
    }
}

