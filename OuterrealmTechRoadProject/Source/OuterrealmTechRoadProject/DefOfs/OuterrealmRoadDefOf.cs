using RimWorld;
using Verse;

namespace OuterrealmTechRoadProject.DefOfs
{
    [DefOf]
    public static class OuterrealmRoadDefOf
    {
        public static RoadDef OuterrealmTech_OuterrealmLink;

        static OuterrealmRoadDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OuterrealmRoadDefOf));
        }
    }
}
