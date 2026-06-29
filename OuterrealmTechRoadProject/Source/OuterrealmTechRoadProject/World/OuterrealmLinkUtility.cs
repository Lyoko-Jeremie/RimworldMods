using System.Collections.Generic;
using OuterrealmTechRoadProject.DefOfs;
using OuterrealmTechRoadProject.Defs;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    public static class OuterrealmLinkUtility
    {
        public static bool TryOverlayOuterrealmLinkSegment(PlanetTile from, PlanetTile to)
        {
            return TryOverlayRoadSegment(from, to, OuterrealmRoadDefOf.OuterrealmTech_OuterrealmLink);
        }

        public static bool TryOverlayRoadSegment(PlanetTile from, PlanetTile to, RoadDef roadDef)
        {
            if (roadDef == null || !from.Valid || !to.Valid || from.Layer != to.Layer)
            {
                return false;
            }

            if (!Find.WorldGrid.IsNeighbor(from, to))
            {
                return false;
            }

            SurfaceTile fromTile = Find.WorldGrid[from] as SurfaceTile;
            SurfaceTile toTile = Find.WorldGrid[to] as SurfaceTile;
            if (fromTile == null || toTile == null)
            {
                return false;
            }

            if (fromTile.potentialRoads == null)
            {
                fromTile.potentialRoads = new List<SurfaceTile.RoadLink>();
            }

            if (toTile.potentialRoads == null)
            {
                toTile.potentialRoads = new List<SurfaceTile.RoadLink>();
            }

            RemoveLowerPriorityRoad(fromTile.potentialRoads, to, roadDef);
            RemoveLowerPriorityRoad(toTile.potentialRoads, from, roadDef);

            AddRoadLinkIfMissing(fromTile.potentialRoads, to, roadDef);
            AddRoadLinkIfMissing(toTile.potentialRoads, from, roadDef);
            MarkWorldRoadsDirtyAndRecalculate(from, to);
            return true;
        }

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

        public static bool CanTraverseWorldEdge(PlanetTile from, PlanetTile to)
        {
            if (!NeedsOuterrealmLinkEdge(from) && !NeedsOuterrealmLinkEdge(to))
            {
                return true;
            }

            return HasOuterrealmLinkBetween(from, to);
        }

        public static bool IsOuterrealmLinkRoad(RoadDef roadDef)
        {
            return roadDef != null && roadDef.GetModExtension<DefModExtension_OuterrealmLinkRoad>() != null;
        }

        public static void MarkWorldRoadsDirtyAndRecalculate(PlanetTile from, PlanetTile to)
        {
            try
            {
                Find.World.renderer.SetDirty<WorldDrawLayer_Paths>(from.Layer);

                bool needsRecacheFrom;
                bool needsRecacheTo;
                Find.WorldPathGrid.RecalculatePerceivedMovementDifficultyAt(from, out needsRecacheFrom);
                Find.WorldPathGrid.RecalculatePerceivedMovementDifficultyAt(to, out needsRecacheTo);
                if (needsRecacheFrom || needsRecacheTo)
                {
                    Find.WorldReachability.ClearCache();
                }
            }
            catch
            {
                Log.Warning("[OuterrealmTechRoadProject] Failed to refresh world road/pathing caches.");
            }
        }

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
