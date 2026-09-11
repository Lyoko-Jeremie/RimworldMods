using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>移动请求保留到下一次 PatherTick，避免在 Toil.initAction 内递归推进工作链。</summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    internal static class Patch_OmniNavigation_Start
    {
        [HarmonyPrefix]
        internal static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn,
            LocalTargetInfo dest, PathEndMode peMode, ref LocalTargetInfo ___destination,
            ref PathEndMode ___peMode, ref bool ___moving)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            __instance.StopDead();
            ___destination = dest;
            ___peMode = peMode;
            ___moving = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
    internal static class Patch_OmniNavigation_Tick
    {
        private static readonly Action<Pawn_PathFollower> Arrived =
            AccessTools.MethodDelegate<Action<Pawn_PathFollower>>(AccessTools.Method(typeof(Pawn_PathFollower), "PatherArrived"));
        private static readonly Action<Pawn_PathFollower> Failed =
            AccessTools.MethodDelegate<Action<Pawn_PathFollower>>(AccessTools.Method(typeof(Pawn_PathFollower), "PatherFailed"));

        [HarmonyPrefix]
        internal static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn,
            LocalTargetInfo ___destination, PathEndMode ___peMode, ref int ___lastMovedTick)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            if (!__instance.Moving || !___pawn.Spawned) return false;
            if (!OmniWorkProxyNavigation.TryFindEnd(___pawn, ___pawn.Map, ___pawn.Position,
                ___destination, ___peMode, out IntVec3 end))
            {
                ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>().WorkFailures
                    .NavigationFailed(___pawn, ___destination);
                Failed(__instance);
                return false;
            }
            if (end != ___pawn.Position)
            {
                ___pawn.Map.pawnDestinationReservationManager.ObsoleteAllClaimedBy(___pawn);
                ___pawn.Position = end;
                ___lastMovedTick = GenTicks.TicksGame;
                ___pawn.Notify_Teleported(endCurrentJob: false);
            }
            // StopDead/Notify_Teleported 不代表 Toil 完成；必须显式走原版到达回调。
            Arrived(__instance);
            return false;
        }
    }

    /// <summary>外部调用 Reset 也不能重新进入原版异步寻路。</summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.ResetToCurrentPosition))]
    internal static class Patch_OmniNavigation_Reset
    {
        [HarmonyPrefix]
        internal static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            __instance.DisposeAndClearCurPathRequest();
            __instance.DisposeAndClearCurPath();
            __instance.nextCell = ___pawn.Position;
            __instance.nextCellCostLeft = 0;
            __instance.nextCellCostTotal = 1;
            return false;
        }
    }

    [HarmonyPatch(typeof(ReachabilityImmediate), nameof(ReachabilityImmediate.CanReachImmediate),
        new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(Map), typeof(PathEndMode), typeof(Pawn) })]
    internal static class Patch_OmniNavigation_Immediate
    {
        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 start, LocalTargetInfo target, Map map,
            PathEndMode peMode, Pawn pawn, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = OmniWorkProxyNavigation.IsAt(pawn, map, start, target, peMode);
            return false;
        }
    }

    [HarmonyPatch(typeof(GenPath), nameof(GenPath.ResolveClosestTouchPathMode))]
    internal static class Patch_OmniNavigation_ClosestTouch
    {
        [HarmonyPrefix]
        internal static bool Prefix(Pawn pawn, ref PathEndMode __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = PathEndMode.Touch;
            return false;
        }
    }

    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindGoodAdjacentSpotToTouch))]
    internal static class Patch_OmniNavigation_BuildSpot
    {
        [HarmonyPrefix]
        internal static bool Prefix(Pawn toucher, Thing touchee, ref IntVec3 result, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(toucher)) return true;
            __result = OmniWorkProxyNavigation.TryFindEnd(toucher, toucher.Map, toucher.Position,
                touchee, PathEndMode.Touch, out result, adjacentOnly: true, reserveCell: true);
            return false;
        }
    }

    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow), new Type[] {
        typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms),
        typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
    internal static class Patch_OmniNavigation_FindPathNow
    {
        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 start, LocalTargetInfo target, TraverseParms traverseParms,
            PathEndMode peMode, Map ___map, ref PawnPath __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(traverseParms.pawn)) return true;
            __result = OmniWorkProxyNavigation.FindPath(traverseParms.pawn, ___map, start, target, peMode);
            return false;
        }
    }

    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PushRequest))]
    internal static class Patch_OmniNavigation_PathRequest
    {
        private static readonly AccessTools.FieldRef<PathRequest, IntVec3?> ExactDestination =
            AccessTools.FieldRefAccess<PathRequest, IntVec3?>("exactDestination");

        [HarmonyPrefix]
        internal static bool Prefix(PathRequest request)
        {
            Pawn pawn = request.pawn ?? request.TraverseParms.pawn;
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            if (!request.Cancelled && !request.ResultIsReady)
            {
                IntVec3? exact = ExactDestination(request);
                // 精确落点不能丢失，也不能让它绕过原始目标的有效性检查。
                bool valid = OmniWorkProxyNavigation.TryFindEnd(pawn, request.map, request.Start,
                    request.Target, request.EndMode, out _);
                request.Resolve(valid ? OmniWorkProxyNavigation.FindPath(pawn, request.map, request.Start,
                    exact.HasValue ? new LocalTargetInfo(exact.Value) : request.Target,
                    exact.HasValue ? PathEndMode.OnCell : request.EndMode) : PawnPath.NotFound);
            }
            return false;
        }
    }

    /// <summary>直接调用角落判定的原版工作也必须遵循代理网格。</summary>
    [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.IsCornerTouchAllowed_NewTemp))]
    internal static class Patch_OmniNavigation_Corner
    {
        [HarmonyPrefix]
        internal static bool Prefix(IntVec3 adjCardinalX, IntVec3 adjCardinalZ,
            PathingContext pc, ref bool __result)
        {
            if (!(pc?.pathGrid is OmniWorkProxyPathGrid)) return true;
            __result = OmniWorkProxyNavigation.Walkable(pc.map, adjCardinalX) &&
                OmniWorkProxyNavigation.Walkable(pc.map, adjCardinalZ);
            return false;
        }
    }

    /// <summary>建造不回退到占用蓝图本体；落点在启动前预约，避免到达与预约使用不同条件。</summary>
    [HarmonyPatch(typeof(Toils_Goto), nameof(Toils_Goto.GotoBuild))]
    internal static class Patch_OmniNavigation_GotoBuild
    {
        [HarmonyPostfix]
        internal static void Postfix(TargetIndex ind, Toil __result)
        {
            Action original = __result.initAction;
            Toil toil = __result;
            toil.initAction = () =>
            {
                Pawn pawn = toil.actor;
                if (!OmniWorkProxyUtility.IsProxy(pawn)) { original(); return; }
                LocalTargetInfo target = pawn.CurJob.GetTarget(ind);
                if (!OmniWorkProxyNavigation.TryFindEnd(pawn, pawn.Map, pawn.Position, target,
                    PathEndMode.Touch, out IntVec3 end, adjacentOnly: true, reserveCell: true) ||
                    !pawn.Reserve(end, pawn.CurJob, errorOnFailed: false))
                {
                    pawn.Map.GetComponent<MapComponent_OmniWorkstation>().WorkFailures.NavigationFailed(pawn, target);
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                    return;
                }
                pawn.pather.StartPath(end, PathEndMode.OnCell);
            };
        }
    }
}
