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
        public static void Postfix(ref float __result, PlanetTile tile)
        {
            // 小于 1000 表示原版已经认为可通行，不需要干预普通地形的移动难度。
            if (__result < 1000f)
            {
                return;
            }

            // 只有拥有超维链路的不可通行 tile 才被放行；没有道路的海洋/山脉仍保持不可通行。
            Defs.DefModExtension_OuterrealmLinkRoad extension;
            if (OuterrealmLinkUtility.TileHasOuterrealmLink(tile, out extension))
            {
                // 默认值 4 比 RotR 对不可通行 tile 常用的 12 更快，符合“快于闪耀高速”的设计。
                __result = extension != null ? extension.impassableTileMovementDifficulty : 4f;
            }
        }
    }
}
