using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

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
                var mapComp = ArtificialMaidMapComponent.Get(map);
                mapComp?.RegisterMaid(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.ForceWait))]
    public static class Patch_PawnUtility_ForceWait
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, ref int ticks)
        {
            if (ticks <= 0 && pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                ticks = 1;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    public static class Patch_Pawn_DeSpawn
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                ArtificialMaidBackupCloud.NotifyMaidDestroyed(__instance);
                if (__instance.Map != null)
                {
                    var mapComp = ArtificialMaidMapComponent.Get(__instance.Map);
                    mapComp?.UnregisterMaid(__instance);
                }
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
                var mapComp = ArtificialMaidMapComponent.Get(__instance.Map);
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
                ArtificialMaidBackupCloud.NotifyMaidKilled(__instance);

                // 被 Kill 时也尝试注销，确保计数准确（即使它不死）
                if (__instance.Map != null)
                {
                    var mapComp = ArtificialMaidMapComponent.Get(__instance.Map);
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

    [HarmonyPatch(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove),
        new System.Type[] { typeof(List<Pawn>), typeof(float), typeof(float), typeof(bool), typeof(StringBuilder) })]
    public static class Patch_CaravanTicksPerMoveUtility_GetTicksPerMove
    {
        private const int MinArtificialMaidWorldTicksPerMove = 50;

        [HarmonyPostfix]
        public static void Postfix(List<Pawn> pawns, bool isShuttle, StringBuilder explanation, ref int __result)
        {
            if (isShuttle || __result <= 0 || __result >= MinArtificialMaidWorldTicksPerMove ||
                !ContainsArtificialMaid(pawns))
            {
                return;
            }

            // 世界寻路使用整数边权；过低的移动成本会被道路倍率截断为 0，导致 A* 退化出异常路线。
            __result = MinArtificialMaidWorldTicksPerMove;

            if (explanation != null)
            {
                explanation.AppendLine();
                explanation.Append("  " + "ArtificialMaidCaravanWorldPathingLimit".Translate(
                    (60000f / MinArtificialMaidWorldTicksPerMove).ToString("0.#")));
            }
        }

        private static bool ContainsArtificialMaid(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return false;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
                {
                    return true;
                }
            }

            return false;
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

    [HarmonyPatch(typeof(DamageWorker_Extinguish), nameof(DamageWorker_Extinguish.Apply))]
    public static class Patch_DamageWorker_Extinguish_Apply
    {
        [HarmonyPrefix]
        public static void Prefix(ref DamageInfo dinfo)
        {
            if (dinfo.Instigator is Pawn pawn && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 人造人女仆灭火速度极大提升
                dinfo.SetAmount(999999f);
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wear), nameof(JobDriver_Wear.Notify_Starting))]
    public static class Patch_JobDriver_Wear_Notify_Starting
    {
        private static readonly AccessTools.FieldRef<JobDriver_Wear, int> DurationRef =
            AccessTools.FieldRefAccess<JobDriver_Wear, int>("duration");

        [HarmonyPostfix]
        public static void Postfix(JobDriver_Wear __instance)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 穿戴物品时间缩短为 1 tick
                DurationRef(__instance) = 1;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_RemoveApparel), nameof(JobDriver_RemoveApparel.Notify_Starting))]
    public static class Patch_JobDriver_RemoveApparel_Notify_Starting
    {
        private static readonly AccessTools.FieldRef<JobDriver_RemoveApparel, int> DurationRef =
            AccessTools.FieldRefAccess<JobDriver_RemoveApparel, int>("duration");

        [HarmonyPostfix]
        public static void Postfix(JobDriver_RemoveApparel __instance)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 脱掉物品时间缩短为 1 tick
                DurationRef(__instance) = 1;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_TakeInventory), nameof(JobDriver_TakeInventory.TryMakePreToilReservations))]
    public static class Patch_JobDriver_TakeInventory_TryMakePreToilReservations
    {
        [HarmonyPostfix]
        public static void Postfix(JobDriver_TakeInventory __instance)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 拾取物品延迟缩短为 0
                if (__instance.job != null)
                {
                    __instance.job.takeInventoryDelay = 0;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValueUnfinalized))]
    public static class Patch_StatWorker_GetValueUnfinalized
    {
        private static readonly AccessTools.FieldRef<StatWorker, StatDef> StatRef =
            AccessTools.FieldRefAccess<StatWorker, StatDef>("stat");

        [HarmonyPostfix]
        public static void Postfix(StatWorker __instance, StatRequest req, ref float __result)
        {
            if (req.HasThing && req.Thing is Pawn pawn && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                StatDef stat = StatRef(__instance);
                if (stat == null) return;

                string defName = stat.defName;
                if (defName == "EntityStudyRate" ||
                    defName == "StudyEfficiency" ||
                    defName == "ActivitySuppressionRate" ||
                    defName == "PsychicRitualQuality" ||
                    defName == "PsychicRitualQualityOffset")
                {
                    __result *= 1000000f;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StudyManager), nameof(StudyManager.Study))]
    public static class Patch_StudyManager_Study
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn studier, ref float studyAmount)
        {
            if (studier != null && studier.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                studyAmount *= 1000000f;
            }
        }
    }

    [HarmonyPatch(typeof(StudyManager), nameof(StudyManager.StudyAnomaly))]
    public static class Patch_StudyManager_StudyAnomaly
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn studier, ref float knowledgeAmount)
        {
            if (studier != null && studier.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                knowledgeAmount *= 1000000f;
            }
        }
    }

    [HarmonyPatch(typeof(PsychicRitualToil_InvokeHorax), nameof(PsychicRitualToil_InvokeHorax.Start))]
    public static class Patch_PsychicRitualToil_InvokeHorax_Start
    {
        [HarmonyPrefix]
        public static void Prefix(PsychicRitualToil_InvokeHorax __instance, PsychicRitual psychicRitual)
        {
            Pawn invoker = psychicRitual.assignments.FirstAssignedPawn(__instance.invokerRole);
            if (invoker != null && invoker.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __instance.hoursUntilOutcome *= 0.000001f;
                __instance.hoursUntilHoraxEffect *= 0.000001f;
            }
        }
    }

    [HarmonyPatch(typeof(TraitSet), nameof(TraitSet.GainTrait))]
    public static class Patch_TraitSet_GainTrait_ExclusiveCheck
    {
        private static readonly AccessTools.FieldRef<TraitSet, Pawn> PawnField =
            AccessTools.FieldRefAccess<TraitSet, Pawn>("pawn");

        [HarmonyPrefix]
        public static bool Prefix(TraitSet __instance, Trait trait)
        {
            if (trait?.def == ArtificialMaidDefOf.ArtificialMaidTrait_MasterProtocol)
            {
                Pawn pawn = PawnField(__instance);
                if (pawn == null) return true;

                bool isMaid = pawn.def == ArtificialMaidDefOf.ArtificialMaid || pawn.GetComp<CompArtificialMaid>() != null;
                bool isPlayerFaction = pawn.Faction == Faction.OfPlayer;

                if (!isMaid || !isPlayerFaction)
                {
                    return false; // 拦截非法持有
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Patch_Pawn_SpawnSetup_ExclusiveTraitCheck
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance)
        {
            if (__instance.story?.traits != null)
            {
                var traitDef = ArtificialMaidDefOf.ArtificialMaidTrait_MasterProtocol;
                if (traitDef != null && __instance.story.traits.HasTrait(traitDef))
                {
                    bool isMaid = __instance.def == ArtificialMaidDefOf.ArtificialMaid || __instance.GetComp<CompArtificialMaid>() != null;
                    bool isPlayerFaction = __instance.Faction == Faction.OfPlayer;

                    if (!isMaid || !isPlayerFaction)
                    {
                        var trait = __instance.story.traits.GetTrait(traitDef);
                        if (trait != null)
                        {
                            __instance.story.traits.RemoveTrait(trait);
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(KidnapAIUtility), nameof(KidnapAIUtility.TryFindGoodKidnapVictim))]
    public static class Patch_KidnapAIUtility_TryFindGoodKidnapVictim
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, ref Pawn victim)
        {
            if (__result && victim != null && victim.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                victim = null;
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(KidnapAIUtility), nameof(KidnapAIUtility.ReachableWoundedGuest))]
    public static class Patch_KidnapAIUtility_ReachableWoundedGuest
    {
        [HarmonyPostfix]
        public static void Postfix(ref Pawn __result)
        {
            if (__result != null && __result.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.Kidnap))]
    public static class Patch_KidnappedPawnsTracker_Kidnap
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn)
        {
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CompDevourer), nameof(CompDevourer.StartDigesting))]
    public static class Patch_CompDevourer_StartDigesting
    {
        [HarmonyPostfix]
        public static void Postfix(CompDevourer __instance, LocalTargetInfo target)
        {
            if (target.Pawn != null && target.Pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __instance.Pawn?.Kill(null);
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_ActivateMonolith), "MakeNewToils")]
    public static class Patch_JobDriver_ActivateMonolith_MakeNewToils
    {
        [HarmonyPostfix]
        public static void Postfix(JobDriver_ActivateMonolith __instance, ref IEnumerable<Toil> __result)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = ModifyToils(__result);
            }
        }

        private static IEnumerable<Toil> ModifyToils(IEnumerable<Toil> toils)
        {
            foreach (var toil in toils)
            {
                if (toil.defaultDuration > 1)
                {
                    toil.defaultDuration = 1;
                }
                yield return toil;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_InteractThing), "MakeNewToils")]
    public static class Patch_JobDriver_InteractThing_MakeNewToils
    {
        [HarmonyPostfix]
        public static void Postfix(JobDriver_InteractThing __instance, ref IEnumerable<Toil> __result)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = ModifyToils(__result);
            }
        }

        private static IEnumerable<Toil> ModifyToils(IEnumerable<Toil> toils)
        {
            foreach (var toil in toils)
            {
                if (toil.defaultDuration > 10)
                {
                    toil.defaultDuration = 10;
                }
                yield return toil;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DynamicDrawPhaseAt))]
    public static class Patch_Pawn_DynamicDrawPhaseAt
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            // 性能优化：首先进行快速的 def 检查，避免所有非女仆 Pawn 进入组件查找逻辑
            if (__instance.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return true;
            }

            // 如果女仆正处于工作检测的伪造状态中，则跳过本次绘制（防止在 InteractionCell 产生闪烁）
            // 注意：由于展示柜内部是通过直接调用 PawnRenderer 绘制的，因此不会受到此拦截的影响
            var comp = CompArtificialMaid.GetCompCached(__instance);
            if (comp != null && comp.isFaking)
            {
                return false;
            }
            return true;
        }
    }
}
