using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Maps the Architect category Def to the custom PhantomWall2 passability designator.
    /// </summary>
    [HarmonyPatch(typeof(DesignationCategoryDef), nameof(DesignationCategoryDef.ResolveReferences))]
    public static class DesignationCategoryDefResolveReferencesPhantomWall2DesignatorPatch
    {
        // Keep this in sync with your DesignationCategoryDef.defName in XML.
        private const string TargetCategoryDefName = "OmniCrafter_Category";

        public static void Postfix(DesignationCategoryDef __instance)
        {
            if (__instance == null || __instance.defName != TargetCategoryDefName)
                return;

            List<Type> specialDesignatorClasses = __instance.specialDesignatorClasses;
            if (specialDesignatorClasses == null)
            {
                specialDesignatorClasses = new List<Type>();
                __instance.specialDesignatorClasses = specialDesignatorClasses;
            }

            Type designatorType = typeof(Designator_PhantomWall2Passability);
            if (!specialDesignatorClasses.Contains(designatorType))
                specialDesignatorClasses.Add(designatorType);
        }
    }
}
