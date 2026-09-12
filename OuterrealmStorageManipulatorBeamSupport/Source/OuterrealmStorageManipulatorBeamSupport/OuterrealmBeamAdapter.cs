using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using FullyAutomaticOmniCrafter;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using HarmonyLib;
using RimWorld;
using Verse;

namespace OuterrealmStorageManipulatorBeamSupport
{
    /// <summary>
    /// 新版牵引光束（IBeamOperator 协议）与超维存储的边界适配器。
    ///
    /// 编译期引用 ManipulatorBeam.dll 与主 mod 源码，但第三方类型只经 AccessTools 字符串
    /// 解析（TypeByName / GetMethod），运行时调用使用表达式树编译委托，不重复反射。
    /// 未安装或签名变化时 Require 抛异常：整组补丁回滚并写日志，主 mod 不受影响。
    ///
    /// 为什么不需要「借种子 / 注入候选」：新版光束的源扫描是
    /// BeamManipulatorUtility.FillTransferQueue / ScanForAnyHaulWork 按
    /// map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver) 枚举，而超维存储的
    /// 查询投影与唯一权威锚点都经 map.listerThings.Add 注册（伪 Spawned），因此光束能自然
    /// 发现它们；旧版按 cell.GetThingList(thingGrid) 逐格扫描才需要种子与注入。
    ///
    /// 安装的 10 个边界见 Install。
    /// </summary>
    internal static class OuterrealmBeamAdapter
    {
        private const string PatchId = "Jeremie.OuterrealmStorage.ManipulatorBeamSupport";
        private static Func<object, Map> mapOf;
        private static Func<object, Pawn> pawnOf;
        private static Func<object, int> ownerOf;
        private static Func<object, object> manipulatorOf;
        private static Func<object, Thing> thingOf, containerOf;
        private static Func<object, IntVec3> destinationOf;
        private static Func<object, int> countOf;
        private static Func<object, bool> stripOf;
        private static Action<object, int> setCount;
        private static Action<object, Thing> setDestinationContainer;
        private static Action<object, int> releaseClaim;
        private static Action<object, Thing> releaseInTransit;
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
        private static OuterrealmBeamLedger Ledger => OuterrealmBeamSupportComponent.Current?.Ledger;

        internal static long ReservationAvailable(Pawn pawn, Thing thing, long available)
            => query.Thing == thing && query.Pawn == pawn && query.Entry != null
                ? Math.Max(0, query.Entry.Count - Storage.ReservedCountOf(query.Entry) + query.Own) : available;

        internal static bool HasForeignReservation(Pawn pawn, Thing thing, OuterrealmEntry entry)
            => (Ledger?.Reserved(entry) ?? 0) > (query.Thing == thing && query.Pawn == pawn ? query.Own : 0);

        internal static int ReservationRequest(Pawn pawn, Thing thing, int requested, long available)
            => query.Thing == thing && query.Pawn == pawn
                ? (int)Math.Min(requested, Math.Max(0, available)) : requested;

        public static void Install()
        {
            Type op = AccessTools.TypeByName("ManipulatorBeam.IBeamOperator");
            if (op == null) return;
            Harmony harmony = new Harmony(PatchId);
            try
            {
                Type utility = RequiredType("BeamManipulatorUtility");
                Type building = RequiredType("Building_BeamManipulator");
                Type transfer = RequiredType("BeamTransfer");
                Type claims = RequiredType("BeamClaimUtility");
                Type set = typeof(HashSet<IntVec3>);
                MethodInfo destination = Require(utility, "TryFindStorageDestinationFor", true, typeof(bool),
                    new[] { "op", "thing", "excludedDestinations", "ownerKey", "destination" },
                    op, typeof(Thing), set, typeof(int), typeof(IntVec3).MakeByRefType());
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
                MethodInfo claimContainer = Require(claims, "TryClaimDestinationContainer", true, typeof(bool),
                    new[] { "transfer", "ownerKey" }, transfer, typeof(int));
                MethodInfo finish = Require(utility, "FinishTransfer", true, typeof(bool),
                    new[] { "op", "carriedThing", "transfer", "fallbackCell" },
                    op, typeof(Thing), transfer, typeof(IntVec3));
                MethodInfo releaseTransit = Require(building, "ReleaseInTransitThing", false, typeof(void),
                    new[] { "thing" }, typeof(Thing));
                // 目的地保护也是协议的一部分，不能只安装源端。
                MethodInfo group = Require(utility, "IsBeamStorageGroupAllowed", true, typeof(bool), new[] { "group" }, typeof(SlotGroup));
                mapOf = Getter<Map>(op, "Map", false);
                pawnOf = Getter<Pawn>(op, "Pawn", false);
                ownerOf = Getter<int>(op, "OwnerKey", false);
                manipulatorOf = Getter<object>(op, "Manipulator", false);
                thingOf = Getter<Thing>(transfer, "thing", true);
                containerOf = Getter<Thing>(transfer, "destinationContainer", true);
                countOf = Getter<int>(transfer, "count", true);
                stripOf = Getter<bool>(transfer, "isStripJob", true);
                destinationOf = Getter<IntVec3>(transfer, "destination", true);
                setCount = Setter<int>(transfer, "count");
                // 目的地「格 → 容器」改写用字段赋值：transfer 对象身份必须保持不变，
                // 因为新版 FillTransferQueue 在构造该 transfer 时已对它做过 claim 登记。
                setDestinationContainer = Setter<Thing>(transfer, "destinationContainer");
                releaseClaim = Bind<Action<object, int>>(release);
                ParameterExpression machine = Expression.Parameter(typeof(object), "machine");
                ParameterExpression carried = Expression.Parameter(typeof(Thing), "carried");
                releaseInTransit = Expression.Lambda<Action<object, Thing>>(Expression.Call(
                    Expression.Convert(machine, building), releaseTransit, carried), machine, carried).Compile();

                Patch(harmony, candidate, "CandidatePrefix", null, "CandidateFinalizer");
                Patch(harmony, destination, "DestinationPrefix", null, "DestinationFinalizer");
                Patch(harmony, enqueue, "EnqueuePrefix", null, "EnqueueFinalizer");
                Patch(harmony, take, "LiftPrefix", null, "LiftFinalizer");
                Patch(harmony, extract, "ExtractPrefix");
                Patch(harmony, release, null, null, "ReleaseFinalizer");
                Patch(harmony, clear, null, null, "ClearFinalizer");
                Patch(harmony, claimContainer, "ContainerClaimPrefix");
                Patch(harmony, finish, "FinishPrefix");
                Patch(harmony, group, null, "GroupPostfix");
                enabled = true;
                Log.Message("[OuterrealmStorageManipulatorBeamSupport] IBeamOperator compatibility installed (10 boundaries).");
            }
            catch (Exception error)
            {
                enabled = false;
                harmony.UnpatchAll(PatchId);
                Log.Error("[OuterrealmStorageManipulatorBeamSupport] IBeamOperator compatibility disabled; installation rolled back. " + error);
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

        /// <summary>该格是否为某 vault 的存储格；是则返回 vault，否则 null（O(1) slotGroup 查询）。</summary>
        private static Building_OuterrealmVault VaultAtCell(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid)
            {
                return null;
            }
            return cell.GetSlotGroup(map)?.parent as Building_OuterrealmVault;
        }

        private static bool IsStored(Thing thing) => thing != null &&
            (OuterrealmVaultUtil.IsProjection(thing) || OuterrealmVaultUtil.IsVaultStoredThing(thing));

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

        /// <summary>光束把 vault 格选为目的地时，改写为容器目的地，使 FinishTransfer 走
        /// view.TryAdd → Deposit 的即时入库路径，而不是先落地再等 vault 的 tick 吸收。
        /// 必须在 TryClaimAndEnqueue 的 claim 之前完成（本 Prefix 内），否则目的地 claim
        /// 会绑定在格子上。</summary>
        private static void RewriteVaultDestination(object transfer)
        {
            if (transfer == null || setDestinationContainer == null || containerOf(transfer) != null)
            {
                return; // 已有容器目的地（施工/补给）不覆盖
            }
            Thing thing = thingOf(transfer);
            Map map = thing?.Map;
            if (map == null)
            {
                return;
            }
            Building_OuterrealmVault vault = VaultAtCell(destinationOf(transfer), map);
            if (vault != null && CanDepositInto(vault, map, thing))
            {
                setDestinationContainer(transfer, vault);
            }
        }

        private static bool CandidatePrefix(object op, Thing thing, int ownerKey, ref bool __result, out QueryScope __state)
        {
            __state = query;
            query = default;
            if (!enabled || !IsStored(thing)) return true;
            if (op == null || !OuterrealmSourceResolver.TryResolve(thing, out OuterrealmSource source) || !Allowed(source, true)
                || Ledger?.IsExtracting(source.Entry) == true || Storage.IsTransferring(source.Entry))
            { __result = false; return false; }
            long own = Ledger?.Own(thing, ownerKey) ?? 0;
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

        private static bool CanDepositInto(Building_OuterrealmVault vault, Map map, Thing thing)
            => vault != null && vault.Map == map && vault.view != null && vault.HaulDestinationEnabled
                && thing != null && !OuterrealmVaultUtil.IsProtectedFromAutomaticDeposit(thing) && vault.Accepts(thing);

        private static bool ContainerClaimPrefix(object transfer, ref bool __result)
        {
            if (!enabled || transfer == null || !(containerOf(transfer) is Building_OuterrealmVault vault))
                return true;
            // Vault 是无限容量的吸收端，不需要第三方对普通容器设置的全局独占锁。
            // 来源 Thing 的 claim 仍由 TryClaimAndEnqueue 保留，故不会重复搬运同一物品。
            __result = CanDepositInto(vault, vault.Map, thingOf(transfer));
            return false;
        }

        private static bool FinishPrefix(object op, Thing carriedThing, object transfer, ref bool __result)
        {
            if (!enabled || op == null || carriedThing == null || transfer == null)
                return true;
            Thing destinationContainer = containerOf(transfer);
            object manipulator = manipulatorOf(op);
            if (manipulator == null) return true;

            if (destinationContainer is Building_MatterEnergyConverter converter)
            {
                CompTransporter transporter = converter.GetComp<CompTransporter>();
                ThingOwner innerContainer = transporter?.innerContainer;
                if (innerContainer == null) return true;

                // MEC 同时继承 Building_Storage。光束通用容器路径会先调用其存储区
                // IHaulDestination.Accepts，错误地用地面存储筛选器拒绝装载模式中的物品。
                // 直接写入 CompTransporter.innerContainer；ThingOwner.NotifyAdded 会自动调用
                // CompTransporter.Notify_ThingAdded，扣减 leftToLoad 并刷新质量缓存。
                releaseInTransit(manipulator, carriedThing);
                if (!innerContainer.TryAdd(carriedThing, true)) return true;
                __result = true;
                return false;
            }

            if (!(destinationContainer is Building_OuterrealmVault vault)
                || !CanDepositInto(vault, mapOf(op), carriedThing))
                return true;

            // BeamContainerUtility 以 Destroyed/stackCount/holdingOwner 判断交付成功；但 vault
            // 的不可堆叠权威实例（尸体等）按设计保持未生成且无 holder，会被误判失败并重新落地。
            // 在此直接以 vault 的 Deposit 结果为提交结果，并从光束在途容器解除，禁止 finally
            // ReturnInTransitThing + EnsureCarriedThingLanded 把已入库的权威实例再次放回地图。
            releaseInTransit(manipulator, carriedThing);
            if (!vault.view.TryAdd(carriedThing, false)) return true;
            __result = true;
            return false;
        }

        private static bool EnqueuePrefix(object transfer, HashSet<Thing> excludedThings, int ownerKey, ref bool __result, out EnqueueState __state)
        {
            __state = default;
            if (!enabled || transfer == null) return true;
            // 目的地改写必须先于 claim：TryClaimAndEnqueue 会登记目的地占用。
            RewriteVaultDestination(transfer);
            Thing thing = thingOf(transfer);
            if (!IsStored(thing)) return true;
            if (!UnityData.IsInMainThread || stripOf(transfer) || !OuterrealmSourceResolver.TryResolve(thing, out OuterrealmSource source)
                || !Allowed(source, ForUse(transfer))) { __result = false; return false; }
            int requested = countOf(transfer) > 0 ? Math.Min(countOf(transfer), thing.stackCount) : thing.stackCount;
            long available = Math.Max(0, source.Entry.Count - Storage.ReservedCountOf(source.Entry));
            requested = (int)Math.Min(requested, available);
            if (Ledger == null || !Ledger.TryAcquire(transfer, source, thing.Map, ownerKey, requested, available))
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
            if (op == null || !UnityData.IsInMainThread || Ledger == null || !Ledger.TryGet(transfer, out OuterrealmBeamLease lease)
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
            if (Ledger == null || !Ledger.BeginExtraction(lease)) return false;
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
                try { Ledger?.Release(__state.Lease.Transfer, true); }
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
