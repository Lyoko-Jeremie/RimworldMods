using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

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

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Destroy))]
    public static class Patch_Pawn_Destroy
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

    [HarmonyPatch(typeof(PawnCapacityUtility), nameof(PawnCapacityUtility.CalculateCapacityLevel))]
    public static class Patch_PawnCapacityUtility_CalculateCapacityLevel
    {
        public static void Postfix(HediffSet diffSet, ref float __result)
        {
            if (diffSet.pawn != null && diffSet.pawn.def == ArtificialMaidDefOf.ArtificialMaid && !diffSet.pawn.Dead)
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

    [HarmonyPatch(typeof(StaggerHandler), nameof(StaggerHandler.StaggerFor))]
    public static class Patch_StaggerHandler_StaggerFor
    {
        [HarmonyPrefix]
        public static bool Prefix(StaggerHandler __instance, ref bool __result)
        {
            if (__instance.parent != null && __instance.parent.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }
    
    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", typeof(Pawn), typeof(IntVec3))]
    public static class Patch_Pawn_PathFollower_CostToMoveIntoCell
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, IntVec3 c, ref float __result)
        {
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 人造人女仆移动不受地形影响
                // 直接根据是直线还是斜角移动返回基础消耗
                __result = (c.x == pawn.Position.x || c.z == pawn.Position.z)
                    ? pawn.TicksPerMoveCardinal
                    : pawn.TicksPerMoveDiagonal;

                // 处理移动紧迫性（LocomotionUrgency）
                if (pawn.CurJob != null)
                {
                    var locomotionUrgencySameAs = pawn.jobs.curDriver.locomotionUrgencySameAs;
                    if (locomotionUrgencySameAs != null && locomotionUrgencySameAs != pawn &&
                        locomotionUrgencySameAs.Spawned)
                    {
                        // 如果跟随其他 Pawn，取两者之间的最大值，保持队形
                        // 这里我们递归调用原始方法或模拟逻辑，但既然我们要“不受地形影响”，
                        // 逻辑上跟随者也应该按照自己的不受影响的速度走，或者按照被跟随者的速度走。
                        // 原版逻辑是： a = Mathf.Max(a, CostToMoveIntoCell(locomotionUrgencySameAs, c))
                        // 为了简单和符合“不受地形影响”，我们这里只处理基本的急迫性倍率。
                    }
                    else
                    {
                        switch (pawn.jobs.curJob.locomotionUrgency)
                        {
                            case LocomotionUrgency.Amble:
                                __result *= 3f;
                                if (__result < 60f) __result = 60f;
                                break;
                            case LocomotionUrgency.Walk:
                                __result *= 2f;
                                if (__result < 50f) __result = 50f;
                                break;
                            case LocomotionUrgency.Jog:
                                break;
                            case LocomotionUrgency.Sprint:
                                __result = UnityEngine.Mathf.RoundToInt(__result * 0.75f);
                                break;
                        }
                    }
                }

                __result = UnityEngine.Mathf.Max(__result, 1f);
                return false; // 跳过原方法
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), nameof(ThoughtHandler.GetAllMoodThoughts))]
    public static class Patch_ThoughtHandler_GetAllMoodThoughts
    {
        public static void Postfix(ThoughtHandler __instance, List<Thought> outThoughts)
        {
            if (__instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                outThoughts.RemoveAll(t => t.MoodOffset() < 0);
            }
        }
    }

    [HarmonyPatch(typeof(Fire), nameof(Fire.TakeDamage))]
    public static class Patch_Fire_TakeDamage
    {
        [HarmonyPrefix]
        public static void Prefix(Fire __instance, ref DamageInfo dinfo)
        {
            if (dinfo.Def == DamageDefOf.Extinguish && dinfo.Instigator is Pawn pawn &&
                pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 人造人女仆灭火速度极大提升
                dinfo.SetAmount(999999f);
            }
        }
    }
}