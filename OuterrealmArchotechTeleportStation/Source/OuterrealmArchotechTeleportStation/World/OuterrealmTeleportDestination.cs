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
        private readonly PlanetTile worldTile;

        private OuterrealmTeleportDestination(
            OuterrealmArchotechTeleportStationWorldObject station,
            Building_OuterrealmArchotechTeleportPortal portal,
            PlanetTile worldTile)
        {
            this.station = station;
            this.portal = portal;
            this.worldTile = worldTile;
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

                if (worldTile.Valid)
                {
                    return worldTile;
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

                if (worldTile.Valid)
                {
                    return "OATS_WorldTileDestinationLabel".Translate(worldTile.ToString());
                }

                return "OATS_MapPortalDestinationLabel".Translate(portal.LabelCap, MapLabelCap(portal.Map));
            }
        }

        public LookTargets LookTargets
        {
            get
            {
                if (station != null)
                {
                    return new LookTargets(station);
                }

                if (worldTile.Valid)
                {
                    return new LookTargets(worldTile);
                }

                return new LookTargets(portal);
            }
        }

        public TaggedString GetMenuLabel(PlanetTile originTile)
        {
            if (station != null)
            {
                return GetStationMenuLabel(originTile);
            }

            return LabelCap;
        }

        public static OuterrealmTeleportDestination ForStation(OuterrealmArchotechTeleportStationWorldObject station)
        {
            return new OuterrealmTeleportDestination(station, null, PlanetTile.Invalid);
        }

        public static OuterrealmTeleportDestination ForPortal(Building_OuterrealmArchotechTeleportPortal portal)
        {
            return new OuterrealmTeleportDestination(null, portal, PlanetTile.Invalid);
        }

        public static OuterrealmTeleportDestination ForWorldTile(PlanetTile tile)
        {
            return new OuterrealmTeleportDestination(null, null, tile);
        }

        public bool IsValid()
        {
            if (station != null)
            {
                return !station.Destroyed && station.Tile.Valid;
            }

            if (worldTile.Valid)
            {
                return OuterrealmTeleportNetworkUtility.CanTeleportToWorldTile(worldTile, out _);
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

            if (worldTile.Valid)
            {
                OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, worldTile);
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

        private TaggedString GetStationMenuLabel(PlanetTile originTile)
        {
            TaggedString biomeLabel = "OATS_UnknownMapLabel".Translate();
            if (station.Tile.Valid)
            {
                BiomeDef biome = Find.WorldGrid[station.Tile]?.PrimaryBiome;
                if (biome != null)
                {
                    biomeLabel = biome.LabelCap;
                }
            }

            string tileLabel = station.Tile.Valid ? station.Tile.tileId.ToString() : "?";
            if (originTile.Valid && station.Tile.Valid && originTile.Layer == station.Tile.Layer)
            {
                int distance = (int)Find.WorldGrid.ApproxDistanceInTiles(originTile, station.Tile);
                return "OATS_TeleportStationDestinationLabelWithDistance".Translate(
                    station.LabelCap,
                    biomeLabel,
                    tileLabel,
                    distance);
            }

            return "OATS_TeleportStationDestinationLabel".Translate(station.LabelCap, biomeLabel, tileLabel);
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
