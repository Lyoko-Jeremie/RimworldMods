using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>新版光束协议适配器。仅边界补丁调用此类，不编译期引用第三方程序集。</summary>
    internal static class OuterrealmBeamAdapter
    {
        private const string PatchId = "Jeremie.Fully.Automatic.OmniCrafter.BeamOperator";
        private const int CandidateWindow = 64;
        private delegate bool FindDestination(object op, Thing thing, HashSet<IntVec3> excluded, int owner, out IntVec3 destination);
        private delegate bool CanTransfer(object op, Thing thing, int owner);
        private static FindDestination findDestination;
        private static CanTransfer canTransfer;
        private static Func<object, Map> mapOf;
        private static Func<object, Pawn> pawnOf;
        private static Func<object, int> ownerOf;
        private static Func<object, Thing> thingOf, containerOf;
        private static Func<object, IntVec3> destinationOf;
        private static Func<object, int> countOf;
        private static Func<object, bool> stripOf;
        private static Action<object, int> setCount;
        private static Func<object, IList> transfersOf;
        private static Func<IntVec3, object> newBatch;
        private static Func<Thing, IntVec3, IntVec3, object> newTransfer;
        private static Func<Thing, IntVec3, Thing, int, object> newContainerTransfer;
        private static Action<object, int> releaseClaim;
        private static volatile bool enabled;
        internal static bool IsInstalled => enabled;

        // 查询上下文只影响当前线程、当前物品和当前 Pawn，异常时由 Finalizer 恢复。
        internal struct QueryScope
        {
            public Thing Thing;
            public Pawn Pawn;
            public OuterrealmEntry Entry;
            public long Own;
        }
        [ThreadStatic] private static QueryScope query;
        [ThreadStatic] private static bool vaultStorageSearch;
        [ThreadStatic] private static LiftState lift;

        internal sealed class LiftState
        {
            public LiftState Previous;
            public OuterrealmBeamLease Lease;
            public GameComponent_OuterrealmStorage Storage;
            public Thing Actual;
        }

        internal struct EnqueueState
        {
            public bool Acquired;
            public bool AlreadyExcluded;
        }

        private static GameComponent_OuterrealmStorage Storage => GameComponent_OuterrealmStorage.Instance;
        private static OuterrealmBeamLedger Ledger => Storage?.Runtime.Beams;

        internal static long ReservationAvailable(Pawn pawn, Thing thing, long available)
            => query.Thing == thing && query.Pawn == pawn && query.Entry != null
                ? Math.Max(0, query.Entry.Count - Storage.ReservedCountOf(query.Entry) + query.Own) : available;

        internal static bool HasForeignReservation(Pawn pawn, Thing thing, OuterrealmEntry entry)
            => (Ledger?.Reserved(entry) ?? 0) > (query.Thing == thing && query.Pawn == pawn ? query.Own : 0);

        internal static int ReservationRequest(Pawn pawn, Thing thing, int requested, long available)
            => query.Thing == thing && query.Pawn == pawn
                ? (int)Math.Min(requested, Math.Max(0, available)) : requested;

        internal static bool ExcludeVaultDestination => vaultStorageSearch;

        public static void Install()
        {
            Type op = AccessTools.TypeByName("ManipulatorBeam.IBeamOperator");
            if (op == null) return;
            Harmony harmony = new Harmony(PatchId);
            try
            {
                Type utility = RequiredType("BeamManipulatorUtility");
                Type building = RequiredType("Building_BeamManipulator");
                Type batch = RequiredType("BeamHaulBatch");
                Type transfer = RequiredType("BeamTransfer");
                Type claims = RequiredType("BeamClaimUtility");
                Type set = typeof(HashSet<IntVec3>);
                MethodInfo batchMethod = Require(utility, "TryBuildBatchFromCell", true, typeof(bool),
                    new[] { "op", "cell", "excludedDestinations", "ownerKey", "batch" },
                    op, typeof(IntVec3), set, typeof(int), batch.MakeByRefType());
                MethodInfo destination = Require(utility, "TryFindStorageDestinationFor", true, typeof(bool),
                    new[] { "op", "thing", "excludedDestinations", "ownerKey", "destination" },
                    op, typeof(Thing), set, typeof(int), typeof(IntVec3).MakeByRefType());
                MethodInfo scan = Require(utility, "ScanForAnyHaulWork", true, typeof(bool), new[] { "op" }, op);
                MethodInfo candidate = Require(utility, "CanBeamTransferThing", true, typeof(bool),
                    new[] { "op", "thing", "ownerKey" }, op, typeof(Thing), typeof(int));
                MethodInfo enqueue = Require(utility, "TryClaimAndEnqueue", true, typeof(bool),
                    new[] { "transfer", "destinationQueue", "excludedThings", "ownerKey" },
                    transfer, typeof(List<>).MakeGenericType(transfer), typeof(HashSet<Thing>), typeof(int));
                MethodInfo take = Require(building, "TryLiftForTransfer", false, typeof(bool),
                    new[] { "op", "transfer", "carriedThing" }, op, transfer, typeof(Thing).MakeByRefType());
                MethodInfo extract = Require(building, "ExtractThingForTransfer", true, typeof(Thing), new[] { "transfer" }, transfer);
                MethodInfo release = Require(claims, "ReleaseClaim", true, typeof(void), new[] { "transfer", "ownerKey" }, transfer, typeof(int));
                MethodInfo clear = Require(claims, "ReleaseAllClaimsForOwner", true, typeof(void), new[] { "map", "ownerKey" }, typeof(Map), typeof(int));
                // 目的地保护也是新版协议的一部分，不能只安装源端。
                MethodInfo group = Require(utility, "IsBeamStorageGroupAllowed", true, typeof(bool), new[] { "group" }, typeof(SlotGroup));
                mapOf = Getter<Map>(op, "Map", false);
                pawnOf = Getter<Pawn>(op, "Pawn", false);
                ownerOf = Getter<int>(op, "OwnerKey", false);
                thingOf = Getter<Thing>(transfer, "thing", true);
                containerOf = Getter<Thing>(transfer, "destinationContainer", true);
                countOf = Getter<int>(transfer, "count", true);
                stripOf = Getter<bool>(transfer, "isStripJob", true);
                destinationOf = Getter<IntVec3>(transfer, "destination", true);
                setCount = Setter<int>(transfer, "count");
                transfersOf = Getter<IList>(batch, "transfers", true);
                findDestination = Bind<FindDestination>(destination);
                canTransfer = Bind<CanTransfer>(candidate);
                releaseClaim = Bind<Action<object, int>>(release);
                ParameterExpression cell = Expression.Parameter(typeof(IntVec3), "cell");
                newBatch = Expression.Lambda<Func<IntVec3, object>>(Expression.Convert(
                    Expression.MemberInit(Expression.New(batch), Expression.Bind(batch.GetField("sourceCell"), cell)), typeof(object)), cell).Compile();
                ParameterExpression item = Expression.Parameter(typeof(Thing), "thing");
                ParameterExpression dest = Expression.Parameter(typeof(IntVec3), "destination");
                newTransfer = Expression.Lambda<Func<Thing, IntVec3, IntVec3, object>>(Expression.Convert(
                    Expression.New(transfer.GetConstructor(new[] { typeof(Thing), typeof(IntVec3), typeof(IntVec3) }), item, cell, dest), typeof(object)), item, cell, dest).Compile();
                ParameterExpression container = Expression.Parameter(typeof(Thing), "container");
                ParameterExpression count = Expression.Parameter(typeof(int), "count");
                ConstructorInfo containerConstructor = transfer.GetConstructor(new[] { typeof(Thing), typeof(IntVec3), typeof(Thing), typeof(int) })
                    ?? throw new MissingMethodException(transfer.FullName, ".ctor(Thing, IntVec3, Thing, int)");
                newContainerTransfer = Expression.Lambda<Func<Thing, IntVec3, Thing, int, object>>(Expression.Convert(
                    Expression.New(containerConstructor, item, cell, container, count), typeof(object)), item, cell, container, count).Compile();

                Patch(harmony, candidate, "CandidatePrefix", null, "CandidateFinalizer");
                Patch(harmony, destination, "DestinationPrefix", null, "DestinationFinalizer");
                Patch(harmony, batchMethod, null, "BatchPostfix");
                Patch(harmony, scan, null, "ScanPostfix");
                Patch(harmony, enqueue, "EnqueuePrefix", null, "EnqueueFinalizer");
                Patch(harmony, take, "LiftPrefix", null, "LiftFinalizer");
                Patch(harmony, extract, "ExtractPrefix");
                Patch(harmony, release, null, null, "ReleaseFinalizer");
                Patch(harmony, clear, null, null, "ClearFinalizer");
                Patch(harmony, group, null, "GroupPostfix");
                enabled = true;
                Log.Message("[OuterrealmStorage] IBeamOperator compatibility installed (10 boundaries).");
            }
            catch (Exception error)
            {
                enabled = false;
                harmony.UnpatchAll(PatchId);
                Log.Error("[OuterrealmStorage] IBeamOperator compatibility disabled; installation rolled back. " + error);
            }
        }

        private static Type RequiredType(string name) => AccessTools.TypeByName("ManipulatorBeam." + name)
            ?? throw new MissingMemberException(name);

        private static MethodInfo Require(Type type, string name, bool isStatic, Type result, string[] names, params Type[] types)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, types, null);
            if (method == null || method.ReturnType != result || method.IsStatic != isStatic || method.ContainsGenericParameters)
                throw new MissingMethodException(type.FullName, name);
            ParameterInfo[] args = method.GetParameters();
            for (int i = 0; i < args.Length; i++)
                if (args[i].Name != names[i] || args[i].IsOut != types[i].IsByRef || args[i].IsIn)
                    throw new MissingMethodException(type.FullName, name + " parameter " + names[i]);
            return method;
        }

        private static Func<object, T> Getter<T>(Type type, string name, bool field)
        {
            ParameterExpression arg = Expression.Parameter(typeof(object));
            Expression member = field ? (Expression)Expression.Field(Expression.Convert(arg, type), name)
                : Expression.Property(Expression.Convert(arg, type), name);
            if (!typeof(T).IsAssignableFrom(member.Type)) throw new MissingMemberException(type.FullName, name);
            return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), arg).Compile();
        }

        private static Action<object, T> Setter<T>(Type type, string name)
        {
            ParameterExpression arg = Expression.Parameter(typeof(object));
            ParameterExpression value = Expression.Parameter(typeof(T));
            return Expression.Lambda<Action<object, T>>(Expression.Assign(Expression.Field(Expression.Convert(arg, type), name), value), arg, value).Compile();
        }

        private static T Bind<T>(MethodInfo method) where T : Delegate
        {
            ParameterInfo[] signature = typeof(T).GetMethod("Invoke").GetParameters();
            ParameterInfo[] target = method.GetParameters();
            ParameterExpression[] args = new ParameterExpression[signature.Length];
            Expression[] call = new Expression[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = Expression.Parameter(signature[i].ParameterType, signature[i].Name);
                call[i] = signature[i].ParameterType == target[i].ParameterType ? (Expression)args[i] : Expression.Convert(args[i], target[i].ParameterType);
            }
            return Expression.Lambda<T>(Expression.Call(method, call), args).Compile();
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefix = null, string postfix = null, string finalizer = null)
        {
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(OuterrealmBeamAdapter), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(OuterrealmBeamAdapter), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(OuterrealmBeamAdapter), finalizer));
        }

        private static bool IsStored(Thing thing) => thing != null &&
            (OuterrealmVaultUtil.IsProjection(thing) || OuterrealmPatchUtil.IsVaultStoredThing(thing));

        private static bool Allowed(in OuterrealmSource source, bool forUse)
        {
            Building_OuterrealmVault vault = source.Vault;
            return source.IsVaultQuery && vault != null && vault.Spawned && !vault.Frozen
                && source.QueryThing.Spawned && source.QueryThing.Map == vault.Map && vault.CanShow(source.QueryThing)
                && (vault.HaulSourceEnabled || (forUse && vault.AllowTakeForUse));
        }

        private static bool ForUse(object transfer)
        {
            Thing container = containerOf(transfer);
            // 运输舱属于搬出；其他补给容器与施工属于拿取使用。
            return container != null && !(container is Building_OuterrealmVault)
                && container.TryGetComp<CompTransporter>() == null;
        }

        private static bool CandidatePrefix(object op, Thing thing, int ownerKey, ref bool __result, out QueryScope __state)
        {
            __state = query;
            query = default;
            if (!enabled || !IsStored(thing)) return true;
            if (op == null || !OuterrealmSourceResolver.TryResolve(thing, out OuterrealmSource source) || !Allowed(source, true)
                || Ledger.IsExtracting(source.Entry) || Storage.Runtime.Bills.IsTransferring(source.Entry))
            { __result = false; return false; }
            long own = Ledger.Own(thing, ownerKey);
            long available = source.Entry.Count - Storage.ReservedCountOf(source.Entry) + own;
            if (available <= 0) { __result = false; return false; }
            query = new QueryScope { Thing = thing, Pawn = pawnOf(op), Entry = source.Entry, Own = own };
            return true;
        }

        private static Exception CandidateFinalizer(Exception __exception, QueryScope __state)
        { query = __state; return __exception; }

        private static bool DestinationPrefix(Thing thing, ref bool __result, out bool __state)
        {
            __state = vaultStorageSearch;
            vaultStorageSearch = enabled && IsStored(thing);
            if (!vaultStorageSearch) return true;
            if (OuterrealmSourceResolver.TryResolve(thing, out OuterrealmSource source) && Allowed(source, false)) return true;
            __result = false;
            return false;
        }

        private static Exception DestinationFinalizer(Exception __exception, bool __state)
        { vaultStorageSearch = __state; return __exception; }

        private static void GroupPostfix(SlotGroup group, ref bool __result)
        {
            if (__result && group?.parent is Building_OuterrealmVault vault)
                __result = vault.HaulDestinationEnabled && !vaultStorageSearch;
        }

        private static void ScanPostfix(object op, ref bool __result)
        {
            if (!enabled || __result || op == null) return;
            Map map = mapOf(op);
            if (map == null || Storage?.HasVaultOnMap(map) != true) return;
            foreach (Thing thing in map.listerHaulables.ThingsPotentiallyNeedingHauling())
            {
                if (IsStored(thing))
                {
                    if (findDestination(op, thing, null, ownerOf(op), out IntVec3 ignored))
                    { __result = true; return; }
                }
                else if (canTransfer(op, thing, ownerOf(op)) && TryFindVaultDestination(map, thing, out Building_OuterrealmVault ignored))
                { __result = true; return; }
            }
        }

        private static void BatchPostfix(object op, IntVec3 cell, HashSet<IntVec3> excludedDestinations, int ownerKey, object[] __args, ref bool __result)
        {
            if (!enabled || op == null) return;
            Map map = mapOf(op);
            object batch = __args[4];
            IList transfers = batch == null ? null : transfersOf(batch);

            // 原光束已将 vault 当普通存储格选中：把格子目的地改写为容器目的地，
            // 使 FinishTransfer 走 vault.view.TryAdd → Deposit，而不是先落地再等待吸收。
            if (transfers != null)
            {
                for (int i = 0; i < transfers.Count; i++)
                {
                    object transfer = transfers[i];
                    Building_OuterrealmVault destinationVault = BeamManipulatorCompat.VaultAtCell(destinationOf(transfer), map);
                    Thing thing = thingOf(transfer);
                    if (destinationVault != null && CanDepositInto(destinationVault, map, thing))
                        transfers[i] = newContainerTransfer(thing, cell, destinationVault, countOf(transfer));
                }
            }

            Building_OuterrealmVault vault = BeamManipulatorCompat.VaultAtCell(cell, map);
            if (vault?.view == null || !vault.HaulSourceEnabled)
            {
                // 原格子搜索未识别 hybrid storage 时，直接为地面物品补建容器传输。
                List<Thing> things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (IsStored(thing) || ContainsThing(transfers, thing) || !canTransfer(op, thing, ownerKey)
                        || !TryFindVaultDestination(map, thing, out Building_OuterrealmVault destinationVault))
                        continue;
                    if (batch == null) { batch = newBatch(cell); transfers = transfersOf(batch); }
                    transfers.Add(newContainerTransfer(thing, cell, destinationVault, thing.stackCount));
                }
                if (transfers != null && transfers.Count > 0) { __args[4] = batch; __result = true; }
                return;
            }
            int originalCount = transfers?.Count ?? 0;
            OuterrealmBeamCursor cursor = Storage.Runtime.BeamCursor(vault, cell);
            List<Thing> copies = vault.view.InnerListForReading;
            int budget = Math.Min(copies.Count, CandidateWindow);
            int start = copies.Count == 0 ? 0 : cursor.Projection % copies.Count;
            for (int i = 0; i < budget; i++)
                AddCandidate(copies[(start + i) % copies.Count], op, cell, excludedDestinations, ownerKey, originalCount, ref batch, ref transfers);
            cursor.Projection = copies.Count == 0 ? 0 : (start + budget) % copies.Count;
            // 唯一锚点使用运行时仓库索引，避免每个格子扫描整个全局账本。
            HashSet<OuterrealmRuntimeRegistration> registrations = Storage.Runtime.BeamRegistrations(vault);
            if (registrations != null && registrations.Count > 0)
            {
                int identityStart = cursor.Identity % registrations.Count;
                int identityBudget = Math.Min(registrations.Count, CandidateWindow);
                int index = 0;
                foreach (OuterrealmRuntimeRegistration registration in registrations)
                {
                    int offset = (index++ - identityStart + registrations.Count) % registrations.Count;
                    if (offset < identityBudget && registration.Active && registration.Kind == OuterrealmRuntimeRegistrationKind.IdentityAnchor)
                        AddCandidate(registration.Thing, op, cell, excludedDestinations, ownerKey, originalCount, ref batch, ref transfers);
                }
                cursor.Identity = (identityStart + identityBudget) % registrations.Count;
            }
            if (transfers != null && transfers.Count > 0) { __args[4] = batch; __result = true; }
        }

        private static bool ContainsThing(IList transfers, Thing thing)
        {
            if (transfers == null) return false;
            for (int i = 0; i < transfers.Count; i++)
                if (thingOf(transfers[i]) == thing) return true;
            return false;
        }

        private static bool CanDepositInto(Building_OuterrealmVault vault, Map map, Thing thing)
            => vault != null && vault.Map == map && vault.view != null && vault.HaulDestinationEnabled
                && thing != null && !OuterrealmVaultUtil.IsProtectedFromAutomaticDeposit(thing) && vault.Accepts(thing);

        private static bool TryFindVaultDestination(Map map, Thing thing, out Building_OuterrealmVault destination)
        {
            destination = null;
            GameComponent_OuterrealmStorage storage = Storage;
            if (map == null || thing == null || storage == null) return false;
            StoragePriority current = StoreUtility.CurrentStoragePriorityOf(thing);
            StoragePriority bestPriority = StoragePriority.Unstored;
            int bestDistance = int.MaxValue;
            List<Building_OuterrealmVault> vaults = storage.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault vault = vaults[i];
                if (!CanDepositInto(vault, map, thing)) continue;
                StoragePriority priority = vault.GetStoreSettings().Priority;
                if (current != StoragePriority.Unstored && priority <= current) continue;
                int distance = (vault.Position - thing.Position).LengthHorizontalSquared;
                if (destination == null || priority > bestPriority || priority == bestPriority && distance < bestDistance)
                {
                    destination = vault;
                    bestPriority = priority;
                    bestDistance = distance;
                }
            }
            return destination != null;
        }

        private static void AddCandidate(Thing thing, object op, IntVec3 cell, HashSet<IntVec3> excluded, int owner, int originalCount,
            ref object batch, ref IList transfers)
        {
            if (thing == null || thing.Position != cell || !IsStored(thing)) return;
            if (transfers != null)
                for (int i = 0; i < originalCount; i++) if (thingOf(transfers[i]) == thing) return;
            if (!findDestination(op, thing, excluded, owner, out IntVec3 destination)) return;
            if (batch == null) { batch = newBatch(cell); transfers = transfersOf(batch); }
            transfers.Add(newTransfer(thing, cell, destination));
        }

        private static bool EnqueuePrefix(object transfer, HashSet<Thing> excludedThings, int ownerKey, ref bool __result, out EnqueueState __state)
        {
            __state = default;
            if (!enabled || transfer == null || !IsStored(thingOf(transfer))) return true;
            Thing thing = thingOf(transfer);
            if (!UnityData.IsInMainThread || stripOf(transfer) || !OuterrealmSourceResolver.TryResolve(thing, out OuterrealmSource source)
                || !Allowed(source, ForUse(transfer))) { __result = false; return false; }
            int requested = countOf(transfer) > 0 ? Math.Min(countOf(transfer), thing.stackCount) : thing.stackCount;
            long available = Math.Max(0, source.Entry.Count - Storage.ReservedCountOf(source.Entry));
            requested = (int)Math.Min(requested, available);
            if (!Ledger.TryAcquire(transfer, source, thing.Map, ownerKey, requested, available))
            { __result = false; return false; }
            __state = new EnqueueState { Acquired = true, AlreadyExcluded = excludedThings.Contains(thing) };
            setCount(transfer, requested);
            return true;
        }

        private static Exception EnqueueFinalizer(object transfer, object destinationQueue, HashSet<Thing> excludedThings,
            int ownerKey, ref bool __result, EnqueueState __state, Exception __exception)
        {
            if (__state.Acquired)
            {
                bool valid = __exception == null && __result && Ledger.Resize(transfer, countOf(transfer));
                if (!valid)
                {
                    __result = false;
                    try { releaseClaim(transfer, ownerKey); }
                    finally
                    {
                        Ledger?.Release(transfer);
                        (destinationQueue as IList)?.Remove(transfer);
                        if (!__state.AlreadyExcluded) excludedThings.Remove(thingOf(transfer));
                    }
                }
            }
            return __exception;
        }

        private static bool LiftPrefix(object op, object transfer, ref bool __result, out LiftState __state)
        {
            __state = null;
            if (!enabled || transfer == null) return true;
            if (!IsStored(thingOf(transfer)))
            {
                // 锚点已被其他路径取出时，旧队列不能继续搬走现在属于地图的实物。
                if (Ledger?.TryGet(transfer, out OuterrealmBeamLease stale) != true) return true;
                Ledger.Release(transfer);
                __result = false;
                return false;
            }
            if (op == null || !UnityData.IsInMainThread || !Ledger.TryGet(transfer, out OuterrealmBeamLease lease)
                || lease.Extracting || lease.Owner != ownerOf(op) || lease.Map != mapOf(op)
                || !OuterrealmSourceResolver.TryResolve(thingOf(transfer), out OuterrealmSource source)
                || source.Entry != lease.Source.Entry || !Allowed(source, ForUse(transfer)))
            { __result = false; return false; }
            __state = new LiftState { Previous = lift, Lease = lease, Storage = Storage };
            lift = __state;
            return true;
        }

        private static bool ExtractPrefix(object transfer, ref Thing __result)
        {
            if (!enabled || transfer == null || !IsStored(thingOf(transfer))) return true;
            __result = null;
            LiftState state = lift;
            if (state == null || state.Lease.Transfer != transfer || state.Lease.Extracting) return false;
            OuterrealmBeamLease lease = state.Lease;
            int count = countOf(transfer);
            if (count <= 0 || count != lease.Count || !Allowed(lease.Source, ForUse(transfer))
                || lease.Source.Entry.Count - state.Storage.ReservedCountOf(lease.Source.Entry) + lease.Count < count) return false;
            if (!state.Storage.Runtime.Beams.BeginExtraction(lease)) return false;
            // 预留在 Checkout 完成前持续有效；回调重入不能再次获得同一额度。
            state.Actual = OuterrealmSourceResolver.Checkout(lease.Source, count);
            __result = state.Actual;
            return false;
        }

        private static Exception LiftFinalizer(LiftState __state, Exception __exception)
        {
            if (__state == null) return __exception;
            try
            {
                Thing actual = __state.Actual;
                if (actual != null && !actual.Destroyed && !actual.Spawned && actual.holdingOwner == null)
                    __state.Storage.Deposit(actual, __state.Lease.Source.Vault);
            }
            finally
            {
                try { __state.Storage.Runtime.Beams.Release(__state.Lease.Transfer, true); }
                finally { lift = __state.Previous; }
            }
            return __exception;
        }

        private static Exception ReleaseFinalizer(object transfer, Exception __exception)
        { Ledger?.Release(transfer); return __exception; }

        private static Exception ClearFinalizer(Map map, int ownerKey, Exception __exception)
        { Ledger?.ReleaseOwner(map, ownerKey); return __exception; }
    }
}
