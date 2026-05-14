using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 幻影墙 XML 扩展参数。
    /// 在 ThingDef 的 &lt;modExtensions&gt; 节点中添加：
    /// <code>
    /// &lt;modExtensions&gt;
    ///   &lt;li Class="FullyAutomaticOmniCrafter.PhantomWallExtension"&gt;
    ///     &lt;allowPrisoner&gt;false&lt;/allowPrisoner&gt;
    ///     &lt;allowGuest&gt;false&lt;/allowGuest&gt;
    ///   &lt;/li&gt;
    /// &lt;/modExtensions&gt;
    /// </code>
    /// allowPrisoner（默认 true）：玩家的俘虏是否可穿越幻影墙。
    /// allowGuest（默认 true）：玩家的客人/访客是否可穿越幻影墙。
    /// targetTemperature（默认 21）：幻影墙尝试维持的房间温度。
    /// heatPushPower（默认 5）：每 Tick 调整温度的速度强度。
    /// </summary>
    public class PhantomWallExtension : DefModExtension
    {
        /// <summary>玩家的俘虏是否可以穿越幻影墙。默认 true。</summary>
        public bool allowPrisoner = true;
        /// <summary>玩家的客人/访客是否可以穿越幻影墙。默认 true。</summary>
        public bool allowGuest = true;
        /// <summary>中立或盟友派系的商队是否可以穿越幻影墙。默认 true。</summary>
        public bool allowTrader = true;
        /// <summary>幻影墙尝试维持的房间温度。默认 21。</summary>
        public float targetTemperature = 21f;
    }

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
    ///      — 修复：Harmony patch 将幻影墙格返回自定义 RegionType(18)，
    ///        连续幻影墙合并为一个多格区域，室内/室外各成独立房间，
    ///        不产生大量无用 Portal 单格房间。
    ///   B) 单格层 (VacuumUtility.EverInVacuum)：
    ///      — 检查单个格子是否暴露于真空。
    ///      — 需要 IsAirtight=true → ExchangeVacuum=false，才会返回 false
    ///        （即幻影墙格本身不存在真空，穿越的小人不受真空伤害）。
    ///      — 修复：覆写 IsAirtight / ExchangeVacuum。
    /// </summary>
    public class Building_OmniPhantomWall : Building, IPathFindCostProvider
    {
        // ── 无敌判定 ──────────────────────────────────────────────────

        /// <summary>
        /// 始终视为满血，不显示血条损耗。
        /// Thing.HitPoints 是 virtual property，覆写 getter 后游戏 UI 和逻辑
        /// 均读取此值，血条永远满格，彻底隐去"耐久"概念。
        /// setter 维持原始存储（不影响序列化等内部流程）。
        /// </summary>
        public override int HitPoints
        {
            get => MaxHitPoints;
            set { /* 忽略任何写入，维持无耐久状态 */ }
        }

        /// <summary>
        /// 吸收所有伤害，作为双重保险（防止绕过 HitPoints 直接调用 TakeDamage 的情况）。
        /// </summary>
        public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true; // 绝对无敌
        }

        /// <summary>
        /// VoidDeleter 等外部系统设置此标志后，
        /// 下一次 Destroy(Vanish) 调用将被放行（且自动清除标志）。
        /// </summary>
        [ThreadStatic]
        internal static bool _authorizedVanish = false;
        
        /// <summary>
        /// 合法销毁途径：
        ///   • DestroyMode.Deconstruct  — 玩家主动下达拆除指令（唯一正常移除方式）
        ///   • allowDestroyNonDestroyable — 系统级强制销毁（地图卸载等内部流程）
        ///
        /// 阻断途径（静默忽略）：
        ///   • Vanish / KillFinalize / KillFinalizeLeavingsOnly — 战斗/脚本销毁
        ///   • WillReplace — 玩家通过"在上面直接建造"
        ///   • Cancel / Refund / FailConstruction — 仅作用于蓝图/框架阶段，
        ///     对已建成建筑实际上不会触发，阻断无害，统一处理以防万一
        /// </summary>
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // 玩家主动拆除指令
            if (mode == DestroyMode.Deconstruct)
            {
                base.Destroy(mode);
                return;
            }
            // mod脚本拆除
            if (mode == DestroyMode.Vanish)
            {
                if (!_authorizedVanish)
                {
                    Log.Message($"[OmniPhantomWall] 阻止销毁：{this}，DestroyMode={mode}");
                    return;
                }
                _authorizedVanish = false;
                base.Destroy(mode);
                return;
            }
            // 在上面替换建造其他建筑导致的销毁
            if (mode == DestroyMode.WillReplace)
            {
                base.Destroy(mode);
                return;
            }
            // 系统级强制销毁（地图卸载、开发者 allowDestroyNonDestroyable）
            if (Thing.allowDestroyNonDestroyable)
            {
                base.Destroy(mode);
                return;
            }
            // 其余所有途径静默阻断
        }

        public void DestroyByScript(DestroyMode mode = DestroyMode.Vanish)
        {
            base.Destroy(mode);
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

            var ext = def.GetModExtension<PhantomWallExtension>();

            // 玩家的俘虏：由 XML 参数 allowPrisoner 决定（默认允许）
            if (pawn.IsPrisonerOfColony)
                return (ext == null || ext.allowPrisoner) ? (ushort)0 : ushort.MaxValue;

            // 玩家的客人/访客：由 XML 参数 allowGuest 决定（默认允许）
            // 判定逻辑：HostFaction 是玩家（访客、外交使团、寻求帮助的难民等）
            if (pawn.HostFaction == Faction.OfPlayer && !pawn.IsPrisoner)
                return (ext == null || ext.allowGuest) ? (ushort)0 : ushort.MaxValue;

            // 商队/访客：由 allowTrader 和 allowGuest 决定
            if (pawn.Faction != null && !pawn.Faction.HostileTo(Faction.OfPlayer))
            {
                var lord = pawn.GetLord();
                var lordJob = lord?.LordJob;

                // 1. 商队判定 (显式商人、商队任务、或是商队Kind)
                bool isTrader = pawn.trader != null 
                             || lordJob is LordJob_TradeWithColony 
                             || (pawn.kindDef != null && pawn.kindDef.trader);

                if (isTrader)
                    return (ext == null || ext.allowTrader) ? (ushort)0 : ushort.MaxValue;

                // 2. 访客/旅行者判定 (LordJob_VisitColony, LordJob_TravelAndExit 等)
                if (lordJob is LordJob_VisitColony || lordJob is LordJob_TravelAndExit)
                    return (ext == null || ext.allowGuest) ? (ushort)0 : ushort.MaxValue;
            }

            // 其余所有单位（敌人、野生动物、敌对派系）——视为墙壁（不可通行）
            return ushort.MaxValue;
        }

        public CellRect GetOccupiedRect() => this.OccupiedRect();

        // ── 自定义区域类型常量 ─────────────────────────────────────────────
        /// <summary>
        /// 幻影墙专用 RegionType，值 = 18 = 0b10010 = Normal(2) | 高位标志(0x10)。
        ///
        /// 关键特性（RegionTypeUtility 中无逐 bit 处理，均为精确值比较）：
        ///   • (18 & Set_Passable=0xE) = 2 ≠ 0 → Passable()=true：BFS 可达性可穿越 ✓
        ///   • 18 ≠ Portal(4) → IsOneCellRegion()=false：洪水填充，连续幻影墙 = 一个区域 ✓
        ///   • 18 ≠ Normal(2)/ImpassableFreeAirExchange(1)/Fence(8)：
        ///     ShouldBeInTheSameRoom 返回 false → 不与 Normal 房间合并 ✓
        ///   • 幻影墙区域 door=null → IsDoorway=false → 真空隔离走 ExchangeVacuum=false ✓
        /// </summary>
        internal const RegionType PhantomWallRegionType = (RegionType)18;
    }

    // ── 区域类型补丁：让幻影墙形成真正的房间边界（不产生无用单格房间）───────
    [HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
    public static class RegionTypeUtility_GetExpectedRegionType_Patch
    {
        public static void Postfix(IntVec3 c, Map map, ref RegionType __result)
        {
            // 只有当结果本来是 Normal（Standable 地块）才需要覆写
            if (__result == RegionType.Normal && c.GetEdifice(map) is Building_OmniPhantomWall)
                __result = Building_OmniPhantomWall.PhantomWallRegionType;
        }
    }

    // ── AffectsRegions 补丁：使 SpawnSetup/DeSpawn 正确触发区域 dirty ─────────
    /// <summary>
    /// ThingDef.AffectsRegions 默认只对 Impassable/IsDoor/IsFence 返回 true。
    /// 幻影墙 passability=Standable，导致 Thing.SpawnSetup 和 Thing.DeSpawn 均不会调用
    /// Notify_ThingAffectingRegionsSpawned / Notify_ThingAffectingRegionsDespawned，
    /// 区域永远不 dirty，GetExpectedRegionType 补丁无法生效，围墙无法形成独立房间。
    ///
    /// 此补丁让 OmniPhantomWall 的 ThingDef 返回 AffectsRegions=true，
    /// 使 Thing.SpawnSetup/DeSpawn 走正常的区域 dirty 流程。
    /// </summary>
    [HarmonyPatch(typeof(ThingDef), "AffectsRegions", MethodType.Getter)]
    public static class ThingDef_AffectsRegions_Patch
    {
        public static void Postfix(ThingDef __instance, ref bool __result)
        {
            if (__result) return; // already true
            // 支持子类，如果将来有继承自 Building_OmniPhantomWall 的子类，也会自动生效。
            if (typeof(Building_OmniPhantomWall).IsAssignableFrom(__instance.thingClass))
                __result = true;
        }
    }

    // ── 可达性补丁：敌方小人在 BFS 层无法穿越幻影墙区域 ─────────────────────
    /// <summary>
    /// 敌方小人不应在可达性 BFS（Region.Allows）层面穿越幻影墙区域。
    ///
    /// 否则敌人会"认为"能到达幻影墙内部，反复尝试寻路，产生无效 AI 行为。
    /// 友方小人（pawn=null 或 Faction=OfPlayer/HostFaction=OfPlayer）正常穿越。
    /// </summary>
    [HarmonyPatch(typeof(Region), nameof(Region.Allows))]
    public static class Region_Allows_PhantomWall_Patch
    {
        public static void Postfix(Region __instance, TraverseParms tp, ref bool __result)
        {
            if (__instance.type != Building_OmniPhantomWall.PhantomWallRegionType)
                return;

            if (tp.pawn == null)
                return; // 无特定小人时不限制（保持 true）

            // 友方可穿越
            if (tp.pawn.Faction == Faction.OfPlayer)
                return;

            // 从该区域的任意幻影墙 Thing 上读取扩展参数
            Building_OmniPhantomWall wall = __instance.AnyCell.GetEdifice(__instance.Map) as Building_OmniPhantomWall;
            var ext = wall?.def.GetModExtension<PhantomWallExtension>();

            // 俘虏：由 allowPrisoner 决定
            if (tp.pawn.IsPrisonerOfColony)
            {
                if (ext == null || ext.allowPrisoner) return;
                __result = false;
                return;
            }

            // 客人/访客：由 allowGuest 决定
            if (tp.pawn.HostFaction == Faction.OfPlayer && !tp.pawn.IsPrisoner)
            {
                if (ext == null || ext.allowGuest) return;
                __result = false;
                return;
            }

            // 商队/访客：由 allowTrader 和 allowGuest 决定
            if (tp.pawn.Faction != null && !tp.pawn.Faction.HostileTo(Faction.OfPlayer))
            {
                var lord = tp.pawn.GetLord();
                var lordJob = lord?.LordJob;

                // 1. 商队判定
                bool isTrader = tp.pawn.trader != null 
                             || lordJob is LordJob_TradeWithColony 
                             || (tp.pawn.kindDef != null && tp.pawn.kindDef.trader);

                if (isTrader)
                {
                    if (ext == null || ext.allowTrader) return;
                    __result = false;
                    return;
                }

                // 2. 访客/旅行者判定
                if (lordJob is LordJob_VisitColony || lordJob is LordJob_TravelAndExit)
                {
                    if (ext == null || ext.allowGuest) return;
                    __result = false;
                    return;
                }
            }

            // 其余所有单位（敌人、野生动物、中立）→ BFS 层面封路
            __result = false;
        }
    }

    // ── 房间合并补丁：让幻影墙形成独立的房间区域 ───────────────────────────
    /// <summary>
    /// 修正幻影墙无法产生房间的问题。
    ///
    /// RimWorld 原逻辑中，只有 Normal/ImpassableFreeAirExchange/Fence 才能属于一个 Room。
    /// 此补丁允许 PhantomWallRegionType 区域互相合并进入同一个 Room，
    /// 但阻止它们与 Normal 等其他区域合并，从而在物理上和逻辑上切断内外连接，形成独立房间。
    /// </summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater), "ShouldBeInTheSameRoom")]
    public static class RegionAndRoomUpdater_ShouldBeInTheSameRoom_Patch
    {
        public static bool Prefix(District a, District b, ref bool __result)
        {
            RegionType typeA = a.RegionType;
            RegionType typeB = b.RegionType;

            bool isPhantomA = typeA == Building_OmniPhantomWall.PhantomWallRegionType;
            bool isPhantomB = typeB == Building_OmniPhantomWall.PhantomWallRegionType;

            // 如果两个都是幻影墙，它们属于同一个房间（连成一圈）
            if (isPhantomA && isPhantomB)
            {
                __result = true;
                return false;
            }

            // 如果其中一个是幻影墙（另一个必然不是），它们绝不属于同一个房间（隔离内外）
            if (isPhantomA || isPhantomB)
            {
                __result = false;
                return false;
            }

            // 其余情况执行原版逻辑
            return true;
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

    // ── 性能优化：Harmony Patch 统一管理幻影墙区域温度 ───────────────────────────
    /// <summary>
    /// 当幻影墙数量巨大（如上万个）时，MapComponent 定时遍历房间虽然比逐个建筑 Tick 高效，
    /// 但仍存在不必要的开销。通过 Patch Room.Temperature 的 Getter，可以在不产生任何
    /// 定时计算开销的情况下，让幻影墙区域始终表现为恒温。
    /// </summary>
    [HarmonyPatch(typeof(Room), "get_Temperature")]
    public static class Room_Temperature_Getter_Patch
    {
        public static void Postfix(Room __instance, ref float __result)
        {
            // 只有当房间属于幻影墙区域时才拦截
            Region firstRegion = __instance.FirstRegion;
            if (firstRegion == null || firstRegion.type != Building_OmniPhantomWall.PhantomWallRegionType)
                return;

            // 防止在区域系统重建中途（invalid 或尚无 cells）访问 AnyCell，
            // 否则 Region.AnyCell → RegionGrid.DirectGrid 会触发递归重建，
            // 导致 "Could not register region" / "Couldn't find any cell in region" 错误。
            if (!firstRegion.valid)
                return;

            Map map = __instance.Map;
            if (map == null)
                return;

            // 仿 AnyCell 逻辑，但使用 GetRegionAt_NoRebuild_InvalidAllowed（不触发重建）
            // 而非 DirectGrid（会调用 TryRebuildDirtyRegionsAndRooms，导致递归重建崩溃）。
            // 同时避免 Cells（yield return 生成器，存在 IEnumerator 堆分配开销）。
            IntVec3 cell = IntVec3.Invalid;
            RegionGrid regionGrid = map.regionGrid;
            foreach (IntVec3 c in firstRegion.extentsClose)
            {
                if (regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(c) == firstRegion)
                {
                    cell = c;
                    break;
                }
            }
            if (!cell.IsValid)
                return;

            Building building = cell.GetEdifice(map);
            var ext = building?.def.GetModExtension<PhantomWallExtension>();
            __result = ext?.targetTemperature ?? 21f;
        }
    }

    [HarmonyPatch(typeof(Room), "set_Temperature")]
    public static class Room_Temperature_Setter_Patch
    {
        public static bool Prefix(Room __instance, ref float value)
        {
            // 如果是幻影墙房间，阻止任何温度修改，使其永远保持在 getter 返回的值
            Region firstRegion = __instance.FirstRegion;
            if (firstRegion != null && firstRegion.valid && firstRegion.type == Building_OmniPhantomWall.PhantomWallRegionType)
            {
                return false;
            }
            return true;
        }
    }
}