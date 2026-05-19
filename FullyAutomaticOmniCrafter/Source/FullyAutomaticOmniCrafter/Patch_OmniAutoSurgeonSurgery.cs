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
}

