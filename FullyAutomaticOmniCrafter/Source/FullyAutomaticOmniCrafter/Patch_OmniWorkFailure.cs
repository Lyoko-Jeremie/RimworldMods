using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>在原版回收 Job 和同步搜索下一项工作之前记录结果。</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    internal static class Patch_OmniWorkFailure_End
    {
        [HarmonyPrefix]
        internal static void Prefix(Pawn ___pawn, JobCondition condition)
        {
            Job job = ___pawn.CurJob;
            if (job == null || MapComponent_OmniWorkstation.IsIdleJob(job)) return;
            OmniWorkFailureCache.For(___pawn)?.Ended(___pawn, job, condition);
        }
    }

    internal static class OmniWorkScannerMethods
    {
        internal static IEnumerable<MethodBase> Declared(string name, Type targetType)
        {
            Type[] signature = { typeof(Pawn), targetType, typeof(bool) };
            List<Type> types = GenTypes.AllTypes;
            for (int i = 0; i < types.Count; i++)
            {
                Type type = types[i];
                if (!typeof(WorkGiver_Scanner).IsAssignableFrom(type) || type.ContainsGenericParameters) continue;
                MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly, null, signature, null);
                if (method != null && !method.IsAbstract && method.ReturnType == typeof(bool)) yield return method;
            }
        }
    }

    // 必须覆盖派生类重写，单独 patch 基类不会拦住原版多数 WorkGiver。
    [HarmonyPatch]
    internal static class Patch_OmniWorkFailure_ThingCandidate
    {
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> TargetMethods() =>
            OmniWorkScannerMethods.Declared(nameof(WorkGiver_Scanner.HasJobOnThing), typeof(Thing));

        [HarmonyPrefix]
        internal static bool Prefix(WorkGiver_Scanner __instance, Pawn __0, Thing __1, ref bool __result)
        {
            OmniWorkFailureCache cache = OmniWorkFailureCache.For(__0);
            if (cache == null || cache.Allows(__0, __instance.def, __1)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_OmniWorkFailure_CellCandidate
    {
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> TargetMethods() =>
            OmniWorkScannerMethods.Declared(nameof(WorkGiver_Scanner.HasJobOnCell), typeof(IntVec3));

        [HarmonyPrefix]
        internal static bool Prefix(WorkGiver_Scanner __instance, Pawn __0, IntVec3 __1, ref bool __result)
        {
            OmniWorkFailureCache cache = OmniWorkFailureCache.For(__0);
            if (cache == null || cache.Allows(__0, __instance.def, __1)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(GenAI), nameof(GenAI.CanUseItemForWork))]
    internal static class Patch_OmniWorkFailure_QueuedResource
    {
        [HarmonyPostfix]
        internal static void Postfix(Pawn p, Thing item, ref bool __result)
        {
            if (__result && OmniWorkFailureCache.For(p) is OmniWorkFailureCache cache)
                __result = cache.Allows(p, null, item);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "IsNewValidNearbyNeeder")]
    internal static class Patch_OmniWorkFailure_QueuedDelivery
    {
        [HarmonyPostfix]
        internal static void Postfix(WorkGiver_ConstructDeliverResources __instance,
            Thing t, Pawn pawn, ref bool __result)
        {
            if (__result && OmniWorkFailureCache.For(pawn) is OmniWorkFailureCache cache)
                __result = cache.Allows(pawn, __instance.def, t);
        }
    }

    /// <summary>制作失败按账单隔离，同一工作台的其他账单仍可继续选料。</summary>
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients")]
    internal static class Patch_OmniWorkFailure_Bill
    {
        [HarmonyPrefix]
        internal static bool Prefix(Bill bill, Pawn pawn, Thing billGiver,
            List<ThingCount> chosen, ref bool __result)
        {
            OmniWorkFailureCache cache = OmniWorkFailureCache.For(pawn);
            if (cache == null || cache.AllowsBill(pawn, billGiver, bill)) return true;
            chosen.Clear();
            __result = false;
            return false;
        }
    }
}
