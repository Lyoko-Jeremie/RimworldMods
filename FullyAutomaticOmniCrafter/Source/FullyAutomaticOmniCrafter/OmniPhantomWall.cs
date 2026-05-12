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
    ///        （IPathFindCostProvider 仅在阶段②写入 providerCost，
    ///          阶段③取 max(grid, providerCost)，无法下调已为 10000 的 grid）
    ///
    ///   ② PathGridDoorsBlockedJob.Execute  [普通托管 IJob]
    ///      — 遍历地图上所有 IPathFindCostProvider，调用 PathFindCostFor(pawn)
    ///      — 结果存入 providerCost[index]（每个小人独立计算）
    ///
    ///   ③ PathFinderJob.IndexCost(index)  [BurstCompile, IJob，A* 主循环]
    ///      — providerCost[index] == ushort.MaxValue → 返回 10000（不可通行）
    ///      — 否则: max(grid[index], providerCost[index])
    ///
    /// 真空隔离机制：
    ///   RimWorld 的真空系统分两层：
    ///   A) 房间层 (Room/VacuumComponent)：
    ///      — 真空在房间之间流动，前提是两侧是「不同的房间」。
    ///      — 房间边界由 Region 系统决定：passability=Standable 会被
    ///        RegionTypeUtility.GetExpectedRegionType 判定为 RegionType.Normal，
    ///        不形成房间边界，两侧合并成同一房间——真空无法隔离。
    ///      — 修复：Harmony patch 将幻影墙格返回 RegionType.Portal，
    ///        使其成为单格 Portal 区域（与关闭的门完全一致），从而形成真正的
    ///        房间边界，两侧成为独立房间。
    ///   B) 单格层 (VacuumUtility.EverInVacuum)：
    ///      — 检查单个格子是否暴露于真空。
    ///      — 需要 IsAirtight=true → ExchangeVacuum=false，才会返回 false
    ///        （即幻影墙格本身不存在真空，穿越的小人不受真空伤害）。
    ///      — 修复：覆写 IsAirtight / ExchangeVacuum。
    /// </summary>
    public class Building_OmniPhantomWall : Building, IPathFindCostProvider
    {
        // ── 无敌判定 ──────────────────────────────────────────────────
        public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true; // 绝对无敌
        }

        // ── 真空隔离 ──────────────────────────────────────────────────
        /// <summary>
        /// 始终视为气密（不依赖材质）。
        /// Building.IsAirtight 原逻辑：isAirtight || (isStuffableAirtight && stuff.isAirtight)
        /// 这里直接返回 true，确保任何材质都能完全隔离真空。
        /// </summary>
        public override bool IsAirtight => true;

        /// <summary>
        /// 不与任何相邻房间交换真空。
        /// VacuumComponent.MergeRoomsIntoGroups 检查此属性来决定是否在房间组之间
        /// 建立真空交流通道。返回 false → 幻影墙彻底切断真空传播。
        /// </summary>
        public override bool ExchangeVacuum => false;

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

    // ── 区域类型补丁：让幻影墙形成真正的房间边界 ─────────────────────────
    /// <summary>
    /// 将幻影墙格的 RegionType 从 Normal 改为 Portal。
    ///
    /// 原因：
    ///   passability=Standable → WalkableByNormal()=true
    ///   → GetExpectedRegionType 返回 RegionType.Normal
    ///   → 不形成房间边界，两侧房间合并 → 真空无法隔离。
    ///
    /// Portal 类型与关闭的门相同：
    ///   • 单格独立区域 (IsOneCellRegion=true)，形成房间边界
    ///   • 包含在 RegionType.Set_Passable，寻路/可达性 BFS 仍可穿越
    ///   • VacuumComponent 将其视为 IsDoorway 房间，检查
    ///     GetDoor(map)—null for phantom wall—不建立真空交流通道
    /// </summary>
    [HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
    public static class RegionTypeUtility_GetExpectedRegionType_Patch
    {
        public static void Postfix(IntVec3 c, Map map, ref RegionType __result)
        {
            // 只有当结果本来是 Normal（Standable 地块）才需要覆写
            if (__result == RegionType.Normal && c.GetEdifice(map) is Building_OmniPhantomWall)
                __result = RegionType.Portal;
        }
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