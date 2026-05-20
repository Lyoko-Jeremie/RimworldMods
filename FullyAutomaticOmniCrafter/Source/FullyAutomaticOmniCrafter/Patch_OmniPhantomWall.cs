using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace FullyAutomaticOmniCrafter
{

    // ── 区域类型补丁：让幻影墙形成真正的房间边界（不产生无用单格房间）───────
    [HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
    public static class RegionTypeUtility_GetExpectedRegionType_Patch
    {
        private static Dictionary<IntVec3, int> tempSignatures = new Dictionary<IntVec3, int>();

        public static void Postfix(IntVec3 c, Map map, ref RegionType __result)
        {
            // 只有当结果本来是 Normal（Standable 地块）才需要覆写
            if (__result == RegionType.Normal)
            {
                var edifice = c.GetEdifice(map);
                if (edifice is Building_OmniPhantomWall)
                {
                    __result = Building_OmniPhantomWall.PhantomWallRegionType;
                }
                else if (edifice is Building_OmniPhantomWall2 wall2)
                {
                    __result = Building_OmniPhantomWall2.PhantomWallRegionType;
                }
            }
        }

        /// <summary>
        /// 核心修复：通过在 FloodFill 过程中利用种子格子（root）的签名，
        /// 强行阻止不同签名的 OmniPhantomWall2 单元格合并到同一个 Region。
        /// </summary>
        [HarmonyPatch(typeof(RegionMaker), "FloodFillAndAddCells")]
        public static class RegionMaker_FloodFillAndAddCells_Patch
        {
            public static void Prefix(RegionMaker __instance, IntVec3 root, Map ___map, Region ___newReg)
            {
                if (___newReg.type == Building_OmniPhantomWall2.PhantomWallRegionType)
                {
                    var wall = root.GetEdifice(___map) as Building_OmniPhantomWall2;
                    if (wall != null)
                    {
                        tempSignatures[root] = wall.settings.GetSignature();
                    }
                }
            }

            public static void Postfix()
            {
                tempSignatures.Clear();
            }
        }

        [HarmonyPatch(typeof(Verse.FloodFiller), nameof(Verse.FloodFiller.FloodFill), new[] { typeof(IntVec3), typeof(Predicate<IntVec3>), typeof(Action<IntVec3>), typeof(int), typeof(bool), typeof(IEnumerable<IntVec3>) })]
        public static class FloodFiller_FloodFill_Patch
        {
            public static void Prefix(IntVec3 root, ref Predicate<IntVec3> passCheck, Map ___map)
            {
                if (tempSignatures.TryGetValue(root, out int rootSig))
                {
                    var oldCheck = passCheck;
                    passCheck = (c) =>
                    {
                        if (!oldCheck(c)) return false;
                        
                        // 如果当前格也是 OmniPhantomWall2，必须签名一致才能继续填充（合并到同一区域）
                        if (c.GetEdifice(___map) is Building_OmniPhantomWall2 otherWall)
                        {
                            return otherWall.settings.GetSignature() == rootSig;
                        }
                        
                        // OmniPhantomWall (v1) 签名视为 0，如果 root 是 v2 且签名不是 0，则不合并
                        if (c.GetEdifice(___map) is Building_OmniPhantomWall)
                        {
                            return rootSig == 0;
                        }

                        return true;
                    };
                }
            }
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
            // 支持子类，如果将来有继承自 Building_OmniPhantomWall2 的子类，也会自动生效。
            if (typeof(Building_OmniPhantomWall2).IsAssignableFrom(__instance.thingClass))
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
            // Building_OmniPhantomWall和Building_OmniPhantomWall2的PhantomWallRegionType值相同
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

            // 从该区域的任意幻影墙 Thing 上读取规则
            var cell = __instance.AnyCell;
            var building = cell.GetEdifice(__instance.Map);
            
            // 检查是否是OmniPhantomWall2
            if (building is Building_OmniPhantomWall2 wall2)
            {
                __result = wall2.CanPawnPassInstance(tp.pawn);
                return;
            }
            
            // 检查是否是OmniPhantomWall
            if (building is Building_OmniPhantomWall wall1)
            {
                var ext = wall1.def.GetModExtension<PhantomWallExtension>();
                __result = Building_OmniPhantomWall.CanPawnPass(tp.pawn, ext);
                return;
            }
            
            // 默认允许通过
            __result = true;
        }
    }

    // ── 房间合并补丁：让幻影墙形成独立的房间区域 ───────────────────────────
    /// <summary>
    /// 修正幻影墙无法产生房间的问题。
    ///
    /// RimWorld 原逻辑中，只有 Normal/ImpassableFreeAirExchange/Fence 才能属于一个 Room。
    /// 此补丁允许 PhantomWallRegionType 区域互相合并进入同一个 Room，
    /// 但阻止它们与 Normal 等其他区域合并，从而在物理上和逻辑上切断内外连接，形成独立房间。
    /// 
    /// 对于OmniPhantomWall2，需要比对通行规则签名，规则相同的墙体才能合并为同一房间。
    /// </summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater), "ShouldBeInTheSameRoom")]
    public static class RegionAndRoomUpdater_ShouldBeInTheSameRoom_Patch
    {
        // Building_OmniPhantomWall和Building_OmniPhantomWall2的PhantomWallRegionType值相同
        private static RegionType PhantomWallRegionType = Building_OmniPhantomWall.PhantomWallRegionType;
        
        public static bool Prefix(District a, District b, ref bool __result)
        {
            RegionType typeA = a.RegionType;
            RegionType typeB = b.RegionType;

            bool isPhantomA = typeA == PhantomWallRegionType;
            bool isPhantomB = typeB == PhantomWallRegionType;

            // 如果两个都是幻影墙，需要判定是否应该合并
            if (isPhantomA && isPhantomB)
            {
                // 获取两个区域的规则签名，只有规则相同才合并
                int sigA = GetRegionRuleSignature(a);
                int sigB = GetRegionRuleSignature(b);
                __result = (sigA == sigB);
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
        
        private static int GetRegionRuleSignature(District district)
        {
            // 从区域中获取任意一个幻影墙的规则签名
            if (district?.Regions == null || district.Regions.Count == 0)
                return 0;
            
            foreach (var cell in district.Regions.First().Cells)
            {
                // 检查是否是OmniPhantomWall2
                var wall2 = cell.GetEdifice(district.Map) as Building_OmniPhantomWall2;
                if (wall2 != null) 
                    return wall2.settings.GetSignature();
                
                // 检查是否是OmniPhantomWall（返回0表示兼容所有）
                var wall1 = cell.GetEdifice(district.Map) as Building_OmniPhantomWall;
                if (wall1 != null)
                    return 0;
            }
            return 0;
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
            if (!(thing is Building_OmniPhantomWall) && !(thing is Building_OmniPhantomWall2))
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
            // Building_OmniPhantomWall和Building_OmniPhantomWall2的PhantomWallRegionType值相同
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
            // Building_OmniPhantomWall和Building_OmniPhantomWall2的PhantomWallRegionType值相同
            Region firstRegion = __instance.FirstRegion;
            if (firstRegion != null && firstRegion.valid && firstRegion.type == Building_OmniPhantomWall.PhantomWallRegionType)
            {
                return false;
            }
            return true;
        }
    }
}