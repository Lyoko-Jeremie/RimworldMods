using System.Collections.Generic;
using OuterrealmTechRoadProject.DefOfs;
using OuterrealmTechRoadProject.Defs;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    /// <summary>
    /// 世界地图超维链路的底层工具类。
    /// 这里集中处理 RoadLink 写入、查询和缓存刷新，避免 UI、建筑、patch 各自直接操作世界道路数据。
    /// </summary>
    public static class OuterrealmLinkUtility
    {
        /// <summary>
        /// 用本 Mod 固定的超维链路 RoadDef 连接两个相邻世界 tile。
        /// </summary>
        public static bool TryOverlayOuterrealmLinkSegment(PlanetTile from, PlanetTile to)
        {
            return TryOverlayRoadSegment(from, to, OuterrealmRoadDefOf.OuterrealmTech_OuterrealmLink);
        }

        /// <summary>
        /// 将指定 RoadDef 写入两个相邻世界 tile 之间。
        /// RimWorld 的道路不是独立对象，而是相邻 SurfaceTile 上各保存一条指向对方的 RoadLink。
        /// </summary>
        public static bool TryOverlayRoadSegment(PlanetTile from, PlanetTile to, RoadDef roadDef)
        {
            // 世界道路只支持同一星球层上的相邻 tile；跨层或无效 tile 不能写入 RoadLink。
            if (roadDef == null || !from.Valid || !to.Valid || from.Layer != to.Layer)
            {
                return false;
            }

            // 原版序列化、显示和寻路都假设道路边连接邻居 tile，不能跨多个 tile 直接连线。
            if (!Find.WorldGrid.IsNeighbor(from, to))
            {
                return false;
            }

            // 目前只处理地表 tile。轨道层或其他非 SurfaceTile 层没有 potentialRoads。
            SurfaceTile fromTile = Find.WorldGrid[from] as SurfaceTile;
            SurfaceTile toTile = Find.WorldGrid[to] as SurfaceTile;
            if (fromTile == null || toTile == null)
            {
                return false;
            }

            // potentialRoads 可能为空。原版世界生成只在需要道路时才创建列表。
            if (fromTile.potentialRoads == null)
            {
                fromTile.potentialRoads = new List<SurfaceTile.RoadLink>();
            }

            if (toTile.potentialRoads == null)
            {
                toTile.potentialRoads = new List<SurfaceTile.RoadLink>();
            }

            // 超维链路优先级很高，写入前先清掉同一边上更低或同等优先级的旧道路，避免重复边。
            RemoveLowerPriorityRoad(fromTile.potentialRoads, to, roadDef);
            RemoveLowerPriorityRoad(toTile.potentialRoads, from, roadDef);

            // RoadLink 必须双向写入；单向写入会导致显示、寻路或存档恢复不一致。
            AddRoadLinkIfMissing(fromTile.potentialRoads, to, roadDef);
            AddRoadLinkIfMissing(toTile.potentialRoads, from, roadDef);

            // 动态修改世界道路后，必须刷新世界绘制层和寻路缓存，否则车队仍可能按旧通行性走。
            MarkWorldRoadsDirtyAndRecalculate(from, to);
            return true;
        }

        /// <summary>
        /// 判断某个世界 tile 是否已经连接了任意一条超维链路。
        /// 这里直接读取 potentialRoads，避免受 SurfaceTile.Roads 的 allowRoads 限制影响。
        /// </summary>
        public static bool TileHasOuterrealmLink(PlanetTile tile, out DefModExtension_OuterrealmLinkRoad extension)
        {
            extension = null;
            if (!tile.Valid || !Find.WorldGrid.InBounds(tile))
            {
                return false;
            }

            SurfaceTile surfaceTile = Find.WorldGrid[tile] as SurfaceTile;
            if (surfaceTile == null || surfaceTile.potentialRoads == null)
            {
                return false;
            }

            // 使用普通 for 循环，避免未来把该函数用于寻路热路径时产生额外分配。
            for (int i = 0; i < surfaceTile.potentialRoads.Count; i++)
            {
                RoadDef road = surfaceTile.potentialRoads[i].road;
                if (IsOuterrealmLinkRoad(road))
                {
                    extension = road.GetModExtension<DefModExtension_OuterrealmLinkRoad>();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断 from -> to 这条世界边上是否存在超维链路。
        /// 后续做严格边级寻路时会使用这个函数，确保海洋/山脉只能沿道路边进出。
        /// </summary>
        public static bool HasOuterrealmLinkBetween(PlanetTile from, PlanetTile to)
        {
            if (!from.Valid || !to.Valid || !Find.WorldGrid.InBounds(from) || !Find.WorldGrid.InBounds(to))
            {
                return false;
            }

            SurfaceTile surfaceTile = Find.WorldGrid[from] as SurfaceTile;
            if (surfaceTile == null || surfaceTile.potentialRoads == null)
            {
                return false;
            }

            for (int i = 0; i < surfaceTile.potentialRoads.Count; i++)
            {
                SurfaceTile.RoadLink link = surfaceTile.potentialRoads[i];
                if (link.neighbor == to && IsOuterrealmLinkRoad(link.road))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断某个 tile 是否属于需要“沿超维链路边通行”的特殊地形。
        /// 当前版本暂未接入 WorldPathing transpiler，但规则先集中放在这里，便于后续启用。
        /// </summary>
        public static bool NeedsOuterrealmLinkEdge(PlanetTile tile)
        {
            if (!tile.Valid || !Find.WorldGrid.InBounds(tile))
            {
                return false;
            }

            SurfaceTile surfaceTile = Find.WorldGrid[tile] as SurfaceTile;
            return surfaceTile != null &&
                   (surfaceTile.WaterCovered ||
                    surfaceTile.PrimaryBiome.impassable ||
                    !surfaceTile.PrimaryBiome.allowRoads ||
                    surfaceTile.hilliness == Hilliness.Impassable);
        }

        /// <summary>
        /// 判断车队是否可以从 from 走到 to。
        /// 普通地形不限制；特殊地形必须有 from-to 之间的超维链路。
        /// </summary>
        public static bool CanTraverseWorldEdge(PlanetTile from, PlanetTile to)
        {
            if (!NeedsOuterrealmLinkEdge(from) && !NeedsOuterrealmLinkEdge(to))
            {
                return true;
            }

            return HasOuterrealmLinkBetween(from, to);
        }

        /// <summary>
        /// 判断 RoadDef 是否为本 Mod 的超维链路。
        /// 使用 DefModExtension 判断，而不是硬编码 defName，方便以后扩展同类道路。
        /// </summary>
        public static bool IsOuterrealmLinkRoad(RoadDef roadDef)
        {
            return roadDef != null && roadDef.GetModExtension<DefModExtension_OuterrealmLinkRoad>() != null;
        }

        /// <summary>
        /// 修改世界道路后的统一刷新逻辑。
        /// 包括世界地图道路图层、WorldPathGrid 感知移动难度，以及可达性缓存。
        /// </summary>
        public static void MarkWorldRoadsDirtyAndRecalculate(PlanetTile from, PlanetTile to)
        {
            try
            {
                // 让世界地图道路层重新生成 mesh，玩家才能立刻看到新道路。
                Find.World.renderer.SetDirty<WorldDrawLayer_Paths>(from.Layer);

                bool needsRecacheFrom;
                bool needsRecacheTo;
                // 重新计算两端 tile 的移动难度。不可通行 tile 可能因为超维链路变成可通行。
                Find.WorldPathGrid.RecalculatePerceivedMovementDifficultyAt(from, out needsRecacheFrom);
                Find.WorldPathGrid.RecalculatePerceivedMovementDifficultyAt(to, out needsRecacheTo);
                if (needsRecacheFrom || needsRecacheTo)
                {
                    // 如果通行性发生变化，原版的世界可达性缓存也要清理。
                    Find.WorldReachability.ClearCache();
                }
            }
            catch
            {
                Log.Warning("[OuterrealmTechRoadProject] Failed to refresh world road/pathing caches.");
            }
        }

        /// <summary>
        /// 移除同一邻居边上优先级不高于新道路的旧道路。
        /// 反向遍历可以在 RemoveAt 时不影响尚未检查的索引。
        /// </summary>
        private static void RemoveLowerPriorityRoad(List<SurfaceTile.RoadLink> roads, PlanetTile neighbor, RoadDef roadDef)
        {
            for (int i = roads.Count - 1; i >= 0; i--)
            {
                RoadDef existingRoad = roads[i].road;
                if (roads[i].neighbor == neighbor && existingRoad != null && existingRoad.priority <= roadDef.priority)
                {
                    roads.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 如果同一邻居边上还没有道路，则添加 RoadLink。
        /// 调用前通常已经清理过低优先级道路，因此这里保守地只防止重复添加。
        /// </summary>
        private static void AddRoadLinkIfMissing(List<SurfaceTile.RoadLink> roads, PlanetTile neighbor, RoadDef roadDef)
        {
            for (int i = 0; i < roads.Count; i++)
            {
                if (roads[i].neighbor == neighbor)
                {
                    return;
                }
            }

            roads.Add(new SurfaceTile.RoadLink
            {
                neighbor = neighbor,
                road = roadDef
            });
        }
    }
}
