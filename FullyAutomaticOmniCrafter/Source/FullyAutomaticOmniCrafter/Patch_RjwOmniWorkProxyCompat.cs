using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// RJW 没有按 PawnKind 完全退出的接口；这里集中识别其程序集，并为工作代理建立最终隔离边界。
    /// 所有反射只在补丁初始化时解析，未安装 RJW 时不会产生硬依赖。
    /// </summary>
    internal static class OmniWorkProxyRjwCompat
    {
        private const string RjwAssemblyName = "RJW";

        internal static readonly Type RjwUtilityType = AccessTools.TypeByName("rjw.xxx");
        internal static readonly Type RjwCompType = AccessTools.TypeByName("rjw.CompRJW");
        internal static readonly Type RjwSexChecksType =
            AccessTools.TypeByName("rjw.ThinkNode_ConditionalSexChecks");
        internal static readonly Assembly RjwAssembly = RjwUtilityType?.Assembly ?? RjwCompType?.Assembly;

        internal static MethodBase ResolveMethod(Type type, string name, params Type[] parameters)
        {
            return type == null ? null : AccessTools.Method(type, name, parameters);
        }

        internal static bool IsRjwType(Type type)
        {
            if (type == null) return false;
            Assembly assembly = RjwAssembly;
            if (assembly != null) return type.Assembly == assembly;
            return string.Equals(type.Assembly.GetName().Name, RjwAssemblyName,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsRjwJob(Job job, ThinkNode suppliedJobGiver)
        {
            if (job == null) return false;
            return IsRjwType(job.def?.driverClass) || IsRjwType(suppliedJobGiver?.GetType()) ||
                IsRjwType(job.jobGiver?.GetType()) ||
                IsRjwType(job.workGiverDef?.giverClass);
        }

        internal static bool ContainsProxyTarget(Job job)
        {
            if (job == null) return false;
            if (IsProxyTarget(job.targetA) || IsProxyTarget(job.targetB) || IsProxyTarget(job.targetC))
                return true;
            return ContainsProxyTarget(job.targetQueueA) || ContainsProxyTarget(job.targetQueueB);
        }

        private static bool ContainsProxyTarget(List<LocalTargetInfo> targets)
        {
            if (targets == null) return false;
            for (int i = 0; i < targets.Count; i++)
                if (IsProxyTarget(targets[i])) return true;
            return false;
        }

        private static bool IsProxyTarget(LocalTargetInfo target)
        {
            return target.HasThing && target.Thing is Pawn pawn && OmniWorkProxyUtility.IsProxy(pawn);
        }
    }

    /// <summary>代理永远不满足 RJW 的基础性行为资格，主动与被动候选检查都会在此失败。</summary>
    [HarmonyPatch]
    internal static class Patch_RJW_OmniWorkProxy_CannotDoLoving
    {
        private static readonly MethodBase Target = OmniWorkProxyRjwCompat.ResolveMethod(
            OmniWorkProxyRjwCompat.RjwUtilityType, "can_do_loving", new[] { typeof(Pawn) });

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;

        [HarmonyPrefix]
        private static bool Prefix(Pawn __0, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__0)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>在 RJW 思考树最外层直接拒绝代理，避免任何后续条件读取已清空的第三方需求。</summary>
    [HarmonyPatch]
    internal static class Patch_RJW_OmniWorkProxy_SkipThinkTree
    {
        private static readonly MethodBase Target = OmniWorkProxyRjwCompat.ResolveMethod(
            OmniWorkProxyRjwCompat.RjwSexChecksType, "Satisfied", new[] { typeof(Pawn) });

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;

        [HarmonyPrefix]
        private static bool Prefix(Pawn __0, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__0)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>RJW 仍会把 CompRJW 注入 Human Def；阻止代理创建、保存或 Tick 任何 RJW 状态。</summary>
    [HarmonyPatch]
    internal static class Patch_RJW_OmniWorkProxy_NoCompState
    {
        private static bool Prepare() => OmniWorkProxyRjwCompat.RjwCompType != null;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = OmniWorkProxyRjwCompat.RjwCompType;
            MethodBase method = OmniWorkProxyRjwCompat.ResolveMethod(type, "Sexualize", typeof(bool));
            if (method != null) yield return method;
            method = OmniWorkProxyRjwCompat.ResolveMethod(type, nameof(ThingComp.PostExposeData));
            if (method != null) yield return method;
            method = OmniWorkProxyRjwCompat.ResolveMethod(type, nameof(ThingComp.CompTickInterval), typeof(int));
            if (method != null) yield return method;
        }

        [HarmonyPrefix]
        private static bool Prefix(ThingComp __instance)
        {
            return !OmniWorkProxyUtility.IsProxy(__instance?.parent as Pawn);
        }
    }

    /// <summary>原版工作扫描不得把 RJW WorkGiver 派给万能工作站代理。</summary>
    [HarmonyPatch(typeof(JobGiver_Work), "PawnCanUseWorkGiver")]
    internal static class Patch_RJW_OmniWorkProxy_FilterWorkGiver
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, WorkGiver giver, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn) ||
                !OmniWorkProxyRjwCompat.IsRjwType(giver?.GetType())) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 最终防线：拒绝代理执行 RJW Job，也拒绝任何 RJW Job 把代理作为直接或队列目标。
    /// 正常候选检查应更早排除代理，此处只处理第三方强制派工等绕过路径。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_RJW_OmniWorkProxy_BlockJob
    {
        private static bool Prepare() => OmniWorkProxyRjwCompat.RjwAssembly != null;

        [HarmonyPrefix]
        private static bool Prefix(Pawn ___pawn, Job newJob, ThinkNode jobGiver)
        {
            if (!OmniWorkProxyRjwCompat.IsRjwJob(newJob, jobGiver) ||
                !OmniWorkProxyUtility.IsProxy(___pawn) &&
                !OmniWorkProxyRjwCompat.ContainsProxyTarget(newJob)) return true;
            ___pawn?.ClearReservationsForJob(newJob);
            return false;
        }
    }
}
