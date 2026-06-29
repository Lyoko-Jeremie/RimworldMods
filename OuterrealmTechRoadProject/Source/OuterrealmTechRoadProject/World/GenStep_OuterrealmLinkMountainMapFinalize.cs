using System.Collections.Generic;
using OuterrealmTechRoadProject.DefOfs;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    /// <summary>
    /// 不可通行山地超维链路局部地图的收尾生成步骤。
    /// 原版会把不可通行山地仍生成成普通山地地图；本步骤在道路生成后把地图重塑成整块岩体，
    /// 只保留超维链路经过的格子作为可站立隧道。
    /// </summary>
    public class GenStep_OuterrealmLinkMountainMapFinalize : GenStep
    {
        private const string RoadCellsKey = "OuterrealmTechRoadProject_OuterrealmLinkMountainRoadCells";

        public override int SeedPart => 1962097592;

        /// <summary>
        /// RoadDefGenStep 在不可通行山地铺设超维链路时调用，用于记录隧道道路格。
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
        /// 在原版道路生成之后执行不可通行山地地图修正。
        /// </summary>
        public override void Generate(Map map, GenStepParams parms)
        {
            if (map == null || !IsImpassableMountainOuterrealmLinkTile(map))
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
                        PrepareTunnelCell(map, cell);
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

                    FillWithNaturalRock(map, cell);
                }
            }

            if (bestStartCell.IsValid)
            {
                // 已经主动设置起点，FindPlayerStartSpot 会直接跳过，避免它因厚岩顶条件重新找点失败。
                MapGenerator.PlayerStartSpot = bestStartCell;
            }
        }

        /// <summary>
        /// 获取或创建当前地图生成过程中的山地隧道格集合。
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
        /// 判断当前地图是否是拥有超维链路的不可通行山地世界 tile。
        /// </summary>
        private static bool IsImpassableMountainOuterrealmLinkTile(Map map)
        {
            if (!map.Tile.Valid)
            {
                return false;
            }

            SurfaceTile surfaceTile = Find.WorldGrid[map.Tile] as SurfaceTile;
            return surfaceTile != null &&
                   surfaceTile.hilliness == Hilliness.Impassable &&
                   OuterrealmLinkUtility.TileHasOuterrealmLink(map.Tile, out _);
        }

        /// <summary>
        /// 隧道格保持空旷、可站立，并覆盖厚岩顶，表现为在山体内部开出的超维链路。
        /// </summary>
        private static void PrepareTunnelCell(Map map, IntVec3 cell)
        {
            ClearThingsForMountainMap(map, cell);
            if (map.terrainGrid.FoundationAt(cell) != null)
            {
                map.terrainGrid.RemoveFoundation(cell, false);
            }

            map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThick);
            map.fogGrid.Unfog(cell);
            map.terrainGrid.SetTerrain(cell, OuterrealmTerrainDefOf.OuterrealmTech_OuterrealmLinkTerrain);
        }

        /// <summary>
        /// 非道路格填充为天然岩墙，并设置厚岩顶。
        /// </summary>
        private static void FillWithNaturalRock(Map map, IntVec3 cell)
        {
            ClearThingsForMountainMap(map, cell);
            if (map.terrainGrid.FoundationAt(cell) != null)
            {
                map.terrainGrid.RemoveFoundation(cell, false);
            }

            ThingDef rockDef = GenStep_RocksFromGrid.RockDefAt(cell);
            TerrainDef naturalTerrain = rockDef != null && rockDef.building != null && rockDef.building.naturalTerrain != null
                ? rockDef.building.naturalTerrain
                : BaseGenUtility.RegionalRockTerrainDef(map.Tile, false);

            map.terrainGrid.SetTerrain(cell, naturalTerrain);
            map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThick);

            if (rockDef != null)
            {
                GenSpawn.Spawn(rockDef, cell, map);
            }
        }

        /// <summary>
        /// 清理指定格上的既有物体。
        /// 本步骤发生在局部地图生成阶段，保守跳过 Pawn，避免误删其他流程已经放入的角色。
        /// </summary>
        private static void ClearThingsForMountainMap(Map map, IntVec3 cell)
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
