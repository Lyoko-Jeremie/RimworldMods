using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 传送站专用进入行为。
    /// 原版 CaravanArrivalAction_Enter 只允许进入已经生成地图的 MapParent；
    /// 这里保留原版右键移动与 Dev 立即进入逻辑，但在抵达时按需生成传送站地图。
    /// </summary>
    public class CaravanArrivalAction_EnterOuterrealmTeleportStation : CaravanArrivalAction
    {
        private OuterrealmArchotechTeleportStationWorldObject station;

        public override string Label => "EnterMap".Translate(station.Label);

        public override string ReportString => "CaravanEntering".Translate(station.Label);

        public CaravanArrivalAction_EnterOuterrealmTeleportStation()
        {
        }

        public CaravanArrivalAction_EnterOuterrealmTeleportStation(OuterrealmArchotechTeleportStationWorldObject station)
        {
            this.station = station;
        }

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport report = base.StillValid(caravan, destinationTile);
            if (!report)
            {
                return report;
            }

            return station != null && station.Tile != destinationTile
                ? (FloatMenuAcceptanceReport)false
                : CanEnter(caravan, station);
        }

        public override void Arrived(Caravan caravan)
        {
            if (!CanEnter(caravan, station).Accepted)
            {
                return;
            }

            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(
                station.Tile,
                station.def.overrideMapSize ?? Find.World.info.initialMapSize,
                station.def);
            if (map == null)
            {
                return;
            }

            CaravanDropInventoryMode dropInventoryMode = map.IsPlayerHome
                ? CaravanDropInventoryMode.UnloadIndividually
                : CaravanDropInventoryMode.DoNotDrop;
            bool draftColonists = station.Faction != null && station.Faction.HostileTo(Faction.OfPlayer);
            if (caravan.IsPlayerControlled || station.Faction == Faction.OfPlayer)
            {
                Find.LetterStack.ReceiveLetter(
                    "LetterLabelCaravanEnteredMap".Translate(station),
                    "LetterCaravanEnteredMap".Translate(caravan.Label, station).CapitalizeFirst(),
                    LetterDefOf.NeutralEvent,
                    caravan.PawnsListForReading);
            }

            CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, dropInventoryMode, draftColonists);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref station, "station");
        }

        public static FloatMenuAcceptanceReport CanEnter(
            Caravan caravan,
            OuterrealmArchotechTeleportStationWorldObject station)
        {
            if (station == null || !station.Spawned || !station.Tile.Valid)
            {
                return false;
            }

            if (station.EnterCooldownBlocksEntering())
            {
                return FloatMenuAcceptanceReport.WithFailMessage(
                    "MessageEnterCooldownBlocksEntering".Translate(station.EnterCooldownTicksLeft().ToStringTicksToPeriod()));
            }

            return true;
        }

        public static IEnumerable<FloatMenuOption> GetFloatMenuOptions(
            Caravan caravan,
            OuterrealmArchotechTeleportStationWorldObject station)
        {
            return CaravanArrivalActionUtility.GetFloatMenuOptions(
                (Func<FloatMenuAcceptanceReport>)(() => CanEnter(caravan, station)),
                (Func<CaravanArrivalAction_EnterOuterrealmTeleportStation>)(() =>
                    new CaravanArrivalAction_EnterOuterrealmTeleportStation(station)),
                "EnterMap".Translate(station.Label),
                caravan,
                station.Tile,
                station);
        }
    }
}
