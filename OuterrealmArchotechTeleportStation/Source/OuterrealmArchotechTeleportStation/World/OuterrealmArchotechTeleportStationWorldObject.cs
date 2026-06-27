using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    public class OuterrealmArchotechTeleportStationWorldObject : MapParent
    {
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(caravan))
            {
                yield return option;
            }

            if (caravan == null || !caravan.IsPlayerControlled)
            {
                yield break;
            }

            if (caravan.Tile != Tile)
            {
                yield return new FloatMenuOption("OATS_CannotTeleportNotAtStation".Translate(), null);
                yield break;
            }

            List<OuterrealmArchotechTeleportStationWorldObject> destinations =
                OuterrealmTeleportNetworkUtility.GetDestinationStations(this);
            if (destinations.Count == 0)
            {
                yield return new FloatMenuOption("OATS_CannotTeleportNoDestinations".Translate(), null);
                yield break;
            }

            foreach (OuterrealmArchotechTeleportStationWorldObject destination in destinations)
            {
                OuterrealmArchotechTeleportStationWorldObject localDestination = destination;
                yield return new FloatMenuOption(
                    "OATS_CommandTeleportToStation".Translate(localDestination.LabelCap),
                    () => OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, this, localDestination));
            }
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (!HasMap)
            {
                return false;
            }

            // 传送站地标永久保留；只有局部地图空置时允许卸载。
            return !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }
    }
}
