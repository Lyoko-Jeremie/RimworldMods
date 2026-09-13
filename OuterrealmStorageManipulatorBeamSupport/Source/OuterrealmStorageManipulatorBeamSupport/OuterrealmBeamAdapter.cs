using System;
using System.Collections;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using RimWorld;
using Verse;

namespace OuterrealmStorageManipulatorBeamSupport
{
    /// <summary>
    /// 新版牵引光束（IBeamOperator 协议）与超维存储的边界适配器：11 个 Harmony 边界的实现。
    ///
    /// 反射绑定与安装流程分别在 OuterrealmBeamBinding / OuterrealmBeamInstaller；本文件只放
    /// 边界逻辑与线程局部状态。第三方类型一律经委托字段访问（安装时用表达式树编译），
    /// 边界方法内不做反射。
    ///
    /// 为什么不需要「借种子 / 注入候选」：新版光束的源扫描是
    /// BeamManipulatorUtility.FillTransferQueue / ScanForAnyHaulWork 按
    /// map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver) 枚举，而超维存储的
    /// 查询投影与唯一权威锚点都经 map.listerThings.Add 注册（伪 Spawned），因此光束能自然
    /// 发现它们；旧版按 cell.GetThingList(thingGrid) 逐格扫描才需要种子与注入。
    ///
    /// 其中 AdvanceChannel 是续搬修正：Checkout 取空条目后主 mod 会移除源投影，光束据此中止
    /// 搬运、把在途实体丢回 vault 格并被自动吸收，形成无限循环。
    /// </summary>
    internal static class OuterrealmBeamAdapter
    {
        internal const string PatchId = "Jeremie.OuterrealmStorage.ManipulatorBeamSupport";
        // 以下委托与开关由 OuterrealmBeamInstaller.Install 赋值。
        internal static Func<object, Map> mapOf;
        internal static Func<object, Pawn> pawnOf;
        internal static Func<object, int> ownerOf;
        internal static Func<object, object> manipulatorOf;
        internal static Func<object, Thing> thingOf, containerOf;
        internal static Func<object, IntVec3> destinationOf;
        internal static Func<object, int> countOf;
        internal static Func<object, bool> stripOf;
        internal static Action<object, int> setCount;
        internal static Action<object, Thing> setDestinationContainer;
        // 续搬用（见 AdvancePrefix）：通道 → transfer、通道 → 在途实体、transfer.thing 赋值。
        internal static Func<object, object> transferOf;
        internal static Func<object, Thing> carriedInTransitOf;
        internal static Action<object, Thing> setThing;
        internal static Action<object, int> releaseClaim;
        internal static Action<object, Thing> releaseInTransit;
        internal static volatile bool enabled;
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
            OuterrealmSource source;
            bool resolved = OuterrealmSourceResolver.TryResolve(thing, out source);
            if (op == null || !resolved || !Allowed(source, true)
                || Ledger?.IsExtracting(source.Entry) == true || Storage.IsTransferring(source.Entry))
            {
                __result = false; return false;
            }
            long own = Ledger?.Own(thing, ownerKey) ?? 0;
            long available = source.Entry.Count - Storage.ReservedCountOf(source.Entry) + own;
            if (available <= 0)
            {
                __result = false; return false;
            }
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
            OuterrealmSource source;
            if (OuterrealmSourceResolver.TryResolve(thing, out source) && Allowed(source, false))
            {
                return true;
            }
            __result = false;
            return false;
        }

        private static Exception DestinationFinalizer(Exception __exception, bool __state)
        { vaultStorageSearch = __state; return __exception; }

        private static void GroupPostfix(SlotGroup group, ref bool __result)
        {
            if (__result && group?.parent is Building_OuterrealmVault vault)
            {
                // 源是 vault 物品时（vaultStorageSearch）排除 vault 目的地，避免共享库存循环搬运。
                __result = vault.HaulDestinationEnabled && !vaultStorageSearch;
            }
        }

        /// <summary>通道推进前的续搬修正。
        ///
        /// 背景：Checkout 取空条目后，主 mod 会移除该条目并让各 vault 视图同步移除源投影
        ///（NotifyEntriesEmptied → view.SyncEntry）。而光束 ManipulatorBeam.AdvanceChannel 每 tick
        /// 都检查 channel.activeTransfer.thing，一旦发现 Destroyed 就 AbortChannel，把已提取的
        /// 在途实体丢回设备落点附近——那里正是 vault 格，于是被自动吸收回库、投影重建、光束
        /// 再次搬运，形成无限循环。
        ///
        /// 搬运的真实载体是通道里的 carriedThingInTransit（本适配器 Checkout 出的权威实例），
        /// transfer.thing 对光束只是源的身份凭证，因此只要该 transfer 仍属于本账本（本次搬运
        /// 仍在途），就把源引用换成在途实体，让搬运照常走完 FinishTransfer 正常入库。
        /// 其余情况一律不改动。</summary>
        private static void AdvancePrefix(object channel)
        {
            if (!enabled || channel == null) return;
            object transfer = transferOf(channel);
            if (transfer == null) return;
            Thing source = thingOf(transfer);
            if (source != null && !source.Destroyed) return;
            OuterrealmBeamLease lease;
            if (Ledger?.TryGet(transfer, out lease) != true) return;
            Thing carried = carriedInTransitOf(channel);
            if (carried == null || carried.Destroyed) return;
            setThing(transfer, carried);
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
            if (manipulator == null)
            {
                return true;
            }

            if (destinationContainer is Building_MatterEnergyConverter converter)
            {
                CompTransporter transporter = converter.GetComp<CompTransporter>();
                ThingOwner innerContainer = transporter?.innerContainer;
                if (innerContainer == null)
                {
                    return true;
                }

                // MEC 同时继承 Building_Storage。光束通用容器路径会先调用其存储区
                // IHaulDestination.Accepts，错误地用地面存储筛选器拒绝装载模式中的物品。
                // 直接写入 CompTransporter.innerContainer；ThingOwner.NotifyAdded 会自动调用
                // CompTransporter.Notify_ThingAdded，扣减 leftToLoad 并刷新质量缓存。
                releaseInTransit(manipulator, carriedThing);
                bool mecAdded = innerContainer.TryAdd(carriedThing, true);
                if (!mecAdded) return true;
                __result = true;
                return false;
            }

            if (!(destinationContainer is Building_OuterrealmVault vault)
                || !CanDepositInto(vault, mapOf(op), carriedThing))
            {
                return true;
            }

            // BeamContainerUtility 以 Destroyed/stackCount/holdingOwner 判断交付成功；但 vault
            // 的不可堆叠权威实例（尸体等）按设计保持未生成且无 holder，会被误判失败并重新落地。
            // 在此直接以 vault 的 Deposit 结果为提交结果，并从光束在途容器解除，禁止 finally
            // ReturnInTransitThing + EnsureCarriedThingLanded 把已入库的权威实例再次放回地图。
            releaseInTransit(manipulator, carriedThing);
            bool added = vault.view.TryAdd(carriedThing, false);
            if (!added) return true;
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
            OuterrealmSource source;
            bool resolved = OuterrealmSourceResolver.TryResolve(thing, out source);
            bool allowed = resolved && Allowed(source, ForUse(transfer));
            if (!UnityData.IsInMainThread || stripOf(transfer) || !resolved || !allowed)
            {
                __result = false; return false;
            }
            int requested = countOf(transfer) > 0 ? Math.Min(countOf(transfer), thing.stackCount) : thing.stackCount;
            long available = Math.Max(0, source.Entry.Count - Storage.ReservedCountOf(source.Entry));
            requested = (int)Math.Min(requested, available);
            if (Ledger == null || !Ledger.TryAcquire(transfer, source, thing.Map, ownerKey, requested, available))
            {
                __result = false; return false;
            }
            __state = new EnqueueState { Acquired = true, AlreadyExcluded = excludedThings.Contains(thing) };
            setCount(transfer, requested);
            return true;
        }

        private static Exception EnqueueFinalizer(object transfer, object destinationQueue, HashSet<Thing> excludedThings,
            int ownerKey, ref bool __result, EnqueueState __state, Exception __exception)
        {
            if (__state.Acquired)
            {
                bool resized = false;
                bool valid = __exception == null && __result && (resized = Ledger.Resize(transfer, countOf(transfer)));
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
                OuterrealmBeamLease stale;
                if (Ledger?.TryGet(transfer, out stale) != true)
                {
                    return true;
                }
                Ledger.Release(transfer);
                __result = false;
                return false;
            }
            OuterrealmBeamLease lease = null;
            OuterrealmSource source = default(OuterrealmSource);
            string why = null;
            if (op == null) why = "op=null";
            else if (!UnityData.IsInMainThread) why = "非主线程";
            else if (Ledger == null) why = "Ledger=null";
            else if (!Ledger.TryGet(transfer, out lease)) why = "无租约";
            else if (lease.Extracting) why = "已在提取中";
            else if (lease.Owner != ownerOf(op)) why = "owner 不匹配";
            else if (lease.Map != mapOf(op)) why = "map 不匹配";
            else if (!OuterrealmSourceResolver.TryResolve(thingOf(transfer), out source)) why = "无法解析来源";
            else if (source.Entry != lease.Source.Entry) why = "条目已变化";
            else if (!Allowed(source, ForUse(transfer))) why = "权限不允许";
            if (why != null)
            {
                __result = false;
                return false;
            }
            __state = new LiftState { Previous = lift, Lease = lease, Storage = Storage };
            lift = __state;
            return true;
        }

        private static bool ExtractPrefix(object transfer, ref Thing __result)
        {
            if (!enabled || transfer == null) return true;
            if (!IsStored(thingOf(transfer)))
            {
                return true;
            }
            __result = null;
            LiftState state = lift;
            if (state == null || state.Lease.Transfer != transfer || state.Lease.Extracting)
            {
                return false;
            }
            OuterrealmBeamLease lease = state.Lease;
            int count = countOf(transfer);
            bool allowed = Allowed(lease.Source, ForUse(transfer));
            long avail = lease.Source.Entry.Count - state.Storage.ReservedCountOf(lease.Source.Entry) + lease.Count;
            if (count <= 0 || count != lease.Count || !allowed || avail < count)
            {
                return false;
            }
            if (Ledger == null || !Ledger.BeginExtraction(lease))
            {
                return false;
            }
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
                bool returned = actual != null && !actual.Destroyed && !actual.Spawned && actual.holdingOwner == null;
                if (returned)
                    __state.Storage.Deposit(actual, __state.Lease.Source.Vault);
            }
            finally
            {
                // 提取成功且物品在途时**不能**在这里结束提取：Checkout 把条目取空后，
                // 主 mod 会立即移除条目并同步销毁源投影，光束随后每 tick 检查
                // transfer.thing.Destroyed 并走 AbortChannel，把在途物品丢回 vault 格 →
                // 被 vault 吸收 → 投影重建 → 再次搬运 → 无限循环。
                // 因此租约与提取隔离保留到光束的搬运唯一终点 ReleaseClaim（ReleaseFinalizer）。
                // 回存（本次搬运失败）、提取失败或异常路径则立即结束，避免条目被永久冻结。
                try
                {
                    Thing actual = __state.Actual;
                    bool inTransit = actual != null && !actual.Destroyed && !actual.Spawned && actual.holdingOwner != null;
                    Ledger?.Release(__state.Lease.Transfer, __exception != null || !inTransit);
                }
                finally { lift = __state.Previous; }
            }
            return __exception;
        }

        private static Exception ReleaseFinalizer(object transfer, Exception __exception)
        {
            // 搬运唯一终点：此时才结束提取隔离并清租约（提取中的租约只在这里被真正释放）。
            Ledger?.Release(transfer, true);
            return __exception;
        }

        private static Exception ClearFinalizer(Map map, int ownerKey, Exception __exception)
        {
            Ledger?.ReleaseOwner(map, ownerKey);
            return __exception;
        }
    }
}
