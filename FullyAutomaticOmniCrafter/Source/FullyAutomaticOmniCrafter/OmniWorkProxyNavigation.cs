using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 全通代理网格的唯一导航规则。网格恒为 10，任意有效格之间均连通，
    /// 因此只需求合法终点；不借用普通居民的 Region、门或地形连通性。
    /// 工作范围、禁用、材料与目标预约仍由工作系统判断。
    /// </summary>
    internal static class OmniWorkProxyNavigation
    {
        internal static bool Walkable(Map map, IntVec3 cell)
        {
            return map != null && cell.IsValid && cell.InBounds(map) &&
                OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid != null &&
                map.pathing.Get(OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid).pathGrid.Walkable(cell);
        }

        private static bool Resolve(Pawn pawn, Map map, ref LocalTargetInfo target,
            ref PathEndMode mode, out CellRect occupied)
        {
            occupied = default(CellRect);
            if (pawn == null || !pawn.Spawned || pawn.Map != map || !target.IsValid) return false;
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                if (thing.Destroyed || thing.MapHeld != map) return false;
                if (!thing.Spawned)
                {
                    // 容器内物品只能从实际生成的持有者处访问，不能使用未生成物品的 Position。
                    thing = thing.SpawnedParentOrMe;
                    if (thing == null || !thing.Spawned || thing.Map != map) return false;
                    target = thing;
                }
            }
            if (!target.Cell.IsValid || !target.Cell.InBounds(map)) return false;
            if (mode == PathEndMode.InteractionCell)
            {
                if (!target.HasThing || !target.Thing.def.hasInteractionCell) return false;
                target = target.Thing.InteractionCell;
                mode = PathEndMode.OnCell;
                if (!target.Cell.IsValid || !target.Cell.InBounds(map)) return false;
            }
            // 代理的 ClosestTouch 不再读取普通网格的 Walkable 或门是否能打开。
            if (mode == PathEndMode.ClosestTouch) mode = PathEndMode.Touch;
            if (mode != PathEndMode.OnCell && mode != PathEndMode.Touch) return false;
            occupied = target.HasThing ? target.Thing.OccupiedRect() : CellRect.SingleCell(target.Cell);
            return true;
        }

        internal static bool IsAt(Pawn pawn, Map map, IntVec3 start,
            LocalTargetInfo target, PathEndMode mode)
        {
            if (!Walkable(map, start) || !Resolve(pawn, map, ref target, ref mode, out CellRect rect))
                return false;
            return (mode == PathEndMode.Touch ? rect.ExpandedBy(1) : rect).Contains(start);
        }

        internal static bool TryFindEnd(Pawn pawn, Map map, IntVec3 start,
            LocalTargetInfo target, PathEndMode mode, out IntVec3 end,
            bool adjacentOnly = false, bool reserveCell = false)
        {
            end = IntVec3.Invalid;
            if (!Walkable(map, start) || !Resolve(pawn, map, ref target, ref mode, out CellRect occupied))
                return false;
            CellRect candidates = mode == PathEndMode.Touch ? occupied.ExpandedBy(1) : occupied;
            candidates.ClipInsideMap(map);
            // 全通网格的矩形最近点是常数时间；仅预约冲突或要求移出蓝图时枚举边界。
            IntVec3 nearest = candidates.ClosestCellTo(start);
            if ((!adjacentOnly || !occupied.Contains(nearest)) && Walkable(map, nearest) &&
                (!reserveCell || map.reservationManager.CanReserve(pawn, nearest)))
            {
                end = nearest;
                return true;
            }
            int best = int.MaxValue;
            for (int z = candidates.minZ; z <= candidates.maxZ; z++)
                for (int x = candidates.minX; x <= candidates.maxX; x++)
                {
                    IntVec3 cell = new IntVec3(x, 0, z);
                    if (adjacentOnly && occupied.Contains(cell)) continue;
                    int distance = (cell - start).LengthHorizontalSquared;
                    if (distance >= best || !Walkable(map, cell) ||
                        reserveCell && !map.reservationManager.CanReserve(pawn, cell)) continue;
                    best = distance;
                    end = cell;
                }
            return end.IsValid;
        }

        // 反射只在类型初始化时解析；热路径不分配参数数组。
        private static readonly AccessTools.FieldRef<PawnPath, int> NodeIndex =
            AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");
        private static readonly AccessTools.FieldRef<PawnPath, float> TotalCost =
            AccessTools.FieldRefAccess<PawnPath, float>("totalCostInt");
        private static readonly AccessTools.FieldRef<PawnPath, bool> InUse =
            AccessTools.FieldRefAccess<PawnPath, bool>("inUse");

        internal static PawnPath FindPath(Pawn pawn, Map map, IntVec3 start,
            LocalTargetInfo target, PathEndMode mode)
        {
            if (!TryFindEnd(pawn, map, start, target, mode, out IntVec3 end)) return PawnPath.NotFound;
            // 兼容直接请求 PawnPath 的调用者：返回相邻节点序列，而不是只有两个远距离端点。
            PawnPath path = new PawnPath();
            IntVec3 cell = end;
            float cost = 0;
            while (cell != start)
            {
                if (!Walkable(map, cell)) { path.Dispose(); return PawnPath.NotFound; }
                path.AddNode(cell);
                int dx = Math.Sign(start.x - cell.x);
                int dz = Math.Sign(start.z - cell.z);
                cost += 10 + (dx != 0 && dz != 0 ? 18 : 13);
                cell = new IntVec3(cell.x + dx, 0, cell.z + dz);
            }
            path.AddNode(start);
            NodeIndex(path) = path.NodesReversed.Count - 1;
            TotalCost(path) = cost;
            InUse(path) = true;
            return path;
        }
    }

}
