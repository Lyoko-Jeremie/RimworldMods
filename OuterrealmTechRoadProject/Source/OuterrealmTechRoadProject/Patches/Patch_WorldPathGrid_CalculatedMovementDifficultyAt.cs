using HarmonyLib;
using OuterrealmTechRoadProject.World;
using RimWorld.Planet;

namespace OuterrealmTechRoadProject.Patches
{
    /// <summary>
    /// 修改世界地图 tile 的移动难度计算。
    /// 原版对海洋、不可通行山脉等 tile 直接返回 1000，车队永远不能进入；
    /// 这里在 tile 上存在超维链路时把它降为可通行值。
    /// </summary>
    [HarmonyPatch(typeof(WorldPathGrid), nameof(WorldPathGrid.CalculatedMovementDifficultyAt))]
    public static class Patch_WorldPathGrid_CalculatedMovementDifficultyAt
    {
        /// <summary>
        /// Postfix 只在原版已经算完后修正结果，尽量减少与其他 mod 的冲突。
        /// </summary>
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(ref float __result, PlanetTile tile)
        {
            bool needsOuterrealmLinkEdge = OuterrealmLinkUtility.NeedsOuterrealmLinkEdge(tile);
            // 普通可通行地形仍交给原版和道路倍率处理；只有特殊地形或不可通行结果需要本补丁兜底。
            if (__result < 1000f && !needsOuterrealmLinkEdge)
            {
                return;
            }

            // 只有拥有超维链路的特殊 tile 才被放行；没有道路的海洋/山脉仍保持不可通行。
            Defs.DefModExtension_OuterrealmLinkRoad extension;
            if (OuterrealmLinkUtility.TileHasOuterrealmLink(tile, out extension))
            {
                // 默认值 4 比 RotR 对不可通行 tile 常用的 12 更快，符合“快于闪耀高速”的设计。
                __result = extension != null ? extension.impassableTileMovementDifficulty : 4f;
            }
        }
    }
}
