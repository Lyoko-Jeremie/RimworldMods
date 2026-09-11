using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 代理的需求永不消耗。
    ///
    /// 原版 <see cref="Pawn_NeedsTracker.NeedsTrackerTickInterval"/> 每 tick 被 Pawn 调用一次，
    /// 内部按 150 tick 的哈希间隔遍历 AllNeeds 并调用每个 <see cref="Need.NeedInterval"/>。
    /// 对代理直接跳过整段，效果是：
    ///   1. 需求值恒定在生成/读档时的初值——例如 RJW 的 Need_Sex 由 Need.SetInitialLevel() 得到 0.5，
    ///      正好落在它的 Neutral 区间，既不会滑到 Horny/Frustrated 这些行为阈值，
    ///      也不会产生 NeedInterval 携带的第三方副作用；
    ///   2. 不必再清空 needs 来"摆脱需求"：保留实例才能避免第三方读到 null
    ///      （RJW 的 xxx.need_sex 对没有 Need_Sex 的 Pawn 会直接判定为 Frustrated）。
    ///
    /// 这里刻意不写需求值：不同 Mod 的 Need 语义方向不同（有的越低越好）、MaxLevel 也未必是 1，
    /// 统一钉常量是错的；"不消耗"才是与第三方语义无关的做法。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval))]
    internal static class Patch_OmniWorkProxyNeeds_NoConsumption
    {
        // 高频路径（每 tick、每个已生成 Pawn 各一次）：先做最廉价的 kindDef 引用比较，
        // 非代理立即放行，不触碰 needs 列表。
        [HarmonyPrefix]
        private static bool Prefix(Pawn ___pawn)
        {
            return !OmniWorkProxyUtility.IsProxy(___pawn);
        }
    }
}
