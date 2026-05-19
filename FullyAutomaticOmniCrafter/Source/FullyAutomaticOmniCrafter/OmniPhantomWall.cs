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
    /// </summary>
    public enum PhantomWallPassMode
    {
        /// <summary>1: 只有玩家、玩家动物、玩家机器人能通过。友方（俘虏、客人、商队）和敌方都不能通过。</summary>
        PlayerAndPets,
        /// <summary>2: 玩家、玩家动物、玩家机器人以及友方（俘虏、客人、商队）能通过。敌方不能通过。</summary>
        PlayerPetsAndAllies,
        /// <summary>3: 只有玩家、玩家机器人能通过。动物、友方、敌方均不能通过。</summary>
        OnlyPlayerNoPets,
        /// <summary>4: 只有玩家、玩家机器人能通过。动物、囚犯、友方、敌方均不能通过。</summary>
        OnlyPlayerNoPetsNotPrisoners,
        /// <summary>5: 只有玩家、玩家机器人能通过。动物、囚犯、实体、友方、敌方均不能通过。</summary>
        OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity,
    }

    /// <summary>
    /// 幻影墙扩展参数。
    /// 
    /// 示例：
    /// <code>
    /// <modExtensions>
    ///   <li Class="FullyAutomaticOmniCrafter.PhantomWallExtension">
    ///     <passMode>PlayerAndPets</passMode>
    ///     <targetTemperature>21</targetTemperature>
    ///   </li>
    /// </modExtensions>
    /// </code>
    /// </summary>
    public class PhantomWallExtension : DefModExtension
    {
        /// <summary>通行模式。默认为 PlayerPetsAndAllies。</summary>
        public PhantomWallPassMode passMode = PhantomWallPassMode.PlayerPetsAndAllies;

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
        /// <summary>
        /// 重写绘制颜色：保留材料的基础颜色，但强制将透明度改为 0.3
        /// </summary>
        public override Color DrawColor
        {
            get
            {
                var alpha = 0.5f;
                
                // base.DrawColor 会自动计算并返回当前材料(Stuff)的颜色，或者派系的颜色
                Color originalColor = base.DrawColor;
                
                // 返回 R, G, B 不变，强制将 Alpha 通道设为 0.3f (30% 不透明度)
                return new Color(originalColor.r, originalColor.g, originalColor.b, alpha);
            }
        }
        
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

        /// <summary>
        /// 检查小人是否可以穿越幻影墙。
        /// 1: PlayerAndPets - 只有玩家、玩家动物、机器人能通过。
        /// 2: PlayerPetsAndAllies - 玩家、玩家动物、机器人以及友方（俘虏、客人、商队）能通过。
        /// 3: OnlyPlayerNoPets - 只有玩家、机器人能通过。
        /// 4: OnlyPlayerNoPetsNotPrisoners - 只有玩家、机器人能通过，动物、囚犯也不能通过。
        /// 5: OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity - 只有玩家、机器人能通过，动物、囚犯、实体均不能通过。
        /// </summary>
        public static bool CanPawnPass(Pawn pawn, PhantomWallExtension ext)
        {
            if (pawn == null) return false;
            var mode = ext?.passMode ?? PhantomWallPassMode.PlayerPetsAndAllies;

            // Log.Message(
            //     $"[OmniPhantomWall] Check pass: pawn={pawn}, " +
            //     $"Faction={pawn.Faction}, HostFaction={pawn.HostFaction}, " +
            //     $"Lord={pawn.GetLord()}, Humanlike={pawn.RaceProps.Humanlike}, Animal={pawn.RaceProps.Animal}, " +
            //     $"HostileToPlayer={pawn.HostileTo(Faction.OfPlayer)}, Mode={mode}");

            // 【强制拦截判定】
            // 0. 囚犯判定 (针对模式 4, 5)
            // 必须在检查 Faction.OfPlayer 之前执行，因为被逮捕的本派系殖民者虽然 Faction 仍为玩家，但身份已变为囚犯。
            if (mode == PhantomWallPassMode.OnlyPlayerNoPetsNotPrisoners || mode == PhantomWallPassMode.OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity)
            {
                // 如果是囚犯（无论所属派系，包括被逮捕的发疯殖民者），则绝不允许通过
                if (pawn.IsPrisoner) return false;
            }

            // 0.1 实体判定 (针对模式 5 或所有实体拦截需求)
            // 如果是实体，且处于模式 5 或者是所有实体都需要拦截的情况
            if (pawn.RaceProps.IsAnomalyEntity)
            {
                // 如果模式是 OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity，实体绝不允许通过
                if (mode == PhantomWallPassMode.OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity) return false;
                
                // 此外，如果实体是对玩家敌对的，其他模式下也通常会被拦截（在后面处理），
                // 但为了满足“额外拦截所有的 AnomalyEntity，以便用于建造实体囚室”，
                // 我们确保在除了允许盟友的模式外，实体基本都不能通过。
                // 如果是模式 1, 3, 4，实体也不应该通过，因为它们不是玩家单位。
                // if (mode != PhantomWallPassMode.PlayerPetsAndAllies) return false;
            }

            // 1. 核心玩家单位判定
            // 检查是否为玩家派系的成员
            bool isPlayerFaction = pawn.Faction == Faction.OfPlayer;
            
            // 【任何时候玩家单位都可通过（除上述模式 4 的囚犯限制外）】
            if (isPlayerFaction)
            {
                // 如果是动物
                if (pawn.RaceProps.Animal)
                {
                    // 【额外限制，动物不可在 OnlyPlayerNoPets, OnlyPlayerNoPetsNotPrisoners 或 OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity 下通过】
                    return mode != PhantomWallPassMode.OnlyPlayerNoPets && 
                           mode != PhantomWallPassMode.OnlyPlayerNoPetsNotPrisoners &&
                           mode != PhantomWallPassMode.OnlyPlayerNoPetsNotPrisonersNotAnomalyEntity;
                }
                // 殖民者、机甲等人类或机器人始终可以通过
                return true;
            }

            // 【中立/友方单位判定】
            // 2. 友方/中立判定 (仅在模式 2: PlayerPetsAndAllies 下启用)
            if (mode == PhantomWallPassMode.PlayerPetsAndAllies)
            {
                // 如果是玩家托管的单位 (囚犯、受雇奴隶、受保护的客人)
                if (pawn.IsPrisonerOfColony || pawn.HostFaction == Faction.OfPlayer)
                {
                    return true;
                }

                // 非敌对的商队、访客、盟友、野生动物等
                if (!pawn.HostileTo(Faction.OfPlayer))
                {
                    // 具有派系的人员、具有领主（Lord）的群体单位，或非敌对的野生动物
                    // 额外放行智力达到“工具使用”等级的非人类 (ToolUser)，增加对 Mod 实体的兼容性
                    if (pawn.Faction != null
                        || pawn.GetLord() != null
                        || pawn.RaceProps.Humanlike
                        || pawn.RaceProps.Animal
                        || pawn.RaceProps.ToolUser)
                    {
                        return true;
                    }
                }
            }

            // 3. 其余情况 (敌对者、野生动物、在不对外开放模式下的访客/商队) 一律不允许通过
            return false;
        }

        // ── IPathFindCostProvider ─────────────────────────────────────
        /// <summary>
        /// 寻路代价：能通过返回 0，不能通过返回 ushort.MaxValue。
        /// </summary>
        public ushort PathFindCostFor(Pawn pawn)
        {
            // Log.Message($"[OmniPhantomWall] PathFindCostFor ENTER: wall={this}, pawn={pawn}, def={def?.defName}");

            var ext = def.GetModExtension<PhantomWallExtension>();
            return CanPawnPass(pawn, ext) ? (ushort)0 : ushort.MaxValue;
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

            // Log.Message(
            //     $"[OmniPhantomWall] Region.Allows PhantomWallRegion: pawn={tp.pawn}, " +
            //     $"regionType={__instance.type}, originalResult={__result}");

            if (tp.pawn == null)
            {
                // 如果没有提供 Pawn 信息（如区域检查），不执行进一步拦截，以免破坏系统功能
                return;
            }

            // 从该区域的任意幻影墙 Thing 上读取扩展参数
            Building_OmniPhantomWall wall = __instance.AnyCell.GetEdifice(__instance.Map) as Building_OmniPhantomWall;
            var ext = wall?.def.GetModExtension<PhantomWallExtension>();

            __result = Building_OmniPhantomWall.CanPawnPass(tp.pawn, ext);
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