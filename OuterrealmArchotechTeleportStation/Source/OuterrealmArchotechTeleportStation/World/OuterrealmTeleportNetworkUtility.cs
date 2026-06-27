using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    public static class OuterrealmTeleportNetworkUtility
    {
        private const int MinStationDistance = 12;
        private const int RandomSiteMinDistance = 10;
        private const int RandomSiteMaxDistance = 80;

        private static readonly List<OuterrealmArchotechTeleportStationWorldObject> TmpStations = new List<OuterrealmArchotechTeleportStationWorldObject>();

        public static List<OuterrealmArchotechTeleportStationWorldObject> GetStations()
        {
            TmpStations.Clear();
            List<WorldObject> objects = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is OuterrealmArchotechTeleportStationWorldObject station && !station.Destroyed)
                {
                    TmpStations.Add(station);
                }
            }

            TmpStations.SortBy(station => station.Tile.tileId);
            return TmpStations.ToList();
        }

        public static List<OuterrealmArchotechTeleportStationWorldObject> GetDestinationStations(
            OuterrealmArchotechTeleportStationWorldObject origin)
        {
            List<OuterrealmArchotechTeleportStationWorldObject> stations = GetStations();
            stations.RemoveAll(station => station == origin || station.Destroyed || !station.Tile.Valid);
            if (origin != null && origin.Tile.Valid)
            {
                stations.SortBy(station => Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, station.Tile));
            }

            return stations;
        }

        public static void TeleportCaravan(
            Caravan caravan,
            OuterrealmArchotechTeleportStationWorldObject origin,
            OuterrealmArchotechTeleportStationWorldObject destination)
        {
            if (caravan == null || destination == null || destination.Destroyed || !destination.Tile.Valid)
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (origin != null && caravan.Tile != origin.Tile)
            {
                Messages.Message("OATS_CannotTeleportNotAtStation".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            TeleportCaravan(caravan, destination);
        }

        public static void TeleportCaravan(Caravan caravan, OuterrealmArchotechTeleportStationWorldObject destination)
        {
            if (caravan == null || destination == null || destination.Destroyed || !destination.Tile.Valid)
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            caravan.pather.StopDead();
            caravan.Tile = destination.Tile;
            caravan.Notify_Teleported();
            Messages.Message(
                "OATS_MessageCaravanTeleported".Translate(caravan.Name, destination.LabelCap),
                new LookTargets(caravan, destination),
                MessageTypeDefOf.TaskCompletion);
        }

        public static bool TryFindNewStationTile(out PlanetTile tile)
        {
            PlanetTile nearTile;
            bool hasPlayerTile = TileFinder.TryFindRandomPlayerTile(out nearTile, allowCaravans: true);
            if (hasPlayerTile && TileFinder.TryFindNewSiteTile(
                    out tile,
                    nearTile,
                    RandomSiteMinDistance,
                    RandomSiteMaxDistance,
                    allowCaravans: true,
                    validator: candidate => CanPlaceStationAt(candidate, out _)))
            {
                return true;
            }

            return TileFinder.TryFindNewSiteTile(
                out tile,
                RandomSiteMinDistance,
                RandomSiteMaxDistance,
                allowCaravans: true,
                validator: candidate => CanPlaceStationAt(candidate, out _)) ||
                TryFindRandomValidTile(out tile);
        }

        private static bool TryFindRandomValidTile(out PlanetTile tile)
        {
            int tileCount = Find.WorldGrid.TilesCount;
            for (int i = 0; i < 2000; i++)
            {
                PlanetTile candidate = new PlanetTile(Rand.Range(0, tileCount));
                if (CanPlaceStationAt(candidate, out _))
                {
                    tile = candidate;
                    return true;
                }
            }

            for (int i = 0; i < tileCount; i++)
            {
                PlanetTile candidate = new PlanetTile(i);
                if (CanPlaceStationAt(candidate, out _))
                {
                    tile = candidate;
                    return true;
                }
            }

            tile = PlanetTile.Invalid;
            return false;
        }

        public static bool TryAddStationAt(
            PlanetTile tile,
            out OuterrealmArchotechTeleportStationWorldObject station,
            out TaggedString reason,
            bool sendMessage = true)
        {
            station = null;
            if (!CanPlaceStationAt(tile, out reason))
            {
                return false;
            }

            station = (OuterrealmArchotechTeleportStationWorldObject)WorldObjectMaker.MakeWorldObject(
                OuterrealmDefOf.OuterrealmArchotechTeleportStation);
            station.Tile = tile;
            Find.WorldObjects.Add(station);
            if (sendMessage)
            {
                Messages.Message(
                    "OATS_MessageTeleportStationAdded".Translate(station.LabelCap),
                    new LookTargets(station),
                    MessageTypeDefOf.PositiveEvent);
            }

            return true;
        }

        public static bool CanPlaceStationAt(PlanetTile tile, out TaggedString reason)
        {
            if (!tile.Valid)
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            if (GetStations().Count >= MaxStationCount())
            {
                reason = "OATS_CannotAddTeleportStationMaxCount".Translate(MaxStationCount());
                return false;
            }

            if (Find.World.Impassable(tile))
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            Tile tileInfo = Find.WorldGrid[tile];
            if (tileInfo?.PrimaryBiome == null || !tileInfo.PrimaryBiome.canBuildBase)
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            if (Find.WorldObjects.AnyWorldObjectAt(tile))
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            List<OuterrealmArchotechTeleportStationWorldObject> stations = GetStations();
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i].Tile.Layer == tile.Layer &&
                    Find.WorldGrid.ApproxDistanceInTiles(stations[i].Tile, tile) < MinStationDistance)
                {
                    reason = "OATS_CannotAddTeleportStationHere".Translate();
                    return false;
                }
            }

            reason = TaggedString.Empty;
            return true;
        }

        public static int MaxStationCount()
        {
            int tileCount = Find.WorldGrid.TilesCount;
            return Mathf.Clamp(Mathf.RoundToInt(tileCount / 1200f), 6, 24);
        }
    }
}
