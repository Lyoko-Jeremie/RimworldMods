using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// “Do Something for Idle”的可选兼容层。
    /// </summary>
    internal static class DSFICompatibility
    {
        private const string PackageId = "gguake.ai.dsfi";
        private const string IdleJobDefNamePrefix = "IdleJob_";

        // RimWorld 1.6 的部分框架可能在工作线程运行思维逻辑，因此抑制状态必须按线程隔离。
        [ThreadStatic]
        private static Pawn pawnCheckingForRealWork;

        private static bool initialized;

        /// <summary>
        /// DSFI 是可选 Mod，因此运行时查找并补丁其类型，避免添加程序集依赖。
        /// </summary>
        public static void Initialize(Harmony harmony)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            Type idleNodeType = AccessTools.TypeByName("DSFI.ThinkNode_ColonistIdle");
            if (idleNodeType == null)
            {
                return;
            }

            MethodInfo target = AccessTools.Method(
                idleNodeType,
                nameof(ThinkNode.TryIssueJobPackage),
                new[] { typeof(Pawn), typeof(JobIssueParams) });
            if (target == null)
            {
                Log.Warning("[OuterrealmTechRobot] 检测到 Do Something for Idle，但未找到其空闲思维节点方法。");
                return;
            }

            HarmonyMethod prefix = new HarmonyMethod(
                typeof(DSFICompatibility),
                nameof(TryIssueJobPackagePrefix));
            harmony.Patch(target, prefix: prefix);
        }

        /// <summary>
        /// 判断当前工作是否由 DSFI 提供，且属于它的空闲行为。
        /// </summary>
        public static bool IsIdleJob(Job job)
        {
            JobDef def = job?.def;
            ModContentPack contentPack = def?.modContentPack;
            return contentPack != null &&
                   string.Equals(contentPack.PackageIdPlayerFacing, PackageId, StringComparison.OrdinalIgnoreCase) &&
                   def.defName != null &&
                   def.defName.StartsWith(IdleJobDefNamePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// 扫描真实工作时暂时屏蔽 DSFI 的空闲节点，防止空闲工作互相覆盖和反复重启。
        /// </summary>
        public static void CheckForRealJobOverride(Pawn pawn)
        {
            Pawn previousPawn = pawnCheckingForRealWork;
            pawnCheckingForRealWork = pawn;
            try
            {
                pawn.jobs.CheckForJobOverride();
            }
            finally
            {
                pawnCheckingForRealWork = previousPawn;
            }
        }

        private static bool TryIssueJobPackagePrefix(Pawn pawn, ref ThinkResult __result)
        {
            if (!ReferenceEquals(pawn, pawnCheckingForRealWork) ||
                pawn?.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return true;
            }

            __result = ThinkResult.NoJob;
            return false;
        }
    }
}
