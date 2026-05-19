using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    [HarmonyPatch(typeof(Recipe_Surgery), "CheckSurgeryFail")]
    public static class Patch_OmniAutoSurgeonSurgery_CheckSurgeryFail
    {
        public static bool Prefix(ref bool __result, Pawn surgeon, Pawn patient, List<Thing> ingredients, BodyPartRecord part, Bill bill)
        {
            if (!OmniAutoSurgeonSurgeryContext.IsActive)
            {
                return true;
            }

            // OmniAutoSurgeon operations should never fail surgery checks.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(RecipeWorker), "ConsumeIngredient")]
    public static class Patch_OmniAutoSurgeonSurgery_ConsumeIngredient
    {
        public static bool Prefix()
        {
            return !OmniAutoSurgeonSurgeryContext.IsActive;
        }
    }

    [HarmonyPatch(typeof(Thing), "Destroy")]
    public static class Patch_OmniAutoSurgeonSurgery_ThingDestroy
    {
        public static bool Prefix(Thing __instance)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // We prevent destruction of things during OmniAutoSurgeon operations.
                // This covers cases where RecipeWorkers manually destroy ingredients.
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Thing), "SplitOff")]
    public static class Patch_OmniAutoSurgeonSurgery_ThingSplitOff
    {
        public static bool Prefix(Thing __instance, int count, ref Thing __result)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // Return the instance itself instead of splitting it, 
                // preventing stack count reduction.
                __result = __instance;
                return false;
            }
            return true;
        }
    }
}

