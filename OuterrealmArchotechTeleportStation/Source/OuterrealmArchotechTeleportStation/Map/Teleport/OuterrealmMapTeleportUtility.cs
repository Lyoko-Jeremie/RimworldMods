using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 处理从局部地图把玩家内容转换为远行队并瞬移到目标传送站。
    /// </summary>
    public static class OuterrealmMapTeleportUtility
    {
        /// <summary>
        /// 按远行队界面使用的 Transferable 结构收集当前地图可传送内容。
        /// </summary>
        public static List<TransferableOneWay> CreateTransferables(Map map, bool selectAll)
        {
            List<TransferableOneWay> transferables = new List<TransferableOneWay>();
            if (map == null)
            {
                return transferables;
            }

            List<Pawn> pawns = Dialog_FormCaravan.AllSendablePawns(map, true);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn.Faction != Faction.OfPlayer)
                {
                    continue;
                }

                AddToTransferables(transferables, pawn, selectAll);
                Thing carriedThing = pawn.carryTracker?.CarriedThing;
                if (carriedThing != null)
                {
                    AddToTransferables(transferables, carriedThing, selectAll);
                }
            }

            List<Thing> items = CaravanFormingUtility.AllReachableColonyItems(
                map,
                allowEvenIfOutsideHomeArea: true,
                allowEvenIfReserved: true,
                canMinify: true);
            for (int i = 0; i < items.Count; i++)
            {
                AddToTransferables(transferables, items[i], selectAll);
            }

            return transferables;
        }

        /// <summary>
        /// 按玩家在界面中选择的数量传送内容。
        /// </summary>
        public static bool TryTeleportTransferables(
            Map map,
            OuterrealmArchotechTeleportStationWorldObject destination,
            List<TransferableOneWay> transferables,
            bool removeSourceMap)
        {
            if (map == null || destination == null || destination.Destroyed || !destination.Tile.Valid)
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            if (transferables == null || transferables.Count == 0)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(transferables);
            pawns.RemoveAll(pawn => pawn == null || pawn.Dead || pawn.Faction != Faction.OfPlayer);
            if (!pawns.Any(pawn => CaravanUtility.IsOwner(pawn, Faction.OfPlayer) && !pawn.Downed))
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            if (!TryMoveSelectedItemsToPawns(transferables, pawns))
            {
                return false;
            }

            PlanetTile originTile = map.Tile;
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
                return false;
            }

            OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, destination);
            Messages.Message(
                "OATS_MessagePawnsTeleportedFromMap".Translate(destination.LabelCap),
                new LookTargets(caravan, destination),
                MessageTypeDefOf.TaskCompletion);

            if (removeSourceMap && Current.Game.Maps.Contains(map))
            {
                Current.Game.DeinitAndRemoveMap(map, true);
            }

            return true;
        }

        private static void AddToTransferables(List<TransferableOneWay> transferables, Thing thing, bool selectAll)
        {
            if (thing == null || thing.Destroyed)
            {
                return;
            }

            TransferableOneWay transferable =
                TransferableUtility.TransferableMatching<TransferableOneWay>(
                    thing,
                    transferables,
                    TransferAsOneMode.PodsOrCaravanPacking);
            if (transferable == null)
            {
                transferable = new TransferableOneWay();
                transferables.Add(transferable);
            }

            if (transferable.things.Contains(thing))
            {
                return;
            }

            transferable.things.Add(thing);
            if (selectAll)
            {
                transferable.AdjustTo(transferable.CountToTransfer + thing.stackCount);
            }
        }

        private static bool TryMoveSelectedItemsToPawns(List<TransferableOneWay> transferables, List<Pawn> pawns)
        {
            bool hasSelectedItem = transferables.Any(transferable =>
                transferable.CountToTransfer > 0 && !(transferable.AnyThing is Pawn));
            if (hasSelectedItem && !pawns.Any(pawn => pawn.inventory != null && MassUtility.CanEverCarryAnything(pawn)))
            {
                Messages.Message("OATS_CannotTeleportNoItemCarrier".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            for (int i = 0; i < transferables.Count; i++)
            {
                TransferableOneWay transferable = transferables[i];
                if (transferable.CountToTransfer <= 0 || transferable.AnyThing is Pawn)
                {
                    continue;
                }

                TransferableUtility.Transfer(
                    transferable.things,
                    transferable.CountToTransfer,
                    (splitPiece, originalHolder) =>
                    {
                        if (splitPiece.Faction != null && splitPiece.Faction != Faction.OfPlayer)
                        {
                            splitPiece.SetFactionDirect(Faction.OfPlayer);
                        }

                        Thing thing = splitPiece.TryMakeMinified();
                        Pawn carrier = CaravanInventoryUtility.FindPawnToMoveInventoryTo(thing, pawns, null);
                        carrier.inventory.TryAddAndUnforbid(thing);
                    });
            }

            return true;
        }
    }
}
