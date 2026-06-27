using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    public class GenStep_OuterrealmTeleportStationLayout : GenStep
    {
        private static readonly List<Thing> SpawnedThings = new List<Thing>();

        public override int SeedPart => 744391701;

        public override void Generate(Map map, GenStepParams parms)
        {
            PrepareMap(map);

            IntVec3 center = map.Center;
            OuterrealmTeleportStationPrefabDef prefabDef = ChoosePrefab(map);
            Building portal = null;
            IntVec3 playerStart = IntVec3.Invalid;

            if (prefabDef != null && prefabDef.prefab != null)
            {
                TrySpawnPrefab(map, prefabDef, center, out portal, out playerStart);
            }

            if (portal == null)
            {
                portal = SpawnFallbackLayout(map, center);
                playerStart = center + new IntVec3(0, 0, -6);
            }

            if (!playerStart.IsValid || !playerStart.InBounds(map) || !playerStart.Standable(map))
            {
                playerStart = CellFinder.StandableCellNear(portal.Position, map, 8f);
            }

            MapGenerator.PlayerStartSpot = playerStart;
            MapGenerator.rootsToUnfog.Add(playerStart);
            MapGenerator.rootsToUnfog.Add(portal.Position);
            map.fogGrid.ClearAllFog();
        }

        private static void PrepareMap(Map map)
        {
            foreach (IntVec3 cell in map.AllCells)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.PackedDirt);
                map.roofGrid.SetRoof(cell, null);
                List<Thing> things = cell.GetThingList(map);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    Thing thing = things[i];
                    if (thing.def.destroyable)
                    {
                        thing.Destroy();
                    }
                }
            }
        }

        private static OuterrealmTeleportStationPrefabDef ChoosePrefab(Map map)
        {
            List<OuterrealmTeleportStationPrefabDef> prefabs = DefDatabase<OuterrealmTeleportStationPrefabDef>.AllDefsListForReading
                .Where(def => def.prefab != null && def.weight > 0f && def.AllowsBiome(map.Biome))
                .ToList();

            if (prefabs.Count == 0)
            {
                return null;
            }

            return prefabs.RandomElementByWeight(def => def.weight);
        }

        private static bool TrySpawnPrefab(
            Map map,
            OuterrealmTeleportStationPrefabDef prefabDef,
            IntVec3 center,
            out Building portal,
            out IntVec3 playerStart)
        {
            portal = null;
            playerStart = IntVec3.Invalid;
            Rot4 rot = PrefabUtility.ValidateRotation(prefabDef.prefab, Rot4.North);
            IntVec3 root = PrefabUtility.GetRoot(prefabDef.prefab, center, rot);
            CellRect occupied = new CellRect(root.x, root.z, prefabDef.prefab.size.x, prefabDef.prefab.size.z).ExpandedBy(3).ClipInsideMap(map);

            foreach (IntVec3 cell in occupied)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.MetalTile ?? TerrainDefOf.Concrete);
                GenSpawn.WipeExistingThings(cell, Rot4.North, ThingDefOf.Wall, map, DestroyMode.Vanish);
            }

            if (!PrefabUtility.CanSpawnPrefab(prefabDef.prefab, map, root, rot))
            {
                return false;
            }

            SpawnedThings.Clear();
            PrefabUtility.SpawnPrefab(prefabDef.prefab, map, root, rot, spawned: SpawnedThings);
            portal = SpawnedThings.OfType<Building>()
                .FirstOrDefault(thing => thing.def == OuterrealmDefOf.OuterrealmArchotechTeleportPortal);

            if (portal == null && prefabDef.portalOffset.IsValid)
            {
                IntVec3 portalPos = root + prefabDef.portalOffset.ToIntVec3;
                portal = portalPos.GetFirstBuilding(map);
            }

            if (prefabDef.playerStartOffset.IsValid)
            {
                playerStart = root + prefabDef.playerStartOffset.ToIntVec3;
            }

            if (portal != null)
            {
                EnsureRoadToEdge(map, portal.Position);
            }

            SpawnedThings.Clear();
            return portal != null;
        }

        private static Building SpawnFallbackLayout(Map map, IntVec3 center)
        {
            CellRect platform = CellRect.CenteredOn(center, 13, 13).ClipInsideMap(map);
            foreach (IntVec3 cell in platform)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.MetalTile ?? TerrainDefOf.Concrete);
            }

            EnsureRoadToEdge(map, center);
            return (Building)GenSpawn.Spawn(OuterrealmDefOf.OuterrealmArchotechTeleportPortal, center, map, Rot4.South);
        }

        private static void EnsureRoadToEdge(Map map, IntVec3 center)
        {
            CellRect mapRect = CellRect.WholeMap(map);
            for (int z = mapRect.minZ; z <= mapRect.maxZ; z++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    IntVec3 cell = new IntVec3(center.x + dx, 0, z);
                    if (cell.InBounds(map))
                    {
                        map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
                    }
                }
            }

            for (int x = mapRect.minX; x <= mapRect.maxX; x++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    IntVec3 cell = new IntVec3(x, 0, center.z + dz);
                    if (cell.InBounds(map))
                    {
                        map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
                    }
                }
            }
        }
    }
}
