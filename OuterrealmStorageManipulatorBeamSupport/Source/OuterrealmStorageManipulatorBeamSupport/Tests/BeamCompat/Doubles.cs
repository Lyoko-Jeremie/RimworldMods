using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using OuterrealmStorageManipulatorBeamSupport;

namespace Verse
{
    public struct IntVec3 : IEquatable<IntVec3>
    {
        public int x;
        public IntVec3(int x) { this.x = x; }
        public bool IsValid => x >= 0;
        public static readonly IntVec3 Invalid = new IntVec3(-1);
        public bool Equals(IntVec3 other) => x == other.x;
        public override bool Equals(object other) => other is IntVec3 v && Equals(v);
        public override int GetHashCode() => x;
        public static bool operator ==(IntVec3 a, IntVec3 b) => a.Equals(b);
        public static bool operator !=(IntVec3 a, IntVec3 b) => !a.Equals(b);
        public static IntVec3 operator -(IntVec3 a, IntVec3 b) => new IntVec3(a.x - b.x);
        public int LengthHorizontalSquared => x * x;
    }
    public class Thing
    {
        public int stackCount = 75;
        public int thingIDNumber;
        public bool Destroyed, Spawned = true, Stored, Forbidden;
        public object holdingOwner;
        public Map Map;
        public IntVec3 Position;
        public object Comp;
        public T TryGetComp<T>() where T : class => Comp as T;
        public T GetComp<T>() where T : class => Comp as T;
    }
    public class Pawn : Thing { }
    public class ThingOwner
    {
        public readonly List<Thing> Things = new List<Thing>();
        public virtual bool TryAdd(Thing thing, bool canMergeWithExistingStacks = true)
        {
            if (thing == null || thing.holdingOwner != null) return false;
            thing.Spawned = false;
            thing.holdingOwner = this;
            Things.Add(thing);
            return true;
        }
    }
    public class Map
    {
        public readonly Lister listerHaulables = new Lister();
        internal readonly Dictionary<IntVec3, Building_OuterrealmVault> Vaults = new Dictionary<IntVec3, Building_OuterrealmVault>();
        internal readonly Dictionary<IntVec3, RimWorld.SlotGroup> SlotGroups = new Dictionary<IntVec3, RimWorld.SlotGroup>();
    }
    public class Lister
    {
        public readonly List<Thing> Things = new List<Thing>();
        public ICollection<Thing> ThingsPotentiallyNeedingHauling() => Things;
    }
    public static class GridsUtility
    {
        public static List<Thing> GetThingList(this IntVec3 cell, Map map)
        {
            var result = new List<Thing>();
            for (int i = 0; i < map.listerHaulables.Things.Count; i++)
                if (map.listerHaulables.Things[i].Position == cell) result.Add(map.listerHaulables.Things[i]);
            return result;
        }
        public static RimWorld.SlotGroup GetSlotGroup(this IntVec3 cell, Map map)
            => map.SlotGroups.TryGetValue(cell, out RimWorld.SlotGroup group) ? group : null;
    }
    public static class UnityData { public static bool IsInMainThread = true; }
    public static class Log
    {
        public static void Message(string text) => Console.WriteLine(text);
        public static void Error(string text) => throw new Exception(text);
    }
}
namespace RimWorld
{
    public enum StoragePriority { Unstored, Low, Normal, Preferred, Important, Critical }
    public class StorageSettings { public StoragePriority Priority = StoragePriority.Normal; }
    public static class StoreUtility
    {
        public static StoragePriority CurrentStoragePriorityOf(Verse.Thing thing)
            => thing.Stored ? StoragePriority.Normal : StoragePriority.Unstored;
    }
    public class SlotGroup { public object parent; }
    public class CompTransporter { public readonly Verse.ThingOwner innerContainer = new Verse.ThingOwner(); }
}
namespace FullyAutomaticOmniCrafter
{
    using RimWorld;
    using Verse;
    public class Building_MatterEnergyConverter : Thing { }
}
namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    using RimWorld;
    using Verse;

    internal class OuterrealmEntry { public long Count; }
    internal enum OuterrealmRuntimeRegistrationKind { Projection, IdentityAnchor }
    internal class OuterrealmRuntimeRegistration
    {
        public bool Active = true;
        public Thing Thing;
        public OuterrealmRuntimeRegistrationKind Kind;
    }
    internal class View
    {
        public readonly List<Thing> InnerListForReading = new List<Thing>();
        public bool TryAdd(Thing thing, bool canMerge)
        {
            GameComponent_OuterrealmStorage.Instance.Deposits++;
            thing.Spawned = false;
            thing.holdingOwner = null;
            return true;
        }
    }
    internal class Building_OuterrealmVault : Thing
    {
        public readonly View view = new View();
        public readonly StorageSettings Settings = new StorageSettings();
        public bool Frozen, AllowTakeForUse, NoWithdraw, NoDeposit;
        public bool HaulSourceEnabled => Spawned && !Frozen && !NoWithdraw;
        public bool HaulDestinationEnabled => Spawned && !Frozen && !NoDeposit;
        public bool CanShow(Thing thing) => !Frozen;
        public bool Accepts(Thing thing) => CanShow(thing);
        public StorageSettings GetStoreSettings() => Settings;
    }
    internal struct OuterrealmSource
    {
        public Thing QueryThing;
        public OuterrealmEntry Entry;
        public Building_OuterrealmVault Vault;
        public bool IsVaultQuery => Vault != null;
    }
    internal class BillLedger { public bool IsTransferring(OuterrealmEntry entry) => false; }
    internal class RuntimeState
    {
        public readonly BillLedger Bills = new BillLedger();
        public readonly Dictionary<Building_OuterrealmVault, HashSet<OuterrealmRuntimeRegistration>> Registrations =
            new Dictionary<Building_OuterrealmVault, HashSet<OuterrealmRuntimeRegistration>>();
        public HashSet<OuterrealmRuntimeRegistration> RegistrationsFor(Building_OuterrealmVault vault)
            => Registrations.TryGetValue(vault, out var set) ? set : null;
    }

    /// <summary>主 mod 预留契约的替身：转发到测试内的 provider 列表。</summary>
    internal interface IOuterrealmExternalReservation
    {
        long ReservedFor(OuterrealmEntry entry);
        bool IsExtracting(OuterrealmEntry entry);
        bool HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry);
        long AdjustAvailable(Pawn caller, Thing query, long available);
        int AdjustRequest(Pawn caller, Thing query, int requested, long available);
        void ForgetVault(Building_OuterrealmVault vault);
        void ForgetMap(Map map);
    }

    internal static class OuterrealmExternalReservationRegistry
    {
        private static readonly List<IOuterrealmExternalReservation> providers = new List<IOuterrealmExternalReservation>();
        public static void Register(IOuterrealmExternalReservation provider) { if (provider != null) providers.Add(provider); }
        public static void Unregister(IOuterrealmExternalReservation provider) { providers.Remove(provider); }
        public static long ReservedTotal(OuterrealmEntry entry)
        {
            long total = 0; for (int i = 0; i < providers.Count; i++) total += providers[i].ReservedFor(entry); return total;
        }
        public static bool AnyReserved(OuterrealmEntry entry)
        {
            for (int i = 0; i < providers.Count; i++) if (providers[i].ReservedFor(entry) > 0) return true; return false;
        }
        public static bool AnyExtracting(OuterrealmEntry entry)
        {
            for (int i = 0; i < providers.Count; i++) if (providers[i].IsExtracting(entry)) return true; return false;
        }
        public static bool HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry)
        {
            for (int i = 0; i < providers.Count; i++) if (providers[i].HasForeignReservation(caller, query, entry)) return true; return false;
        }
        public static long AdjustAvailable(Pawn caller, Thing query, long available)
        {
            for (int i = 0; i < providers.Count; i++) available = providers[i].AdjustAvailable(caller, query, available); return available;
        }
        public static int AdjustRequest(Pawn caller, Thing query, int requested, long available)
        {
            for (int i = 0; i < providers.Count; i++) requested = providers[i].AdjustRequest(caller, query, requested, available); return requested;
        }
        public static void NotifyVaultRemoved(Building_OuterrealmVault vault)
        { for (int i = 0; i < providers.Count; i++) providers[i].ForgetVault(vault); }
        public static void NotifyMapRemoved(Map map)
        { for (int i = 0; i < providers.Count; i++) providers[i].ForgetMap(map); }
        public static void NotifyReservationChanged() { }
        public static void NotifyIdentityReservationReleased(Thing query) { }
    }

    internal static class OuterrealmVaultUtil
    {
        public static bool IsProjection(Thing thing) => thing.Stored;
        public static bool IsProtectedFromAutomaticDeposit(Thing thing) => false;
        public static bool IsVaultStoredThing(Thing thing) => thing.Stored;
    }

    internal class GameComponent_OuterrealmStorage
    {
        public static GameComponent_OuterrealmStorage Instance = new GameComponent_OuterrealmStorage();
        public readonly RuntimeState Runtime = new RuntimeState();
        public readonly Dictionary<Thing, OuterrealmSource> Sources = new Dictionary<Thing, OuterrealmSource>();
        public readonly List<Building_OuterrealmVault> VaultsForReading = new List<Building_OuterrealmVault>();
        public long PawnReserved;
        public int Deposits, Checkouts;
        public Action DuringCheckout;
        public bool HasVaultOnMap(Map map) => map.Vaults.Count > 0;
        public bool IsTransferring(OuterrealmEntry entry) => Runtime.Bills.IsTransferring(entry);
        public HashSet<OuterrealmRuntimeRegistration> RegistrationsFor(Building_OuterrealmVault vault)
            => Runtime.RegistrationsFor(vault);
        public long ReservedCountOf(OuterrealmEntry entry)
            => OuterrealmExternalReservationRegistry.AnyExtracting(entry) ? entry.Count
                : PawnReserved + OuterrealmExternalReservationRegistry.ReservedTotal(entry);
        public void Deposit(Thing thing, Building_OuterrealmVault home)
        {
            Deposits++;
            Sources[thing].Entry.Count += thing.stackCount;
            thing.holdingOwner = this;
        }
    }
    internal static class OuterrealmSourceResolver
    {
        public static bool TryResolve(Thing thing, out OuterrealmSource source)
            => GameComponent_OuterrealmStorage.Instance.Sources.TryGetValue(thing, out source) && source.Entry.Count > 0;
        public static Thing Checkout(in OuterrealmSource source, int count)
        {
            var storage = GameComponent_OuterrealmStorage.Instance;
            storage.Checkouts++;
            storage.DuringCheckout?.Invoke();
            source.Entry.Count -= count;
            Thing actual = new Thing { stackCount = count, Spawned = false, thingIDNumber = 999 };
            storage.Sources[actual] = source;
            return actual;
        }
    }
}

namespace OuterrealmStorageManipulatorBeamSupport
{
    using FullyAutomaticOmniCrafter.OuterrealmStorage;

    /// <summary>每局账本容器的替身：测试里用静态实例，模拟 Current 访问路径。</summary>
    internal class OuterrealmBeamSupportComponent
    {
        private static OuterrealmBeamSupportComponent current;
        internal readonly OuterrealmBeamLedger Ledger;

        private OuterrealmBeamSupportComponent()
        {
            Ledger = new OuterrealmBeamLedger(
                OuterrealmExternalReservationRegistry.NotifyReservationChanged,
                OuterrealmExternalReservationRegistry.NotifyIdentityReservationReleased);
            OuterrealmExternalReservationRegistry.Register(Ledger);
        }

        public static OuterrealmBeamSupportComponent Current => current ?? (current = new OuterrealmBeamSupportComponent());

        public static void Reset()
        {
            if (current != null) OuterrealmExternalReservationRegistry.Unregister(current.Ledger);
            current = null;
        }
    }
}

namespace ManipulatorBeam
{
    using Verse;
    using RimWorld;
    public interface IBeamOperator { Map Map { get; } Pawn Pawn { get; } int OwnerKey { get; } Building_BeamManipulator Manipulator { get; } }
    public class Operator : IBeamOperator
    {
        public Map Map { get; set; }
        public Pawn Pawn { get; set; }
        public int OwnerKey { get; set; }
        public Building_BeamManipulator Manipulator { get; set; } = new Building_BeamManipulator();
    }
    public class BeamTransfer
    {
        public Thing thing, destinationContainer;
        public int count;
        public bool isStripJob;
        public IntVec3 sourceCell, destination;
        public BeamTransfer(Thing thing, IntVec3 sourceCell, IntVec3 destination)
        { this.thing = thing; this.sourceCell = sourceCell; this.destination = destination; count = thing.stackCount; }
        public BeamTransfer(Thing thing, IntVec3 sourceCell, Thing destinationContainer, int count)
        { this.thing = thing; this.sourceCell = sourceCell; destination = IntVec3.Invalid; this.destinationContainer = destinationContainer; this.count = count; }
    }
    public static class BeamManipulatorUtility
    {
        public static bool RejectDestination, ThrowEnqueue, ThrowAfterEnqueue;
        public static int ShrinkTo;
        public static SlotGroup DestinationGroup = new SlotGroup();
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryFindStorageDestinationFor(IBeamOperator op, Thing thing, HashSet<IntVec3> excludedDestinations, int ownerKey, out IntVec3 destination)
        { destination = new IntVec3(99); return CanBeamTransferThing(op, thing, ownerKey) && IsBeamStorageGroupAllowed(DestinationGroup); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CanBeamTransferThing(IBeamOperator op, Thing thing, int ownerKey)
        {
            if (thing.Forbidden || !thing.Spawned) return false;
            if (op.Pawn != null && OuterrealmSourceResolver.TryResolve(thing, out var source))
            {
                long available = source.Entry.Count - GameComponent_OuterrealmStorage.Instance.ReservedCountOf(source.Entry);
                available = OuterrealmBeamAdapter.ReservationAvailable(op.Pawn, thing, available);
                int request = OuterrealmBeamAdapter.ReservationRequest(op.Pawn, thing, thing.stackCount, available);
                return request > 0 && request <= available;
            }
            return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryClaimAndEnqueue(BeamTransfer transfer, List<BeamTransfer> destinationQueue, HashSet<Thing> excludedThings, int ownerKey)
        {
            BeamClaimUtility.Claimed.Add(transfer);
            if (ThrowEnqueue) throw new InvalidOperationException("enqueue");
            if (RejectDestination || !BeamClaimUtility.TryClaimDestinationContainer(transfer, ownerKey)) return false;
            if (ShrinkTo > 0) transfer.count = ShrinkTo;
            destinationQueue.Add(transfer);
            excludedThings.Add(transfer.thing);
            if (ThrowAfterEnqueue) throw new InvalidOperationException("after enqueue");
            return true;
        }
        public static bool Enqueue(BeamTransfer transfer, int owner, List<BeamTransfer> queue, HashSet<Thing> excluded)
            => TryClaimAndEnqueue(transfer, queue, excluded, owner);
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool FinishTransfer(IBeamOperator op, Thing carriedThing, BeamTransfer transfer, IntVec3 fallbackCell)
        {
            op.Manipulator.ReleaseInTransitThing(carriedThing);
            try
            {
                if (transfer?.destinationContainer is Building_OuterrealmVault vault)
                {
                    vault.view.TryAdd(carriedThing, false);
                    return carriedThing.Destroyed || carriedThing.stackCount <= 0 || carriedThing.holdingOwner != null;
                }
                return false;
            }
            finally
            {
                op.Manipulator.ReturnInTransitThing(carriedThing);
            }
        }
        public static bool Finish(IBeamOperator op, Thing carriedThing, BeamTransfer transfer)
            => FinishTransfer(op, carriedThing, transfer, transfer.sourceCell);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsBeamStorageGroupAllowed(SlotGroup group) => true;
        public static bool GroupAllowed(SlotGroup group) => IsBeamStorageGroupAllowed(group);
    }
    public class Building_BeamManipulator
    {
        public bool ThrowOnAdd, FailOnAdd;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryLiftForTransfer(IBeamOperator op, BeamTransfer transfer, out Thing carriedThing)
        {
            carriedThing = null;
            if (!BeamManipulatorUtility.CanBeamTransferThing(op, transfer.thing, op.OwnerKey)) return false;
            Thing actual = ExtractThingForTransfer(transfer);
            if (actual == null) return false;
            if (ThrowOnAdd) throw new InvalidOperationException("container");
            if (FailOnAdd) return false;
            actual.Spawned = false;
            actual.holdingOwner = this;
            carriedThing = actual;
            return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Thing ExtractThingForTransfer(BeamTransfer transfer) => transfer.thing;
        public Thing Lift(IBeamOperator op, BeamTransfer transfer) => TryLiftForTransfer(op, transfer, out Thing thing) ? thing : null;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void ReleaseInTransitThing(Thing thing)
        {
            if (thing?.holdingOwner == this) thing.holdingOwner = null;
        }
        public void ReturnInTransitThing(Thing thing)
        {
            if (thing != null && !thing.Destroyed && !thing.Spawned && thing.holdingOwner == null)
                thing.holdingOwner = this;
        }
    }
    public static class BeamClaimUtility
    {
        public static readonly HashSet<BeamTransfer> Claimed = new HashSet<BeamTransfer>();
        public static readonly HashSet<int> ExclusiveContainers = new HashSet<int>();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryClaimDestinationContainer(BeamTransfer transfer, int ownerKey)
        {
            if (transfer?.destinationContainer == null) return true;
            return ExclusiveContainers.Add(transfer.destinationContainer.thingIDNumber);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReleaseClaim(BeamTransfer transfer, int ownerKey)
        {
            Claimed.Remove(transfer);
            if (transfer?.destinationContainer != null)
                ExclusiveContainers.Remove(transfer.destinationContainer.thingIDNumber);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReleaseAllClaimsForOwner(Map map, int ownerKey)
        {
            Claimed.Clear();
            ExclusiveContainers.Clear();
        }
    }
}
