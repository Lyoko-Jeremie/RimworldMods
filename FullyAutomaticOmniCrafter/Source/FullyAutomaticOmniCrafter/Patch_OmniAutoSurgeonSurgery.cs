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

    [HarmonyPatch(typeof(Recipe_AdministerUsableItem), "ApplyOnPawn")]
    public static class Patch_Recipe_AdministerUsableItem_ApplyOnPawn
    {
        public static bool Prefix(Recipe_AdministerUsableItem __instance, Pawn pawn, List<Thing> ingredients)
        {
            if (!OmniAutoSurgeonSurgeryContext.IsActive) return true;

            // If ingredients is empty, we find the thing def from recipe and simulate usage
            if (ingredients.Count == 0)
            {
                ThingDef itemDef = __instance.recipe.fixedIngredientFilter?.AnyAllowedDef;
                if (itemDef != null)
                {
                    // Create a temporary thing to get its CompUsable
                    Thing tempThing = ThingMaker.MakeThing(itemDef);
                    if (tempThing != null)
                    {
                        CompUsable comp = tempThing.TryGetComp<CompUsable>();
                        if (comp != null)
                        {
                            comp.UsedBy(pawn);
                        }
                    }
                }
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(SurgeryOutcomeComp_MedicineQuality), "XGetter")]
    public static class Patch_SurgeryOutcomeComp_MedicineQuality_XGetter
    {
        public static bool Prefix(ref float __result)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // Always return high quality (e.g. 2.0 for Glitterworld medicine potency)
                __result = 2.0f;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MedicalRecipesUtility), "IsCleanAndDroppable")]
    public static class Patch_MedicalRecipesUtility_IsCleanAndDroppable
    {
        public static bool Prefix(ref bool __result, Pawn pawn, BodyPartRecord part)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // If the part has a spawn thing, we allow it regardless of cleanliness or animal status
                if (part.def.spawnThingOnRemoved != null)
                {
                    __result = true;
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MedicalRecipesUtility), "IsClean")]
    public static class Patch_MedicalRecipesUtility_IsClean
    {
        public static bool Prefix(ref bool __result)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                __result = true;
                return false;
            }
            return true;
        }
    }
}

