using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 【临时诊断】定位 “Couldn't find any cell in region” 报错的成因。
    ///
    /// 只在 Region.AnyCell 确实在 extentsClose 内找不到归属格子时输出一条 Warning，
    /// 并附带：
    ///   • Region 自身状态（valid / type / links / 原始 cachedCellCount / extents）；
    ///   • 该区域内 grid 的真实分布（哪些 Region 的 id 各占多少格、多少格为 null）；
    ///   • 是否仍挂在 RCellFinder.regions / District / Room 里；
    ///   • 调用栈前若干帧。
    ///
    /// 全程使用 GetRegionAt_NoRebuild_InvalidAllowed 探测，绝不在诊断中触发区域重建。
    /// Prefix 返回 true，不改变原版行为。定位完成后可整文件删除
    /// （同时移除 .csproj 中的 Compile 项）。
    /// </summary>
    [HarmonyPatch(typeof(Region), nameof(Region.AnyCell), MethodType.Getter)]
    public static class Patch_RegionAnyCellDiag
    {
        // 最多打印条数，避免刷屏拖慢游戏
        private const int MaxLogs = 20;
        // 最多探测次数，超过后不再做任何额外遍历
        private const int MaxProbes = 5000;
        // 调用栈打印帧数
        private const int StackFrames = 8;

        private static int probeCount;
        private static int logCount;

        public static bool Prefix(Region __instance)
        {
            if (__instance == null)
                return true;

            int probes = Interlocked.Increment(ref probeCount);
            if (probes > MaxProbes || Volatile.Read(ref logCount) >= MaxLogs)
                return true;

            try
            {
                Map map = __instance.Map;
                if (map == null)
                    return true;

                RegionGrid grid = map.regionGrid;
                if (grid == null)
                    return true;

                // 用 NoRebuild 版本复现 AnyCell 的查找，避免诊断本身触发区域重建。
                foreach (IntVec3 c in __instance.extentsClose)
                {
                    if (grid.GetRegionAt_NoRebuild_InvalidAllowed(c) == __instance)
                        return true; // 原逻辑会成功，不打印
                }

                // 走到这里说明原逻辑即将 Log.Error，输出完整诊断。
                if (Interlocked.Increment(ref logCount) <= MaxLogs)
                    Log.Warning(BuildReport(__instance, map, grid));
            }
            catch (Exception ex)
            {
                // 诊断自身绝不能影响游戏，异常只吞掉并提示一次。
                Log.Warning("[RegionAnyCellDiag] 诊断自身异常: " + ex);
            }

            return true;
        }

        private static string BuildReport(Region region, Map map, RegionGrid grid)
        {
            var sb = new StringBuilder(512);
            sb.Append("[RegionAnyCellDiag] 找不到归属格子: region#").Append(region.id);
            sb.Append(" valid=").Append(region.valid);
            sb.Append(" type=").Append(region.type);
            sb.Append(" links=").Append(region.links != null ? region.links.Count : -1);
            sb.Append(" rawCachedCellCount=").Append(ReadRawCachedCellCount(region));
            sb.Append(" touchesMapEdge=").Append(region.touchesMapEdge);
            sb.Append(" extents=").Append(region.extentsClose);

            // grid 在 extentsClose 内的真实分布：谁占了这些格子
            int total = 0;
            int nullCells = 0;
            var byRegion = new Dictionary<int, int>();
            foreach (IntVec3 c in region.extentsClose)
            {
                total++;
                Region at = grid.GetRegionAt_NoRebuild_InvalidAllowed(c);
                if (at == null)
                {
                    nullCells++;
                    continue;
                }
                int n;
                byRegion.TryGetValue(at.id, out n);
                byRegion[at.id] = n + 1;
            }

            sb.Append("\n  grid: total=").Append(total)
              .Append(" null=").Append(nullCells)
              .Append(" byRegion=");
            if (byRegion.Count == 0)
            {
                sb.Append("<empty>");
            }
            else
            {
                bool first = true;
                foreach (KeyValuePair<int, int> kv in byRegion)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(kv.Key).Append(':').Append(kv.Value);
                }
            }

            // 是否仍被静态列表 / 结构引用持有
            sb.Append("\n  refs: inRCellFinderRegions=").Append(IsInRCellFinderRegions(region));
            District district = region.District;
            sb.Append(" district=").Append(district != null ? district.ID.ToString() : "null");
            sb.Append(" inDistrictRegions=").Append(district != null && district.Regions.Contains(region));
            Room room = region.Room;
            sb.Append(" room=").Append(room != null ? room.ID.ToString() : "null");
            sb.Append(" inRoomRegions=").Append(room != null && room.Regions.Contains(region));

            // 调用栈前若干帧，用于判断是 wander 还是其它调用方
            sb.Append("\n  stack:");
            try
            {
                var st = new StackTrace(2, false);
                int frames = Math.Min(st.FrameCount, StackFrames);
                for (int i = 0; i < frames; i++)
                {
                    System.Reflection.MethodBase method = st.GetFrame(i)?.GetMethod();
                    if (method == null)
                        continue;
                    sb.Append(" <- ")
                      .Append(method.DeclaringType != null ? method.DeclaringType.Name : "?")
                      .Append('.').Append(method.Name);
                }
            }
            catch
            {
                sb.Append(" <unavailable>");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 读取 Region 缓存的 CellCount 原始值；不调用 CellCount getter，避免副作用。
        /// -1 表示尚未计算过。
        /// </summary>
        private static int ReadRawCachedCellCount(Region region)
        {
            try
            {
                return Traverse.Create(region).Field("cachedCellCount").GetValue<int>();
            }
            catch
            {
                return int.MinValue;
            }
        }

        /// <summary>
        /// 检查该 Region 是否仍残留在 RCellFinder 的静态复用列表里。
        /// </summary>
        private static bool IsInRCellFinderRegions(Region region)
        {
            try
            {
                var list = Traverse.Create(typeof(RCellFinder))
                    .Field("regions")
                    .GetValue<List<Region>>();
                return list != null && list.Contains(region);
            }
            catch
            {
                return false;
            }
        }
    }
}
