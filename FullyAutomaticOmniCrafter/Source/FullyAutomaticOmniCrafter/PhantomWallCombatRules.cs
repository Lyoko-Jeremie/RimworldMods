using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 幻影墙战斗通行判定内核。
    ///
    /// 唯一穿墙许可：**射击者属于我方 且 目标不属于我方**。
    /// 目标是纯格子（没有具体 Thing）时，一律视为「非我方」（按需求：格子目标直接放行）。
    ///
    /// “我方”口径（用户确认）：
    ///   • Faction == Faction.OfPlayer         —— 殖民者 / 我方机械体 / 我方动物 / 我方建筑与炮塔
    ///   • HostFaction == Faction.OfPlayer     —— 我方雇佣（奴隶等）
    ///   • IsPrisonerOfColony                  —— 我方囚犯
    ///
    /// 注意：树木/野草属于 ThingCategory.Plant、物品属于 ThingCategory.Item，
    /// 而 ThingDef.CanHaveFaction 只对 Pawn / Building / 蓝图 / 框架返回 true，
    /// 因此它们的 Faction 恒为 null → 天然判定为「非我方」→ 放行。
    /// 地图生成的岩石与中立建筑虽然“可以有派系”，但生成时未设置 → Faction 同样为 null → 放行。
    /// </summary>
    internal static class PhantomWallCombatRules
    {
        /// <summary>该 Thing 是否为本 Mod 的幻影墙（一代或二代）。</summary>
        internal static bool IsPhantomWall(Thing thing)
            => thing is Building_OmniPhantomWall || thing is Building_OmniPhantomWall2;

        /// <summary>该格上的建筑是否为本 Mod 的幻影墙（O(1)，无分配）。</summary>
        internal static bool IsPhantomWallAt(IntVec3 cell, Map map)
            => map != null && IsPhantomWall(cell.GetEdifice(map));

        /// <summary>是否属于我方。</summary>
        internal static bool IsOurs(Thing thing)
        {
            if (thing == null)
                return false;

            // 殖民者 / 我方机械体 / 我方动物 / 我方建筑与炮塔
            if (thing.Faction == Faction.OfPlayer)
                return true;

            // 我方关押或雇佣的人（囚犯、奴隶等）。
            // 注意：HostFaction 只定义在 Pawn 上，Thing 没有这个成员。
            if (thing is Pawn pawn)
            {
                if (pawn.IsPrisonerOfColony)
                    return true;

                if (pawn.HostFaction == Faction.OfPlayer)
                    return true;
            }

            return false;
        }

        /// <summary>目标是否属于我方；纯格子目标（无 Thing）一律视为非我方。</summary>
        internal static bool TargetIsOurs(LocalTargetInfo target)
            => target.HasThing && IsOurs(target.Thing);

        /// <summary>
        /// 是否允许穿过幻影墙。
        /// shooter 为发射者（Pawn / 炮塔建筑 / 被操纵炮塔的实际射手），target 为射击目标。
        /// </summary>
        internal static bool MayPierce(Thing shooter, LocalTargetInfo target)
            => IsOurs(shooter) && !TargetIsOurs(target);
    }
}
