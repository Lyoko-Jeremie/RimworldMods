using System;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 全图档的可达性旁路。
    ///
    /// Reachability.CanReach(IntVec3, LocalTargetInfo, PathEndMode, TraverseParms)
    /// 是 1.6/1.7 中所有可达性判定的唯一收口（GenClosest、Pawn.CanReach、
    /// CanReachNonLocal 最终都会落到它）。只在判定窗口内、且 traverseParams.pawn
    /// 就是当前判定 Pawn 时短路，避免影响任何真实 Pawn 的寻路。
    ///
    /// 档 B（PhysicalOnly）不拦截：判定 Pawn 的 Position 已被伪造为建筑格，
    /// 原版会按普通连通性从建筑位置真实 BFS，正好就是"仅物理连通"的语义。
    /// </summary>
    [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
        new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
    internal static class Patch_HaulJudge_Reachability_CanReach
    {
        [HarmonyPrefix]
        private static bool Prefix(LocalTargetInfo dest, TraverseParms traverseParams, ref bool __result)
        {
            if (!OmniHaulJudge.WholeMapMode) return true;
            if (!OmniHaulJudge.IsJudging(traverseParams.pawn)) return true;

            __result = OmniHaulJudge.IsValidReachTarget(dest);
            return false;
        }
    }
}
