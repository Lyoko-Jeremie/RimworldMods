using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    public class Building_OuterrealmArchotechTeleportPortal : Building
    {
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "OATS_SelectTeleportDestination".Translate(),
                defaultDesc = "OATS_CommandTeleportToStationDesc".Translate(),
                icon = OuterrealmTeleportStationTex.Teleport,
                action = ShowTeleportDestinationMenu
            };

            yield return new Command_Action
            {
                defaultLabel = "OATS_CommandAddTeleportStation".Translate(),
                defaultDesc = "OATS_CommandAddTeleportStationDesc".Translate(),
                icon = OuterrealmTeleportStationTex.AddStation,
                action = ShowAddStationMenu
            };
        }

        private void ShowTeleportDestinationMenu()
        {
            OuterrealmArchotechTeleportStationWorldObject origin = Map?.Parent as OuterrealmArchotechTeleportStationWorldObject;
            List<OuterrealmArchotechTeleportStationWorldObject> destinations =
                OuterrealmTeleportNetworkUtility.GetDestinationStations(origin);

            if (destinations.Count == 0)
            {
                Messages.Message("OATS_CannotTeleportNoDestinations".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (OuterrealmArchotechTeleportStationWorldObject destination in destinations)
            {
                OuterrealmArchotechTeleportStationWorldObject localDestination = destination;
                options.Add(new FloatMenuOption(localDestination.LabelCap, () => TeleportMapPawns(localDestination)));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void TeleportMapPawns(OuterrealmArchotechTeleportStationWorldObject destination)
        {
            List<Pawn> pawns = Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                .Where(pawn => !pawn.Downed && !pawn.Dead)
                .ToList();

            if (pawns.Count == 0)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            PlanetTile originTile = Map.Tile;
            Caravan caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(
                pawns,
                Faction.OfPlayer,
                originTile,
                originTile,
                PlanetTile.Invalid,
                false);

            if (caravan == null)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, destination);
            Messages.Message(
                "OATS_MessagePawnsTeleportedFromMap".Translate(destination.LabelCap),
                new LookTargets(caravan, destination),
                MessageTypeDefOf.TaskCompletion);
        }

        private void ShowAddStationMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption("OATS_AddTeleportStationRandom".Translate(), AddRandomStation),
                new FloatMenuOption("OATS_AddTeleportStationSelectTile".Translate(), BeginSelectStationTile)
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void AddRandomStation()
        {
            if (!OuterrealmTeleportNetworkUtility.TryFindNewStationTile(out PlanetTile tile))
            {
                Messages.Message("OATS_CannotAddTeleportStationHere".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!OuterrealmTeleportNetworkUtility.TryAddStationAt(tile, out _, out TaggedString reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
            }
        }

        private void BeginSelectStationTile()
        {
            CameraJumper.TryJump(new GlobalTargetInfo(Map.Tile));
            Find.WorldSelector.ClearSelection();
            Find.TilePicker.StartTargeting_NewTemp(
                validator: tile =>
                {
                    if (OuterrealmTeleportNetworkUtility.CanPlaceStationAt(tile, out TaggedString reason))
                    {
                        return true;
                    }

                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    return false;
                },
                tileChosen: tile =>
                {
                    Find.World.renderer.wantedMode = WorldRenderMode.None;
                    if (!OuterrealmTeleportNetworkUtility.TryAddStationAt(tile, out _, out TaggedString reason))
                    {
                        Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    }
                },
                onGuiAction: DrawSelectTileMouseAttachment,
                title: "OATS_SelectTeleportStationTile".Translate(),
                showRandomButton: false,
                selectTileBehindObject: true,
                hideFormCaravanGizmo: true,
                canCancel: true,
                noTileChosenMessage: "OATS_SelectTeleportStationTile".Translate());
        }

        private static void DrawSelectTileMouseAttachment()
        {
            Vector2 mousePosition = Event.current.mousePosition;
            GUI.DrawTexture(new Rect(mousePosition.x + 8f, mousePosition.y + 8f, 32f, 32f), OuterrealmTeleportStationTex.AddStation);
        }
    }
}
