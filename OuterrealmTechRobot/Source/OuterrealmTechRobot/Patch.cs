using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    public static class Patch_MassUtility_Capacity
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn p, ref float __result, StringBuilder explanation)
        {
            if (p != null && p.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                if (__result < 999999f)
                {
                    __result = 999999f;
                    if (explanation != null)
                    {
                        if (explanation.Length > 0)
                            explanation.AppendLine();
                        explanation.Append($"  - {p.LabelShortCap} (ArtificialMaid): {999999f.ToStringMassOffset()}");
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Patch_Pawn_SpawnSetup
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, Map map)
        {
            if (map != null && __instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                var mapComp = map.GetComponent<ArtificialMaidMapComponent>();
                mapComp?.RegisterMaid(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    public static class Patch_Pawn_DeSpawn
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance)
        {
            if (__instance.Map != null && __instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                var mapComp = __instance.Map.GetComponent<ArtificialMaidMapComponent>();
                mapComp?.UnregisterMaid(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
    public static class Patch_Thing_Destroy
    {
        [HarmonyPrefix]
        public static void Prefix(Thing __instance)
        {
            if (__instance is Pawn pawn && pawn.Map != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                var mapComp = pawn.Map.GetComponent<ArtificialMaidMapComponent>();
                mapComp?.UnregisterMaid(pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "ShouldHaveIdeo", MethodType.Getter)]
    public static class Patch_Pawn_ShouldHaveIdeo
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "PreApplyDamage")]
    public static class Patch_Pawn_PreApplyDamage
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = false;
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                absorbed = true;
                return false;
            }

            return true;
        }
    }

    // Method 1: Intercept Pawn.Kill
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 被 Kill 时也尝试注销，确保计数准确（即使它不死）
                if (__instance.Map != null)
                {
                    var mapComp = __instance.Map.GetComponent<ArtificialMaidMapComponent>();
                    mapComp?.UnregisterMaid(__instance);
                }

                __instance.health.Reset();
                Find.LetterStack.ReceiveLetter("ArtificialMaid_DeathLetter_Label".Translate(),
                    "ArtificialMaid_DeathLetter_Text".Translate(__instance.LabelShort), LetterDefOf.Death, __instance);
                return false;
            }

            return true;
        }
    }


    // Method 2 Supplemental: Patch Corpse to trigger recovery if dead
    [HarmonyPatch(typeof(Corpse), nameof(Corpse.TickRare))]
    public static class Patch_Corpse_TickRare
    {
        [HarmonyPostfix]
        public static void Postfix(Corpse __instance)
        {
            Pawn pawn = __instance.InnerPawn;
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                var hediff = pawn.health.hediffSet.GetFirstHediff<Hediff_ArtificialMaidRecovery>();
                hediff?.ManualTickRare();
            }
        }
    }

    // Method 3: Strengthening - Patch CheckForStateChange to prevent death state
    [HarmonyPatch(typeof(Pawn_HealthTracker), "CheckForStateChange")]
    public static class Patch_Pawn_HealthTracker_CheckForStateChange
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_HealthTracker __instance, Pawn ___pawn)
        {
            if (___pawn != null && ___pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                if (__instance.ShouldBeDead() || __instance.ShouldBeDowned())
                {
                    __instance.Reset();
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), "CombinedDisabledWorkTags", MethodType.Getter)]
    public static class Patch_Pawn_CombinedDisabledWorkTags
    {
        public static void Postfix(Pawn __instance, ref WorkTags __result)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = WorkTags.None;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetDisabledWorkTypes))]
    public static class Patch_Pawn_GetDisabledWorkTypes
    {
        public static void Postfix(Pawn __instance, List<WorkTypeDef> __result)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result.Clear();
            }
        }
    }

    [HarmonyPatch(typeof(PawnCapacitiesHandler), "GetLevel")]
    public static class Patch_PawnCapacitiesHandler_GetLevel
    {
        public static void Postfix(PawnCapacitiesHandler __instance, PawnCapacityDef capacity, ref float __result,
            Pawn ___pawn)
        {
            if (___pawn != null && ___pawn.def == ArtificialMaidDefOf.ArtificialMaid && !___pawn.Dead)
            {
                if (__result < 2.0f)
                {
                    __result = 2.0f;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), "AddGene", new System.Type[] { typeof(GeneDef), typeof(bool) })]
    public static class Patch_Pawn_GeneTracker_AddGene
    {
        public static bool Prefix(Pawn_GeneTracker __instance, GeneDef geneDef)
        {
            if (geneDef == ArtificialMaidDefOf.ArtificialMaid_Core)
            {
                if (__instance.pawn != null && __instance.pawn.def != ArtificialMaidDefOf.ArtificialMaid)
                {
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), "RemoveGene")]
    public static class Patch_Pawn_GeneTracker_RemoveGene
    {
        public static bool Prefix(Pawn_GeneTracker __instance, Gene gene)
        {
            if (gene.def == ArtificialMaidDefOf.ArtificialMaid_Core)
            {
                if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
                {
                    return false;
                }
            }

            return true;
        }
    }
}