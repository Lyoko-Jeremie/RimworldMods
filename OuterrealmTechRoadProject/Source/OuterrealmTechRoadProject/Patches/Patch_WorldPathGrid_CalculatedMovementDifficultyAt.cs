using HarmonyLib;
using OuterrealmTechRoadProject.World;
using RimWorld.Planet;

namespace OuterrealmTechRoadProject.Patches
{
    [HarmonyPatch(typeof(WorldPathGrid), nameof(WorldPathGrid.CalculatedMovementDifficultyAt))]
    public static class Patch_WorldPathGrid_CalculatedMovementDifficultyAt
    {
        public static void Postfix(ref float __result, PlanetTile tile)
        {
            if (__result < 1000f)
            {
                return;
            }

            Defs.DefModExtension_OuterrealmLinkRoad extension;
            if (OuterrealmLinkUtility.TileHasOuterrealmLink(tile, out extension))
            {
                __result = extension != null ? extension.impassableTileMovementDifficulty : 4f;
            }
        }
    }
}
