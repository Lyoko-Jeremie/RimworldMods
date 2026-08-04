using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 让人造人女仆在战斗相关调用期间忽略目标的心理隐身。
    /// 使用线程局部状态，避免 RimWorld 1.6 并行 Tick 时不同 Pawn 之间互相干扰。
    /// </summary>
    public static class ArtificialMaidInvisibilityBypass
    {
        [ThreadStatic]
        private static Pawn observer;

        [ThreadStatic]
        private static int depth;

        public struct State
        {
            internal Pawn PreviousObserver;
            internal int PreviousDepth;
        }

        public static void Push(Pawn pawn, out State state)
        {
            state = new State
            {
                PreviousObserver = observer,
                PreviousDepth = depth
            };

            if (pawn == null || pawn.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return;
            }

            observer = pawn;
            depth++;
        }

        public static void Pop(State state)
        {
            observer = state.PreviousObserver;
            depth = state.PreviousDepth;
        }

        public static bool ShouldBypassFor(Pawn pawn)
        {
            // 女仆自身的隐身状态仍按原版处理；仅无视她正在感知的其他 Pawn 的隐身。
            return depth > 0 && pawn != null && pawn != observer;
        }

        public static IEnumerable<LocalTargetInfo> EnumerateTargets(
            IEnumerable<LocalTargetInfo> targets,
            Pawn observingPawn)
        {
            Push(observingPawn, out State state);
            try
            {
                foreach (LocalTargetInfo target in targets)
                {
                    yield return target;
                }
            }
            finally
            {
                Pop(state);
            }
        }
    }

    [HarmonyPatch(typeof(InvisibilityUtility), nameof(InvisibilityUtility.IsPsychologicallyInvisible))]
    public static class Patch_InvisibilityUtility_IsPsychologicallyInvisible_ArtificialMaid
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref bool __result)
        {
            if (!ArtificialMaidInvisibilityBypass.ShouldBypassFor(pawn))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget))]
    public static class Patch_AttackTargetFinder_BestAttackTarget_ArtificialMaid
    {
        [HarmonyPrefix]
        public static void Prefix(IAttackTargetSearcher searcher, out ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Push(searcher?.Thing as Pawn, out __state);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Pop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTargetFrom))]
    public static class Patch_Verb_CanHitTargetFrom_ArtificialMaid
    {
        [HarmonyPrefix]
        public static void Prefix(Verb __instance, out ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Push(__instance.CasterPawn, out __state);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Pop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ThinkNode_JobGiver), nameof(ThinkNode_JobGiver.TryIssueJobPackage))]
    public static class Patch_ThinkNodeJobGiver_TryIssueJobPackage_ArtificialMaid
    {
        [HarmonyPrefix]
        public static void Prefix(ThinkNode_JobGiver __instance, Pawn pawn,
            out ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Push(
                __instance is JobGiver_AIFightEnemy ||
                __instance is JobGiver_ReactToCloseMeleeThreat
                    ? pawn
                    : null,
                out __state);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Pop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTick))]
    public static class Patch_JobDriver_DriverTick_ArtificialMaid
    {
        [HarmonyPrefix]
        public static void Prefix(JobDriver __instance, out ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Push(GetCombatPawn(__instance), out __state);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Pop(__state);
            return __exception;
        }

        internal static Pawn GetCombatPawn(JobDriver driver)
        {
            JobDef jobDef = driver?.job?.def;
            return jobDef == JobDefOf.AttackMelee ||
                   jobDef == JobDefOf.AttackStatic ||
                   jobDef == JobDefOf.Wait_Combat
                ? driver.pawn
                : null;
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTickInterval))]
    public static class Patch_JobDriver_DriverTickInterval_ArtificialMaid
    {
        [HarmonyPrefix]
        public static void Prefix(JobDriver __instance, out ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Push(
                Patch_JobDriver_DriverTick_ArtificialMaid.GetCombatPawn(__instance),
                out __state);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ArtificialMaidInvisibilityBypass.State __state)
        {
            ArtificialMaidInvisibilityBypass.Pop(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GenUI), nameof(GenUI.TargetsAt))]
    public static class Patch_GenUI_TargetsAt_ArtificialMaid
    {
        [HarmonyPostfix]
        public static void Postfix(ITargetingSource source, ref IEnumerable<LocalTargetInfo> __result)
        {
            Pawn pawn = source?.Caster as Pawn;
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = ArtificialMaidInvisibilityBypass.EnumerateTargets(__result, pawn);
            }
        }
    }
}
