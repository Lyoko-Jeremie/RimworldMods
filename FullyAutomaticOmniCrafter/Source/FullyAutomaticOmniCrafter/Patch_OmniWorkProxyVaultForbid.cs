using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站代理的随身访问需要"够得到"超维存储终端：代理的工作范围
    /// （playerSettings.AreaRestrictionInPawnCurrentMap = Area_OmniWorkstation）会把工作站半径之外的
    /// 终端判为 forbidden，于是终端的存储格不会被选为搬运目的地，终端的视图副本也不会成为取料候选。
    ///
    /// 本补丁只对"代理 + 全局开关开启 + 该格属于超维存储终端"返回 false：
    ///   - 不放松玩家对其它区域/其它建筑的限制（非终端格完全走原版）；
    ///   - 禁止存入 / 冻结的终端仍被排除——候选筛选与 TryAdd 两处都检查 HaulDestinationEnabled，
    ///     这里不放宽该开关，也不改变落格物品的吸收判定。
    ///
    /// 性能：IntVec3.IsForbidden(Pawn) 是原版最热的判定之一。判序为
    /// kindDef 引用比较（非代理立即返回）→ 全局开关 bool → 本图是否有终端 → O(1) 格子查询，
    /// 因此非代理 pawn 只额外付一次引用比较。
    /// </summary>
    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden),
        new[] { typeof(IntVec3), typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyVaultForbid
    {
        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 c, Pawn pawn, ref bool __result)
        {
            if (!OmniWorkProxyVaultAccess.Applies(pawn, pawn?.Map))
            {
                return true;
            }
            if (!OmniWorkProxyVaultAccess.IsVaultCell(pawn.Map, c))
            {
                return true;
            }
            __result = false;
            return false;
        }
    }
}
