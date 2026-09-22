using System;
using System.Runtime.CompilerServices;
using Verse.AI;

namespace Verse
{
    public class PawnDestinationReservationManager { public void ObsoleteAllClaimedBy(Pawn pawn) { } }
    public struct PathFinderCostTuning { }
    public struct TraverseParms { public Pawn pawn; }
    public class PathRequest
    {
        private IntVec3? exactDestination;
        public Pawn pawn;
        public TraverseParms TraverseParms;
        public bool Cancelled,ResultIsReady;
        public Map map;
        public IntVec3 Start;
        public LocalTargetInfo Target;
        public PathEndMode EndMode;
        public void Resolve(PawnPath path) { ResultIsReady=true; }
        public interface IPathGridCustomizer { }
    }
    public class PathFinder
    {
        private Map map;
        public PawnPath FindPathNow(IntVec3 start,LocalTargetInfo target,TraverseParms traverseParms,
            PathFinderCostTuning? tuning,PathEndMode peMode,PathRequest.IPathGridCustomizer customizer)=>throw new Exception("vanilla path");
        public void PushRequest(PathRequest request)=>throw new Exception("vanilla queue");
    }
    public static class ReachabilityImmediate
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CanReachImmediate(IntVec3 start,LocalTargetInfo target,Map map,PathEndMode peMode,Pawn pawn)=>false;
    }
}
namespace Verse.AI
{
    public enum TargetIndex { A,B,C }
    public class Toil { public Action initAction;public Pawn actor; }
    public static class Toils_Goto { public static Toil GotoBuild(TargetIndex ind)=>new Toil {initAction=()=>{}}; }
    public static class GenPath { public static PathEndMode ResolveClosestTouchPathMode(Pawn pawn)=>PathEndMode.OnCell; }
    public static class GenAI { public static bool CanUseItemForWork(Pawn p,Thing item)=>true; }
    public static class TouchPathEndModeUtility {
        public static bool IsCornerTouchAllowed_NewTemp(IntVec3 adjCardinalX,IntVec3 adjCardinalZ,PathingContext pc)=>false;
    }
    public class Pawn_JobTracker
    {
        public Pawn pawn;
        public Job curJob;
        public int failures,arrivals;
        public Action onArrival;
        public Pawn_JobTracker(Pawn p) { pawn=p; }
        public void EndCurrentJob(JobCondition condition) { failures++;curJob=null; }
    }
    public class Pawn_PathFollower
    {
        private Pawn pawn;
        private bool moving;
        private LocalTargetInfo destination;
        private PathEndMode peMode;
        private int lastMovedTick;
        public IntVec3 nextCell;
        public float nextCellCostLeft,nextCellCostTotal;
        public bool Moving=>moving;
        public Pawn_PathFollower(Pawn pawn) { this.pawn=pawn; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StartPath(LocalTargetInfo dest,PathEndMode peMode)=>throw new Exception("vanilla region preflight");
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PatherTick()=>throw new Exception("vanilla async worker");
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ResetToCurrentPosition()=>throw new Exception("vanilla reset request");
        public void StopDead() { moving=false; }
        public void DisposeAndClearCurPathRequest() { }
        public void DisposeAndClearCurPath() { }
        private void PatherArrived() { StopDead();pawn.jobs.arrivals++;pawn.jobs.onArrival?.Invoke(); }
        private void PatherFailed() { StopDead();pawn.jobs.EndCurrentJob(JobCondition.ErroredPather); }
    }
}
namespace RimWorld
{
    public static class RCellFinder {
        public static bool TryFindGoodAdjacentSpotToTouch(Verse.Pawn toucher,Verse.Thing touchee,out Verse.IntVec3 result) { result=Verse.IntVec3.Invalid;return false; }
    }
}
