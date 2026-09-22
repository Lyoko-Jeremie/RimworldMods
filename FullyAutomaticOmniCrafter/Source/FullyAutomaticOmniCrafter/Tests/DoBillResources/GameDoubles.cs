// 仅供无游戏进程的控制流测试。它们不是原版实现，Harmony/寻路/存档仍需游戏内回归。
using System;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using Verse;
using Verse.AI;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string n) { } }
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int p) { } }
    public static class Priority { public const int Last = 0; }
}
namespace UnityEngine { public static class Mathf { public static int CeilToInt(float f) => (int)Math.Ceiling(f); } }
namespace Verse.Sound { public static class SoundExtensions { public static void PlayOneShot(this SoundDef sound, TargetInfo target) { } } }
namespace RimWorld
{
    public class WorkGiver_DoBill { }
    public static class JobDefOf { public static readonly JobDef DoBill = new JobDef(); public static readonly JobDef EnterBiosculpterPod = new JobDef(); public static readonly JobDef Reload = new JobDef(); public static readonly JobDef RefuelAtomic = new JobDef(); }
    public class TransferableUtility { }
    public enum QualityCategory { Awful, Legendary }
    public interface ISlotGroup { IEnumerable<Thing> HeldThings { get; } }
    public interface IHaulSource { ThingOwner GetDirectlyHeldThings(); }
    public class Apparel : ThingWithComps { }
    public static class RecordDefOf { public static readonly object ThingsHauled = new object(); }
}
namespace Verse
{
    public interface IThingHolder { }
    public struct IntVec3
    {
        public int x;
        public int LengthHorizontalSquared => x * x;
        public static IntVec3 operator -(IntVec3 a, IntVec3 b) => new IntVec3 { x = a.x - b.x };
    }
    public enum Danger { Some }
    public class SoundDef { }
    public struct TargetInfo { public TargetInfo(IntVec3 position, Map map) { } }
    public class ThingDef
    {
        public string defName;
        public int stackLimit = 75;
        public bool hasInteractionCell;
        public bool CountAsResource, IsApparel, Minifiable;
        public ApparelProperties apparel = new ApparelProperties();
        public SoundDef soundPickup = new SoundDef();
    }
    public class Thing
    {
        public ThingDef def;
        public int stackCount;
        public bool Destroyed, Spawned, Forbidden;
        public object holdingOwner;
        public IThingHolder ParentHolder => holdingOwner as IThingHolder;
        public Thing SplitOff(int count)
        {
            if (OuterrealmSourceResolver.TryResolve(this, out var source)) return OuterrealmSourceResolver.Checkout(source, count);
            count = Math.Min(count, stackCount);
            if (count == stackCount) return this;
            stackCount -= count;
            return new Thing { def = def, stackCount = count };
        }
        public Map Map;
        public IntVec3 Position, InteractionCell;
        public IntVec3 PositionHeld => Position;
        public string ThingID => def?.defName ?? "pawn";
        public bool IsForbidden(Pawn pawn) => Forbidden;
        public bool CanStackWith(Thing other) => other != null && other.def == def;
        public void DeSpawn() { Spawned = false; Map = null; }
    }
    public class ApparelProperties { public bool careIfWornByCorpse; }
    public class ThingWithComps : Thing { }
    public class MinifiedThing : Thing { public Thing InnerThing; }
    public class ThingOwner : List<Thing> { }
    public class FakeHolder : RimWorld.IHaulSource, RimWorld.ISlotGroup
    {
        public readonly ThingOwner Things = new ThingOwner();
        public ThingOwner GetDirectlyHeldThings() => Things;
        public IEnumerable<Thing> HeldThings => Things;
    }
    public class Faction { public static readonly Faction OfPlayer = new Faction(); }
    public enum ThingRequestGroup { MinifiedThing }
    public class FakeLister
    {
        public readonly List<Thing> Items = new List<Thing>();
        public List<Thing> ThingsOfDef(ThingDef def) => Items.FindAll(t => t.def == def);
        public List<Thing> ThingsInGroup(ThingRequestGroup group) => Items.FindAll(t => t is MinifiedThing);
    }
    public class FakeMapPawns
    {
        public readonly List<Pawn> FreeColonistsSpawned = new List<Pawn>();
        public List<Pawn> SpawnedPawnsInFaction(Faction faction) => FreeColonistsSpawned;
    }
    public class FakeHaulManager { public readonly List<RimWorld.IHaulSource> AllHaulSourcesListForReading = new List<RimWorld.IHaulSource>(); }
    public class FakeEquipment { public readonly List<ThingWithComps> AllEquipmentListForReading = new List<ThingWithComps>(); }
    public class FakeApparel { public readonly List<RimWorld.Apparel> WornApparel = new List<RimWorld.Apparel>(); }
    public class ThingDefCountClass { public ThingDef thingDef; }
    public class FloatRange { public float min, max = 1; }
    public class QualityRange { public RimWorld.QualityCategory min, max = RimWorld.QualityCategory.Legendary; }
    public class Bill_Production : Bill
    {
        public Map Map;
        public bool includeEquipped, includeTainted, limitToAllowedStuff;
        public FloatRange hpRange = new FloatRange(); public QualityRange qualityRange = new QualityRange();
        public RimWorld.ISlotGroup Slot;
        public RimWorld.ISlotGroup GetIncludeSlotGroup() => Slot;
    }
    public class RecipeWorkerCounter
    {
        public RecipeDef recipe = new RecipeDef();
        public Func<Thing, bool> Filter = t => true;
        public bool CountValidThing(Thing t, Bill_Production bill, ThingDef def) => t.def == def && Filter(t);
    }
    public class ThingFilter
    {
        public readonly HashSet<ThingDef> Allowed = new HashSet<ThingDef>();
        public bool Allows(ThingDef def) => Allowed.Contains(def);
        public bool Allows(Thing thing) => Allows(thing.def);
    }
    public class IngredientCount
    {
        public ThingFilter filter = new ThingFilter();
        public bool IsFixedIngredient = true;
        public float Count;
        public float GetBaseCount() => Count;
        public int CountRequiredOfFor(ThingDef def, RecipeDef recipe, Bill bill) => (int)Math.Ceiling(Count / recipe.IngredientValueGetter.ValuePerUnitOf(def));
    }
    public class IngredientValueGetter
    {
        public readonly Dictionary<ThingDef, float> Values = new Dictionary<ThingDef, float>();
        public float ValuePerUnitOf(ThingDef def) => Values.TryGetValue(def, out float value) ? value : 1f;
    }
    public class RecipeDef
    {
        public string defName = "test-recipe";
        public bool allowMixingIngredients, ignoreIngredientCountTakeEntireStacks;
        public readonly IngredientValueGetter IngredientValueGetter = new IngredientValueGetter();
        public readonly List<IngredientCount> ingredients = new List<IngredientCount>();
        public readonly List<ThingDefCountClass> products = new List<ThingDefCountClass>();
    }
    public class Bill { public RecipeDef recipe = new RecipeDef(); public ThingFilter ingredientFilter = new ThingFilter(); }
    public struct ThingCount
    {
        public Thing Thing; public int Count;
        public ThingCount(Thing thing, int count, bool ignoreStackLimit = false)
        {
            Thing = thing;
            Count = Math.Max(0, count);
            if (!ignoreStackLimit && Count > thing.stackCount) Count = thing.stackCount;
        }
    }
    public static class ThingCountUtility
    {
        public static void AddToList(List<ThingCount> list, Thing thing, int count)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Thing == thing) { list[i] = new ThingCount(thing, list[i].Count + count); return; }
            list.Add(new ThingCount(thing, count));
        }
    }
    public static class Extensions
    {
        public static bool NullOrEmpty<T>(this List<T> list) => list == null || list.Count == 0;
        public static Thing GetInnerIfMinified(this Thing t) => t is MinifiedThing m ? m.InnerThing : t;
    }
    public class Pawn : Thing
    {
        public bool Dead, Downed, AutoTake = true;
        public readonly Pawn_JobTracker jobs;
        public readonly Pawn_CarryTracker carryTracker;
        public readonly Records records = new Records();
        public readonly FakeEquipment equipment = new FakeEquipment();
        public readonly FakeApparel apparel = new FakeApparel();
        public readonly FakeHolder inventory = new FakeHolder();
        public Pawn(Map map) { Map = map; Spawned = true; jobs = new Pawn_JobTracker(this); carryTracker = new Pawn_CarryTracker(this); }
        public Job CurJob => jobs.curJob;
        public bool CanReach(Thing thing, PathEndMode mode, Danger danger) => !thing.Forbidden;
        public bool Reserve(LocalTargetInfo target, Job job, int maxPawns = 1, int stackCount = -1, bool errorOnFailed = true)
            => Map.reservationManager.Reserve(this, target.Thing, job, stackCount);
        public bool ReserveSittableOrSpot(IntVec3 cell, Job job, bool error) => true;
    }
    public class Records { public void Increment(object def) { } }
    public class ResourceCounter { public void UpdateResourceCounts() { } }
    public class Map
    {
        public readonly ReservationManager reservationManager = new ReservationManager(); public readonly ResourceCounter resourceCounter = new ResourceCounter();
        public readonly FakeLister listerThings = new FakeLister(); public readonly FakeMapPawns mapPawns = new FakeMapPawns();
        public readonly FakeHaulManager haulDestinationManager = new FakeHaulManager();
    }
    public class TickManager { public int TicksGame; }
    public static class Find { public static TickManager TickManager = new TickManager(); }
    public static class Prefs { public static bool DevMode; }
    public static class Log { public static void Warning(string text) { } public static void Error(string text) { } }
    public static class GenSpawn { public static void Spawn(Thing t, IntVec3 p, Map map) { t.Spawned = true; t.Map = map; t.Position = p; } }
    public enum Acceptance { All, None, Partial, ThrowBefore, ThrowAfter }
    public class CarryContainer
    {
        private readonly Pawn_CarryTracker carry;
        public Acceptance Mode;
        public Action Callback;
        public CarryContainer(Pawn_CarryTracker tracker) { carry = tracker; }
        public bool TryAdd(Thing item, bool merge)
        {
            if (Mode == Acceptance.ThrowBefore) throw new InvalidOperationException("before delivery");
            if (Mode == Acceptance.None) return false;
            int count = Mode == Acceptance.Partial ? item.stackCount / 2 : item.stackCount;
            if (carry.CarriedThing != null) carry.CarriedThing.stackCount += count;
            else if (count == item.stackCount) { carry.CarriedThing = item; item.holdingOwner = this; }
            else carry.CarriedThing = new Thing { def = item.def, stackCount = count, holdingOwner = this };
            if (carry.CarriedThing != item) { item.stackCount -= count; item.Destroyed = item.stackCount == 0; }
            Callback?.Invoke();
            if (Mode == Acceptance.ThrowAfter) throw new InvalidOperationException("after delivery");
            return Mode != Acceptance.Partial;
        }
    }
    public class Pawn_CarryTracker
    {
        public Pawn pawn;
        public Thing CarriedThing;
        public int Capacity = 60;
        public CarryContainer innerContainer;
        public Pawn_CarryTracker(Pawn owner) { pawn = owner; innerContainer = new CarryContainer(this); }
        public int AvailableStackSpace(ThingDef def) => Capacity - (CarriedThing?.stackCount ?? 0);
        public int TryStartCarry(Thing t, int count, bool reserve)
        {
            if (OuterrealmSourceResolver.TryResolve(t, out OuterrealmSource source))
                return OuterrealmBillJobUtility.CarryFromProjection(this, source, count, reserve);
            Thing actual = t.SplitOff(Math.Min(count, AvailableStackSpace(t.def)));
            actual.DeSpawn();
            int before = CarriedThing?.stackCount ?? 0;
            innerContainer.TryAdd(actual, true);
            return (CarriedThing?.stackCount ?? 0) - before;
        }
    }
}
namespace Verse.AI
{
    public enum TargetIndex { None, A, B, C }
    public enum PathEndMode { ClosestTouch }
    public enum JobCondition { Incompletable, Errored, Succeeded }
    public class JobDef { }
    public struct LocalTargetInfo
    {
        public Thing Thing;
        public static implicit operator LocalTargetInfo(Thing t) => new LocalTargetInfo { Thing = t };
    }
    public class Job
    {
        public JobDef def = RimWorld.JobDefOf.DoBill;
        public Bill bill = new Bill();
        public int count;
        public LocalTargetInfo targetA, targetB, targetC;
        public List<LocalTargetInfo> targetQueueB = new List<LocalTargetInfo>();
        public List<int> countQueue = new List<int>();
        public LocalTargetInfo GetTarget(TargetIndex i) => i == TargetIndex.A ? targetA : i == TargetIndex.B ? targetB : targetC;
        public void SetTarget(TargetIndex i, LocalTargetInfo value) { if (i == TargetIndex.A) targetA = value; else if (i == TargetIndex.B) targetB = value; else targetC = value; }
        public List<LocalTargetInfo> GetTargetQueue(TargetIndex i) => targetQueueB;
    }
    public class Toil { public Pawn actor; public Action initAction; }
    public class Toils_JobTransforms { }
    public class JobDriver { public Job job; public Pawn pawn; }
    public class JobDriver_DoBill : JobDriver { }
    public class JobDriver_EnterBiosculpterPod : JobDriver { }
    public class Pawn_JobTracker
    {
        private readonly Pawn pawn;
        public Job curJob;
        public Pawn_JobTracker(Pawn owner) { pawn = owner; }
        public void EndCurrentJob(JobCondition condition) { pawn.Map.reservationManager.ReleaseClaimedBy(pawn, curJob); curJob = null; }
    }
    public class ReservationManager
    {
        public sealed class Reservation { public Pawn Claimant; public Job Job; public LocalTargetInfo Target; public int StackCount; }
        public readonly List<Reservation> ReservationsReadOnly = new List<Reservation>();
        public bool Reserve(Pawn pawn, Thing target, Job job, int count)
        {
            if (target == null) return false;
            if (OuterrealmSourceResolver.TryResolve(target, out OuterrealmSource source)
                && count > OuterrealmBillJobUtility.Available(source, pawn, job)) return false;
            for (int i = 0; i < ReservationsReadOnly.Count; i++)
                if (ReservationsReadOnly[i].Job == job && ReservationsReadOnly[i].Target.Thing == target) return true;
            ReservationsReadOnly.Add(new Reservation { Claimant = pawn, Job = job, Target = target, StackCount = count });
            return true;
        }
        public void ReleaseClaimedBy(Pawn pawn, Job job)
        {
            OuterrealmBillJobUtility.Ledger.Release(job);
            ReservationsReadOnly.RemoveAll(r => r.Job == job && r.Claimant == pawn);
            SubspaceAccessUtility.ReturnUnreservedPending();
        }
    }
}
namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    internal static class OuterrealmTradeSourceRegistry
    {
        public static readonly Dictionary<Thing, OuterrealmEntry> Entries = new Dictionary<Thing, OuterrealmEntry>();
        public static bool TryGetEntry(Thing t, out OuterrealmEntry entry) => Entries.TryGetValue(t, out entry);
    }
    internal class OuterrealmEntry { public long Count; public ThingDef Def; }
    internal class Building_OuterrealmVault : Thing
    {
        public FakeTradeView view = new FakeTradeView();
        public bool Frozen, NoWithdraw, AllowTakeForUse;
        public bool CanShow(Thing t) => !Frozen;
    }
    internal enum OuterrealmSourceKind { Projection, IdentityAnchor, SubspaceCanonical }
    internal struct OuterrealmSource
    {
        public Thing QueryThing; public OuterrealmEntry Entry; public Building_OuterrealmVault Vault; public OuterrealmSourceKind Kind;
        public bool IsVaultQuery => Kind != OuterrealmSourceKind.SubspaceCanonical;
    }
    internal static class OuterrealmSourceResolver
    {
        public static readonly Dictionary<Thing, OuterrealmSource> Sources = new Dictionary<Thing, OuterrealmSource>();
        public static readonly HashSet<Thing> ProjectionMarkers = new HashSet<Thing>();
        public static Action BeforeCheckout;
        public static bool TryResolve(Thing t, out OuterrealmSource source)
        {
            source = default;
            return t != null && Sources.TryGetValue(t, out source) && source.Entry.Count > 0;
        }
        public static Thing Checkout(OuterrealmSource source, int count)
        {
            BeforeCheckout?.Invoke();
            int take = (int)Math.Min(count, source.Entry.Count);
            source.Entry.Count -= take;
            if (take == 0) return null;
            Thing result = new Thing { def = source.Entry.Def, stackCount = take };
            if (source.Kind == OuterrealmSourceKind.SubspaceCanonical && source.Entry.Count == 0)
            {
                result = source.QueryThing;
                result.stackCount = take;
                Sources.Remove(result);
            }
            GameComponent_OuterrealmStorage.Instance.ReturnRoutes[result] = source.Entry;
            return result;
        }
    }
    internal static class OuterrealmVaultUtil { public static bool IsProjection(Thing t) => t != null && OuterrealmSourceResolver.ProjectionMarkers.Contains(t); }
    internal class RuntimeState { public readonly OuterrealmBillResourceLedger Bills = new OuterrealmBillResourceLedger(); }
    internal class GameComponent_OuterrealmStorage
    {
        public readonly List<Building_OuterrealmVault> VaultsForReading = new List<Building_OuterrealmVault>();
        public int WithdrawalLimit = int.MaxValue;
        public int WithdrawalsBeforeFailure = int.MaxValue;
        public Thing Withdraw(OuterrealmEntry entry, int count)
        {
            if (WithdrawalsBeforeFailure-- <= 0) return null;
            return OuterrealmSourceResolver.Checkout(new OuterrealmSource { Entry = entry }, Math.Min(count, WithdrawalLimit));
        }
        public static GameComponent_OuterrealmStorage Instance;
        public readonly RuntimeState Runtime = new RuntimeState();
        public readonly List<Map> Maps = new List<Map>();
        public readonly Dictionary<Thing, OuterrealmEntry> ReturnRoutes = new Dictionary<Thing, OuterrealmEntry>();
        public void NotifyReservationChanged() { }
        public bool HasVaultOnMap(Map map) => true;
        public long ReservedCountOf(OuterrealmEntry entry)
        {
            if (Runtime.Bills.IsTransferring(entry)) return entry.Count;
            var result = new Dictionary<OuterrealmEntry, long>();
            Runtime.Bills.AddTotalsTo(result);
            long count = result.TryGetValue(entry, out long value) ? value : 0;
            foreach (Map map in Maps)
                foreach (ReservationManager.Reservation reservation in map.reservationManager.ReservationsReadOnly)
                    if (!Runtime.Bills.IsBridge(reservation.Job, reservation.Target.Thing)
                        && OuterrealmSourceResolver.TryResolve(reservation.Target.Thing, out OuterrealmSource source) && source.Entry == entry)
                        count += reservation.StackCount < 0 ? Math.Min(entry.Count, source.QueryThing.def.stackLimit) : reservation.StackCount;
            return count;
        }
        public OuterrealmEntry Deposit(Thing t) { var entry = ReturnRoutes[t]; entry.Count += t.stackCount; t.Destroyed = true; t.stackCount = 0; return entry; }
    }
    internal static class SubspaceAccessUtility
    {
        public static readonly HashSet<Thing> Pending = new HashSet<Thing>();
        public static bool CanAutoTake(Pawn pawn) => pawn.AutoTake;
        public static void MarkPendingCheckout(Thing t) => Pending.Add(t);
        public static void ReturnUnreservedPending()
        {
            foreach (Thing t in new List<Thing>(Pending))
            {
                if (t.Destroyed || t.holdingOwner != null) { Pending.Remove(t); continue; }
                bool reserved = false;
                foreach (Map map in GameComponent_OuterrealmStorage.Instance.Maps)
                    foreach (ReservationManager.Reservation reservation in map.reservationManager.ReservationsReadOnly)
                        reserved |= reservation.Target.Thing == t;
                if (reserved) continue;
                Pending.Remove(t);
                t.DeSpawn();
                GameComponent_OuterrealmStorage.Instance.Deposit(t);
            }
        }
    }
}
