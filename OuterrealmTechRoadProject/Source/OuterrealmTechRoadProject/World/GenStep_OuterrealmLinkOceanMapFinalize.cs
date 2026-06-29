using System.Collections.Generic;
using OuterrealmTechRoadProject.DefOfs;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    /// <summary>
    /// 海上超维链路局部地图的收尾生成步骤。
    /// 原版海洋 Biome 没有局部地图自然地形表，<see cref="MapGenUtility.TerrainFrom"/> 找不到地形时会回退到沙地；
    /// 因此在 Roads 生成完之后，把海洋地图中非超维链路道路格改回深海，并把玩家起点固定到链路地面上。
    /// </summary>
    public class GenStep_OuterrealmLinkOceanMapFinalize : GenStep
    {
        private const string RoadCellsKey = "OuterrealmTechRoadProject_OuterrealmLinkOceanRoadCells";

        public override int SeedPart => 1962097591;

        /// <summary>
        /// RoadDefGenStep 在铺设海上超维链路时调用，用于记录哪些局部格属于道路带。
        /// 使用 MapGenerator 临时数据，不把生成过程状态写入存档，也避免长期静态集合带来的清理问题。
        /// </summary>
        public static void RegisterRoadCell(IntVec3 cell)
        {
            if (!cell.IsValid)
            {
                return;
            }

            HashSet<IntVec3> roadCells = GetOrCreateRoadCells();
            roadCells.Add(cell);
        }

        /// <summary>
        /// 在原版道路生成之后执行海洋地图修正。
        /// </summary>
        public override void Generate(Map map, GenStepParams parms)
        {
            if (map == null || !IsWaterCoveredOuterrealmLinkTile(map))
            {
                return;
            }

            HashSet<IntVec3> roadCells;
            if (!MapGenerator.TryGetVar(RoadCellsKey, out roadCells) || roadCells.Count == 0)
            {
                return;
            }

            IntVec3 bestStartCell = IntVec3.Invalid;
            float bestStartDistance = float.MaxValue;
            IntVec3 mapCenter = map.Center;

            using (map.pathing.DisableIncrementalScope())
            {
                foreach (IntVec3 cell in map.AllCells)
                {
                    if (roadCells.Contains(cell))
                    {
                        PrepareRoadCell(map, cell);
                        if (cell.Standable(map))
                        {
                            float distance = cell.DistanceToSquared(mapCenter);
                            if (distance < bestStartDistance)
                            {
                                bestStartDistance = distance;
                                bestStartCell = cell;
                            }
                        }

                        continue;
                    }

                    ConvertToDeepOcean(map, cell);
                }
            }

            if (bestStartCell.IsValid)
            {
                // FindPlayerStartSpot 会尊重已经设置好的 PlayerStartSpot，从而保证玩家进入海上营地时落在链路地面。
                MapGenerator.PlayerStartSpot = bestStartCell;
            }
        }

        /// <summary>
        /// 获取或创建当前地图生成过程中的道路格集合。
        /// </summary>
        private static HashSet<IntVec3> GetOrCreateRoadCells()
        {
            HashSet<IntVec3> roadCells;
            if (!MapGenerator.TryGetVar(RoadCellsKey, out roadCells))
            {
                roadCells = new HashSet<IntVec3>();
                MapGenerator.SetVar(RoadCellsKey, roadCells);
            }

            return roadCells;
        }

        /// <summary>
        /// 判断当前地图是否是拥有超维链路的水域世界 tile。
        /// 只有这种地图才需要把原版沙地回退修正成深海。
        /// </summary>
        private static bool IsWaterCoveredOuterrealmLinkTile(Map map)
        {
            if (!map.Tile.Valid)
            {
                return false;
            }

            SurfaceTile surfaceTile = Find.WorldGrid[map.Tile] as SurfaceTile;
            return surfaceTile != null &&
                   surfaceTile.WaterCovered &&
                   OuterrealmLinkUtility.TileHasOuterrealmLink(map.Tile, out _);
        }

        /// <summary>
        /// 道路格保持可站立状态，移除屋顶和阻挡物，避免玩家或物品生成到不可用格。
        /// </summary>
        private static void PrepareRoadCell(Map map, IntVec3 cell)
        {
            ClearThingsForOceanMap(map, cell);
            map.roofGrid.SetRoof(cell, null);
            map.fogGrid.Unfog(cell);

            // Roads 阶段可能已经设置过桥基，这里仍统一确认 top terrain 是超维链路地板。
            RoadDefGenStep_OuterrealmLinkPlace.PlaceOuterrealmLinkOnBridge(
                map,
                cell,
                RoadDefGenStep_OuterrealmLinkPlace.BestAvailableBridgeFoundation(null),
                OuterrealmTerrainDefOf.OuterrealmTech_OuterrealmLinkTerrain);
        }

        /// <summary>
        /// 非道路格统一恢复成深海，并清掉原版沙地地图上已经生成的自然物/废墟，形成真正的海面。
        /// </summary>
        private static void ConvertToDeepOcean(Map map, IntVec3 cell)
        {
            ClearThingsForOceanMap(map, cell);
            map.roofGrid.SetRoof(cell, null);
            if (map.terrainGrid.FoundationAt(cell) != null)
            {
                map.terrainGrid.RemoveFoundation(cell, false);
            }

            map.terrainGrid.SetTerrain(cell, MapGenUtility.DeepOceanWaterTerrainAt(cell, map));
        }

        /// <summary>
        /// 清理海面格上的物体。
        /// 这里在地图生成阶段运行，玩家和物品尚未进入地图；保守跳过 Pawn，避免误删后来由其他流程放入的角色。
        /// </summary>
        private static void ClearThingsForOceanMap(Map map, IntVec3 cell)
        {
            List<Thing> thingList = cell.GetThingList(map);
            for (int i = thingList.Count - 1; i >= 0; i--)
            {
                Thing thing = thingList[i];
                if (!thing.Destroyed && thing.def.category != ThingCategory.Pawn && thing.def.destroyable)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
