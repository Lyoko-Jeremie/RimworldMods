using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    public class WorldComponent_OuterrealmTeleportStationBootstrap : WorldComponent
    {
        private bool ensuredInitialStation;

        public WorldComponent_OuterrealmTeleportStationBootstrap(World world)
            : base(world)
        {
        }

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            if (ensuredInitialStation || OuterrealmTeleportNetworkUtility.GetStations().Count > 0)
            {
                ensuredInitialStation = true;
                return;
            }

            if (OuterrealmTeleportNetworkUtility.TryFindNewStationTile(out PlanetTile tile))
            {
                OuterrealmTeleportNetworkUtility.TryAddStationAt(tile, out _, out _, false);
            }

            ensuredInitialStation = true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ensuredInitialStation, "ensuredInitialStation");
        }
    }
}
