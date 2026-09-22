using System;
using System.Collections.Generic;

namespace Verse
{
    public struct IntVec3 : IEquatable<IntVec3>
    {
        public int x, y, z;
        public IntVec3(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public static IntVec3 Invalid => new IntVec3(-10000, -10000, -10000);
        public bool IsValid => this != Invalid;
        public bool InBounds(Map map) => x >= 0 && z >= 0 && x < map.Size && z < map.Size;
        public int LengthHorizontalSquared => x * x + z * z;
        public static IntVec3 operator -(IntVec3 a, IntVec3 b) => new IntVec3(a.x-b.x, 0, a.z-b.z);
        public static bool operator ==(IntVec3 a, IntVec3 b) => a.Equals(b);
        public static bool operator !=(IntVec3 a, IntVec3 b) => !a.Equals(b);
        public bool Equals(IntVec3 b) => x == b.x && y == b.y && z == b.z;
        public override bool Equals(object b) => b is IntVec3 c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(x,y,z);
    }
    public struct CellRect
    {
        public int minX, maxX, minZ, maxZ;
        public static CellRect SingleCell(IntVec3 c) => new CellRect { minX=c.x, maxX=c.x, minZ=c.z,maxZ=c.z };
        public CellRect ExpandedBy(int n) => new CellRect { minX=minX-n,maxX=maxX+n,minZ=minZ-n,maxZ=maxZ+n };
        public bool Contains(IntVec3 c) => c.x>=minX && c.x<=maxX && c.z>=minZ && c.z<=maxZ;
        public void ClipInsideMap(Map map) { minX=Math.Max(0,minX);minZ=Math.Max(0,minZ);maxX=Math.Min(map.Size-1,maxX);maxZ=Math.Min(map.Size-1,maxZ); }
        public IntVec3 ClosestCellTo(IntVec3 c) => new IntVec3(Math.Clamp(c.x,minX,maxX),0,Math.Clamp(c.z,minZ,maxZ));
    }
    public class ThingDef { public bool hasInteractionCell; }
    public class Thing
    {
        public Map MapHeld;
        public Map Map => Spawned ? MapHeld : null;
        public bool Spawned = true, Destroyed;
        public IntVec3 Position;
        public IntVec3 InteractionCell;
        public Thing Parent;
        public Thing SpawnedParentOrMe => Spawned ? this : Parent?.SpawnedParentOrMe;
        public ThingDef def = new ThingDef();
        public int width=1,height=1;
        public CellRect OccupiedRect() => new CellRect { minX=Position.x,minZ=Position.z,maxX=Position.x+width-1,maxZ=Position.z+height-1 };
    }
    public class Pawn : Thing
    {
        public int thingIDNumber;
        public bool Proxy=true;
        public AI.Job CurJob { get => jobs.curJob; set => jobs.curJob=value; }
        public AI.Pawn_JobTracker jobs;
        public AI.Pawn_PathFollower pather;
        public Pawn() { jobs=new AI.Pawn_JobTracker(this);pather=new AI.Pawn_PathFollower(this); }
        public void Notify_Teleported(bool endCurrentJob=true) { pather.StopDead(); }
        public bool Reserve(LocalTargetInfo cell,AI.Job job,bool errorOnFailed=true) {
            if(!Map.reservationManager.CanReserve(this,cell))return false;
            Map.reservationManager.reserved.Add(cell.Cell);return true;
        }
        public CarryTracker carryTracker = new CarryTracker();
    }
    public class CarryTracker { public Thing CarriedThing; }
    public struct LocalTargetInfo : IEquatable<LocalTargetInfo>
    {
        private Thing thing;
        private IntVec3 cell;
        private bool initialized;
        public LocalTargetInfo(IntVec3 cell) { this.cell=cell;thing=null;initialized=true; }
        public LocalTargetInfo(Thing thing) { this.thing=thing;cell=IntVec3.Invalid;initialized=thing!=null; }
        public Thing Thing => thing;
        public bool HasThing => thing!=null;
        public IntVec3 Cell => HasThing ? thing.Position : initialized ? cell : IntVec3.Invalid;
        public bool IsValid => HasThing || Cell.IsValid;
        public bool ThingDestroyed => HasThing && thing.Destroyed;
        public static implicit operator LocalTargetInfo(Thing t) => new LocalTargetInfo(t);
        public static implicit operator LocalTargetInfo(IntVec3 c) => new LocalTargetInfo(c);
        public bool Equals(LocalTargetInfo b) => HasThing || b.HasThing ? thing==b.thing : Cell==b.Cell;
        public override bool Equals(object b) => b is LocalTargetInfo t && Equals(t);
        public override int GetHashCode() => HasThing ? thing.GetHashCode() : Cell.GetHashCode();
        public static bool operator ==(LocalTargetInfo a, LocalTargetInfo b) => a.Equals(b);
        public static bool operator !=(LocalTargetInfo a, LocalTargetInfo b) => !a.Equals(b);
    }
    public class Map
    {
        public int Size=30;
        public Pathing pathing=new Pathing();
        public ReservationManager reservationManager=new ReservationManager();
        public PawnDestinationReservationManager pawnDestinationReservationManager=new PawnDestinationReservationManager();
        public FullyAutomaticOmniCrafter.MapComponent_OmniWorkstation manager=new FullyAutomaticOmniCrafter.MapComponent_OmniWorkstation();
        public T GetComponent<T>() => (T)(object)manager;
    }
    public class Pathing { public PathingContext context=new PathingContext();public PathingContext Get(object def)=>context; }
    public class PathingContext { public Map map;public PathGrid pathGrid=new PathGrid(); }
    public class PathGrid { public HashSet<IntVec3> blocked=new HashSet<IntVec3>();public bool Walkable(IntVec3 c)=>!blocked.Contains(c); }
    public class ReservationManager { public HashSet<IntVec3> reserved=new HashSet<IntVec3>();public bool CanReserve(Pawn pawn,LocalTargetInfo c)=>!reserved.Contains(c.Cell); }
    public static class GenTicks { public static int TicksGame; }
    public struct ThingCount { }
    public static class GenTypes { public static List<Type> AllTypes=>new List<Type>{typeof(RimWorld.WorkGiver_Scanner),typeof(RimWorld.TestScanner)}; }
}
namespace RimWorld
{
    public class WorkGiverDef { }
    public class Bill { }
    public class WorkGiver_DoBill { }
    public class WorkGiver_Scanner
    {
        public WorkGiverDef def=new WorkGiverDef();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public virtual bool HasJobOnThing(Verse.Pawn pawn,Verse.Thing t,bool forced=false)=>true;
        public virtual bool HasJobOnCell(Verse.Pawn pawn,Verse.IntVec3 c,bool forced=false)=>true;
    }
    public class TestScanner : WorkGiver_Scanner
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public override bool HasJobOnThing(Verse.Pawn worker,Verse.Thing thing,bool forced=false)=>true;
    }
    public class WorkGiver_ConstructDeliverResources : WorkGiver_Scanner { }
    public static class JobDefOf { public static object HaulToContainer=new object(); }
}
namespace Verse.AI
{
    public enum PathEndMode { None,OnCell,Touch,ClosestTouch,InteractionCell }
    public enum JobCondition { Succeeded,ErroredPather,Errored,Incompletable,InterruptForced }
    public class Job
    {
        public object def=new object();
        public RimWorld.WorkGiverDef workGiverDef;
        public LocalTargetInfo targetA,targetB,targetC;
        public int startTick;
        public RimWorld.Bill bill;
        public LocalTargetInfo GetTarget(TargetIndex ind)=>ind==TargetIndex.A?targetA:ind==TargetIndex.B?targetB:targetC;
    }
    public class PawnPath : IDisposable
    {
        private int curNodeIndex;
        private float totalCostInt;
        private bool inUse;
        public List<IntVec3> NodesReversed=new List<IntVec3>();
        public static PawnPath NotFound=>new PawnPath {totalCostInt=-1};
        public bool Found=>totalCostInt>=0;
        public int NodesLeftCount=>curNodeIndex+1;
        public IntVec3 Peek(int index)=>NodesReversed[curNodeIndex-index];
        public void AddNode(IntVec3 cell)=>NodesReversed.Add(cell);
        public void Dispose()=>NodesReversed.Clear();
    }
}
namespace FullyAutomaticOmniCrafter
{
    public static class OmniWorkstationDefOf { public static object FAOC_OmniWorkProxyPathGrid=new object(); }
    public static class OmniWorkProxyUtility { public static bool IsProxy(Verse.Pawn p)=>p!=null&&p.Proxy; }
    public class MapComponent_OmniWorkstation {
        internal readonly OmniWorkFailureCache WorkFailures=new OmniWorkFailureCache();
        internal static bool IsIdleJob(Verse.AI.Job job)=>job==null;
    }
    public class OmniWorkProxyPathGrid : Verse.PathGrid { }
}
