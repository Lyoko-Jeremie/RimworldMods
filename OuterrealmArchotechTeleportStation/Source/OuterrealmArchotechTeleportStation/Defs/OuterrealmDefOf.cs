using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    [DefOf]
    public static class OuterrealmDefOf
    {
        public static WorldObjectDef OuterrealmArchotechTeleportStation;
        public static MapGeneratorDef OuterrealmArchotechTeleportStationMap;
        public static GenStepDef OuterrealmArchotechTeleportStationMapLayout;
        public static ThingDef OuterrealmArchotechTeleportPortal;

        static OuterrealmDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OuterrealmDefOf));
        }
    }
}
