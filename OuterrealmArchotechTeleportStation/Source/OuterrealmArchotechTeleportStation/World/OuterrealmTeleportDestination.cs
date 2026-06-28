using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 传送网络中的统一目的地。
    /// 现在支持世界地图传送站和玩家基地内的传送门；未来未启用传送门只需要在建筑可用性检查中被过滤。
    /// </summary>
    public class OuterrealmTeleportDestination
    {
        private readonly OuterrealmArchotechTeleportStationWorldObject station;
        private readonly Building_OuterrealmArchotechTeleportPortal portal;

        private OuterrealmTeleportDestination(
            OuterrealmArchotechTeleportStationWorldObject station,
            Building_OuterrealmArchotechTeleportPortal portal)
        {
            this.station = station;
            this.portal = portal;
        }

        public OuterrealmArchotechTeleportStationWorldObject Station => station;

        public Building_OuterrealmArchotechTeleportPortal Portal => portal;

        public PlanetTile Tile
        {
            get
            {
                if (station != null)
                {
                    return station.Tile;
                }

                return portal?.Map?.Tile ?? PlanetTile.Invalid;
            }
        }

        public TaggedString LabelCap
        {
            get
            {
                if (station != null)
                {
                    return station.LabelCap;
                }

                return "OATS_MapPortalDestinationLabel".Translate(portal.LabelCap, MapLabelCap(portal.Map));
            }
        }

        public LookTargets LookTargets => station != null ? new LookTargets(station) : new LookTargets(portal);

        public static OuterrealmTeleportDestination ForStation(OuterrealmArchotechTeleportStationWorldObject station)
        {
            return new OuterrealmTeleportDestination(station, null);
        }

        public static OuterrealmTeleportDestination ForPortal(Building_OuterrealmArchotechTeleportPortal portal)
        {
            return new OuterrealmTeleportDestination(null, portal);
        }

        public bool IsValid()
        {
            if (station != null)
            {
                return !station.Destroyed && station.Tile.Valid;
            }

            return portal != null && portal.CanUseAsTeleportDestination(out _);
        }

        public bool MatchesStation(OuterrealmArchotechTeleportStationWorldObject origin)
        {
            return station != null && station == origin;
        }

        public bool MatchesPortal(Building_OuterrealmArchotechTeleportPortal origin)
        {
            return portal != null && portal == origin;
        }

        public bool TryTeleportCaravan(Caravan caravan)
        {
            if (!IsValid())
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            if (station != null)
            {
                OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, station);
                return true;
            }

            Map map = portal.Map;
            IntVec3 root = GetArrivalRoot(portal);
            string caravanName = caravan.Name;
            CaravanEnterMapUtility.Enter(
                caravan,
                map,
                pawn => FindArrivalCell(root, map),
                CaravanDropInventoryMode.DoNotDrop,
                draftColonists: false);
            Messages.Message(
                "OATS_MessageCaravanTeleported".Translate(caravanName, LabelCap),
                LookTargets,
                MessageTypeDefOf.TaskCompletion);
            return true;
        }

        private static TaggedString MapLabelCap(Map map)
        {
            if (map?.Parent != null)
            {
                return map.Parent.LabelCap;
            }

            return "OATS_UnknownMapLabel".Translate();
        }

        private static IntVec3 GetArrivalRoot(Building_OuterrealmArchotechTeleportPortal portal)
        {
            if (portal.InteractionCell.IsValid && portal.InteractionCell.InBounds(portal.Map))
            {
                return portal.InteractionCell;
            }

            return portal.Position;
        }

        private static IntVec3 FindArrivalCell(IntVec3 root, Map map)
        {
            if (CellFinder.TryFindRandomCellNear(
                    root,
                    map,
                    8,
                    cell => cell.Standable(map) && !cell.Fogged(map),
                    out IntVec3 result))
            {
                return result;
            }

            return CellFinder.RandomSpawnCellForPawnNear(root, map);
        }
    }
}
