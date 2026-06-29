using System.Collections.Generic;
using OuterrealmTechRoadProject.UI;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    public static class OuterrealmLinkPlanner
    {
        private static readonly List<PlanetTile> Route = new List<PlanetTile>();
        private static Buildings.Building_OuterrealmLinkProjector projector;

        public static bool IsPlanning
        {
            get
            {
                if (projector != null && !Find.WorldTargeter.IsTargeting)
                {
                    Clear();
                }

                return projector != null;
            }
        }

        public static void BeginPlanning(Buildings.Building_OuterrealmLinkProjector source)
        {
            if (source == null || !source.Spawned)
            {
                return;
            }

            projector = source;
            Route.Clear();
            Route.Add(source.Tile);
            CameraJumper.TryJump(source.Tile, CameraJumper.MovementMode.Cut);
            Find.WorldTargeter.BeginTargeting(
                ChoseWorldTarget,
                true,
                OuterrealmLinkTex.IconPlanOuterrealmLink,
                false,
                DrawRoute,
                ExtraLabel,
                CanSelectTarget,
                source.Tile,
                true);
        }

        public static void CancelPlanning()
        {
            if (Find.WorldTargeter.IsTargeting)
            {
                Find.WorldTargeter.StopTargeting();
            }

            Clear();
        }

        private static bool ChoseWorldTarget(GlobalTargetInfo target)
        {
            PlanetTile tile = target.Tile;
            if (!IsValidSurfaceTile(tile))
            {
                Messages.Message("OuterrealmTechRoadProject_InvalidWorldTile".Translate(), MessageTypeDefOf.RejectInput);
                return false;
            }

            int existingIndex = IndexOf(tile);
            if (existingIndex >= 0)
            {
                if (existingIndex == Route.Count - 1 && Route.Count > 1)
                {
                    ConfirmRoute();
                    return true;
                }

                TrimRouteAfter(existingIndex);
                return false;
            }

            PlanetTile lastTile = Route[Route.Count - 1];
            if (!Find.WorldGrid.IsNeighbor(lastTile, tile))
            {
                Messages.Message("OuterrealmTechRoadProject_MustChooseNeighborTile".Translate(), MessageTypeDefOf.RejectInput);
                return false;
            }

            Route.Add(tile);
            return false;
        }

        private static bool CanSelectTarget(GlobalTargetInfo target)
        {
            PlanetTile tile = target.Tile;
            if (!IsValidSurfaceTile(tile))
            {
                return false;
            }

            if (IndexOf(tile) >= 0)
            {
                return true;
            }

            return Route.Count > 0 && Find.WorldGrid.IsNeighbor(Route[Route.Count - 1], tile);
        }

        private static TaggedString ExtraLabel(GlobalTargetInfo target)
        {
            PlanetTile tile = target.Tile;
            if (!IsValidSurfaceTile(tile))
            {
                return "OuterrealmTechRoadProject_InvalidWorldTile".Translate();
            }

            int existingIndex = IndexOf(tile);
            if (existingIndex == Route.Count - 1 && Route.Count > 1)
            {
                return "OuterrealmTechRoadProject_ClickEndpointToConfirm".Translate(Route.Count - 1);
            }

            if (existingIndex >= 0)
            {
                return "OuterrealmTechRoadProject_ClickRouteNodeToTrim".Translate(existingIndex);
            }

            if (Route.Count > 0 && !Find.WorldGrid.IsNeighbor(Route[Route.Count - 1], tile))
            {
                return "OuterrealmTechRoadProject_MustChooseNeighborTile".Translate();
            }

            return "OuterrealmTechRoadProject_AddOuterrealmLinkNode".Translate(Route.Count);
        }

        private static void DrawRoute()
        {
            if (Route.Count < 2)
            {
                return;
            }

            WorldGrid grid = Find.WorldGrid;
            for (int i = 0; i < Route.Count - 1; i++)
            {
                Vector3 from = grid.GetTileCenter(Route[i]) + grid.GetTileCenter(Route[i]).normalized * 0.05f;
                Vector3 to = grid.GetTileCenter(Route[i + 1]) + grid.GetTileCenter(Route[i + 1]).normalized * 0.05f;
                GenDraw.DrawWorldLineBetween(from, to);
            }
        }

        private static void ConfirmRoute()
        {
            List<PlanetTile> routeCopy = new List<PlanetTile>(Route);
            string text = "OuterrealmTechRoadProject_ConfirmOuterrealmLinkText".Translate(routeCopy.Count, routeCopy.Count - 1);
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, delegate
            {
                BuildRoute(routeCopy);
            }));
            Clear();
        }

        private static void BuildRoute(List<PlanetTile> route)
        {
            if (route == null || route.Count < 2)
            {
                return;
            }

            for (int i = 0; i < route.Count - 1; i++)
            {
                if (!OuterrealmLinkUtility.TryOverlayOuterrealmLinkSegment(route[i], route[i + 1]))
                {
                    Messages.Message("OuterrealmTechRoadProject_BuildOuterrealmLinkFailed".Translate(i + 1), MessageTypeDefOf.RejectInput);
                    return;
                }
            }

            Find.LetterStack.ReceiveLetter(
                "OuterrealmTechRoadProject_OuterrealmLinkBuilt".Translate(),
                "OuterrealmTechRoadProject_OuterrealmLinkBuiltText".Translate(route.Count - 1),
                LetterDefOf.PositiveEvent,
                new GlobalTargetInfo(route[route.Count - 1]));
        }

        private static bool IsValidSurfaceTile(PlanetTile tile)
        {
            return tile.Valid && Find.WorldGrid.InBounds(tile) && Find.WorldGrid[tile] is SurfaceTile;
        }

        private static int IndexOf(PlanetTile tile)
        {
            for (int i = 0; i < Route.Count; i++)
            {
                if (Route[i] == tile)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void TrimRouteAfter(int index)
        {
            int removeCount = Route.Count - index - 1;
            if (removeCount > 0)
            {
                Route.RemoveRange(index + 1, removeCount);
            }
        }

        private static void Clear()
        {
            projector = null;
            Route.Clear();
        }
    }
}
