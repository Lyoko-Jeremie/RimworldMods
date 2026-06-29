using OuterrealmTechRoadProject.DefOfs;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    public class RoadDefGenStep_OuterrealmLinkPlace : RoadDefGenStep_Bulldoze
    {
        public TerrainDef linkTerrain;
        public TerrainDef bridgeTerrain;

        public override void Place(
            Map map,
            IntVec3 position,
            TerrainDef rockDef,
            IntVec3 origin,
            GenStep_Roads.DistanceElement[,] distance)
        {
            if (!position.InBounds(map))
            {
                return;
            }

            ClearBlockingThings(map, position);
            TerrainDef terrain = position.GetTerrain(map);
            if (IsImpassableMountainMap(map))
            {
                ClearMountainCell(map, position, LinkTerrain);
                return;
            }

            if (terrain != null && terrain.IsWater)
            {
                map.roofGrid.SetRoof(position, null);
                map.fogGrid.Unfog(position);
                map.terrainGrid.SetTerrain(position, BridgeTerrain);
                return;
            }

            map.roofGrid.SetRoof(position, null);
            map.fogGrid.Unfog(position);
            map.terrainGrid.SetTerrain(position, LinkTerrain);
        }

        private TerrainDef LinkTerrain
        {
            get
            {
                return linkTerrain ?? OuterrealmTerrainDefOf.OuterrealmTech_OuterrealmLinkTerrain;
            }
        }

        private TerrainDef BridgeTerrain
        {
            get
            {
                if (bridgeTerrain != null)
                {
                    return bridgeTerrain;
                }

                TerrainDef concreteBridge = DefDatabase<TerrainDef>.GetNamedSilentFail("ConcreteBridge");
                if (concreteBridge != null)
                {
                    return concreteBridge;
                }

                TerrainDef heavyBridge = DefDatabase<TerrainDef>.GetNamedSilentFail("HeavyBridge");
                if (heavyBridge != null)
                {
                    return heavyBridge;
                }

                return TerrainDefOf.Bridge;
            }
        }

        private static bool IsImpassableMountainMap(Map map)
        {
            SurfaceTile surfaceTile = Find.WorldGrid[map.Tile] as SurfaceTile;
            return surfaceTile != null && surfaceTile.hilliness == Hilliness.Impassable;
        }

        private static void ClearMountainCell(Map map, IntVec3 position, TerrainDef terrain)
        {
            ClearBlockingThings(map, position);
            map.roofGrid.SetRoof(position, null);
            map.fogGrid.Unfog(position);
            map.terrainGrid.SetTerrain(position, terrain);
        }

        private static void ClearBlockingThings(Map map, IntVec3 position)
        {
            bool removed;
            do
            {
                removed = false;
                Thing edifice = position.GetEdifice(map);
                if (edifice != null)
                {
                    edifice.Destroy(DestroyMode.Vanish);
                    removed = true;
                }

                if (!removed)
                {
                    var thingList = position.GetThingList(map);
                    for (int i = thingList.Count - 1; i >= 0; i--)
                    {
                        Thing thing = thingList[i];
                        if (thing.def.destroyable && thing.def.passability == Traversability.Impassable)
                        {
                            thing.Destroy(DestroyMode.Vanish);
                            removed = true;
                            break;
                        }
                    }
                }
            }
            while (removed && position.InBounds(map));
        }
    }
}
