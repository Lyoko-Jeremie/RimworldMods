using RimWorld;
using Verse;

namespace OuterrealmTechRoadProject.DefOfs
{
    [DefOf]
    public static class OuterrealmTerrainDefOf
    {
        public static TerrainDef OuterrealmTech_OuterrealmLinkTerrain;

        static OuterrealmTerrainDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OuterrealmTerrainDefOf));
        }
    }
}
