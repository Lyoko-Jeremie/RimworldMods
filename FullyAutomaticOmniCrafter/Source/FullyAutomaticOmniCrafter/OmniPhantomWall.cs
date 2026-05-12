using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 幻影墙建筑类。
    ///
    /// 寻路机制说明（RimWorld 1.6 Unity Jobs 架构）：
    ///
    /// 寻路管线分三阶段：
    ///   ① PathGridJob.CostForCell  [BurstCompile, IJobParallelFor]
    ///      — 预计算与小人无关的基础代价网格 grid[]
    ///      — 此阶段由 ThingDef.passability 决定：需设为 Standable，
    ///        否则 grid[index] = 10000，对所有人封路。
    ///
    ///   ② PathGridDoorsBlockedJob.Execute  [普通托管 IJob]
    ///      — 遍历地图上所有 IPathFindCostProvider，调用 PathFindCostFor(pawn)
    ///      — 结果存入 providerCost[index]（每个小人独立计算）
    ///
    ///   ③ PathFinderJob.IndexCost(index)  [BurstCompile, IJob，A* 主循环]
    ///      — providerCost[index] == ushort.MaxValue → 返回 10000（不可通行）
    ///      — 否则: max(grid[index], providerCost[index])
    ///
    /// 因此本类实现 IPathFindCostProvider：
    ///   玩家方小人 → PathFindCostFor = 0      → A* 视为正常地面（可通行）
    ///   非玩家小人 → PathFindCostFor = ushort.MaxValue → A* 视为不可通行（10000）
    ///
    /// ThingDef XML 中必须设置：
    ///   &lt;passability&gt;Standable&lt;/passability&gt;
    ///   &lt;pathCost&gt;0&lt;/pathCost&gt;
    /// </summary>
    public class Building_OmniPhantomWall : Building, IPathFindCostProvider
    {
        // ── 无敌判定 ──────────────────────────────────────────────────
        public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true; // 绝对无敌
        }

        // ── IPathFindCostProvider ─────────────────────────────────────
        /// <summary>
        /// 玩家（含盟友/俘虏）返回 0；敌人/野生动物返回 ushort.MaxValue。
        /// PathFinderJob.IndexCost 会将 ushort.MaxValue 直接映射为 10000（不可通行）。
        /// </summary>
        public ushort PathFindCostFor(Pawn pawn)
        {
            if (pawn == null) return ushort.MaxValue;

            // 本方小人（殖民者、机甲、动物等）可以自由穿行
            if (pawn.Faction == Faction.OfPlayer)
                return 0;

            // 玩家的俘虏/客人也算友方
            if (pawn.HostFaction == Faction.OfPlayer)
                return 0;

            // 其余所有单位（敌人、野生动物、中立派系）——视为墙壁（不可通行）
            return ushort.MaxValue;
        }

        public CellRect GetOccupiedRect() => this.OccupiedRect();
    }

    // ── 子弹穿透补丁 ──────────────────────────────────────────────────
    /// <summary>
    /// 让玩家发射的子弹穿过幻影墙，敌人发射的子弹被挡住。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "CanHit")]
    public static class Projectile_CanHit_Patch
    {
        public static void Postfix(Projectile __instance, Thing thing, ref bool __result)
        {
            if (!(thing is Building_OmniPhantomWall))
                return;

            // Launcher 属性返回发射者 Thing（武器持有者/建筑炮台）
            Thing launcher = __instance.Launcher;
            if (launcher != null && launcher.Faction == Faction.OfPlayer)
            {
                // 玩家发射的子弹穿透幻影墙
                __result = false;
            }
            else
            {
                // 敌人发射的子弹被幻影墙拦截
                __result = true;
            }
        }
    }
}