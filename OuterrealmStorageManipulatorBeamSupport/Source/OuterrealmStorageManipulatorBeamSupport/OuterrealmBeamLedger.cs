using System;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using Verse;

namespace OuterrealmStorageManipulatorBeamSupport
{
    internal sealed class OuterrealmBeamLease
    {
        public object Transfer;
        public OuterrealmSource Source;
        public Map Map;
        public int Owner;
        public int Count;
        public bool Extracting;
    }

    /// <summary>
    /// 每局光束未兑现数量。只保存查询身份，不拥有实物；所有访问共用锁。
    /// 通过 IOuterrealmExternalReservation 把预留注入超维存储的全局可用量口径，
    /// 使普通 Pawn 预留与光束预留互相可见。
    /// </summary>
    internal sealed class OuterrealmBeamLedger : IOuterrealmExternalReservation
    {
        private readonly object gate = new object();
        private readonly Dictionary<object, OuterrealmBeamLease> leases = new Dictionary<object, OuterrealmBeamLease>();
        private readonly Dictionary<OuterrealmEntry, long> totals = new Dictionary<OuterrealmEntry, long>();
        private readonly Dictionary<Thing, Dictionary<int, long>> ownTotals = new Dictionary<Thing, Dictionary<int, long>>();
        private readonly HashSet<OuterrealmEntry> extracting = new HashSet<OuterrealmEntry>();
        private readonly Action changed;
        private readonly Action<Thing> released;

        public OuterrealmBeamLedger(Action changed = null, Action<Thing> released = null)
        { this.changed = changed; this.released = released; }

        public bool IsExtracting(OuterrealmEntry entry) { lock (gate) return extracting.Contains(entry); }

        public long Reserved(OuterrealmEntry entry)
        {
            lock (gate) return entry != null && totals.TryGetValue(entry, out long n) ? n : 0;
        }

        public long Own(Thing query, int owner)
        {
            lock (gate)
            {
                return query != null && ownTotals.TryGetValue(query, out Dictionary<int, long> owners)
                    && owners.TryGetValue(owner, out long count) ? count : 0;
            }
        }

        public bool TryGet(object transfer, out OuterrealmBeamLease lease)
        {
            lock (gate) return leases.TryGetValue(transfer, out lease);
        }

        public bool TryAcquire(object transfer, in OuterrealmSource source, Map map, int owner, int requested, long available)
        {
            lock (gate)
            {
                if (requested <= 0 || available < requested || leases.ContainsKey(transfer)) return false;
                leases.Add(transfer, new OuterrealmBeamLease
                {
                    Transfer = transfer, Source = source, Map = map, Owner = owner, Count = requested
                });
                totals[source.Entry] = Reserved(source.Entry) + requested;
                AdjustOwn(source.QueryThing, owner, requested);
                changed?.Invoke();
                return true;
            }
        }

        // 目的地可把数量缩小；已经申请的数量绝不能在入队后扩大。
        public bool Resize(object transfer, int count)
        {
            lock (gate)
            {
                if (!leases.TryGetValue(transfer, out OuterrealmBeamLease lease)
                    || count <= 0 || count > lease.Count) return false;
                if (count == lease.Count) return true;
                totals[lease.Source.Entry] -= lease.Count - count;
                AdjustOwn(lease.Source.QueryThing, lease.Owner, count - lease.Count);
                lease.Count = count;
                changed?.Invoke();
                return true;
            }
        }

        public void Release(object transfer, bool finishExtraction = false)
        {
            if (transfer == null) return;
            lock (gate)
            {
                if (!leases.TryGetValue(transfer, out OuterrealmBeamLease lease)) return;
                // Checkout 回调中取消设备也不能提前解除提交隔离；外层提取结束后统一释放。
                if (lease.Extracting && !finishExtraction) return;
                leases.Remove(transfer);
                if (lease.Extracting) extracting.Remove(lease.Source.Entry);
                else AdjustOwn(lease.Source.QueryThing, lease.Owner, -lease.Count);
                long remaining = Reserved(lease.Source.Entry) - lease.Count;
                if (remaining <= 0) totals.Remove(lease.Source.Entry);
                else totals[lease.Source.Entry] = remaining;
                changed?.Invoke();
                released?.Invoke(lease.Source.QueryThing);
            }
        }

        public bool BeginExtraction(OuterrealmBeamLease lease)
        {
            lock (gate)
            {
                if (lease.Extracting || !leases.ContainsKey(lease.Transfer) || !extracting.Add(lease.Source.Entry)) return false;
                lease.Extracting = true;
                AdjustOwn(lease.Source.QueryThing, lease.Owner, -lease.Count);
                return true;
            }
        }

        private void AdjustOwn(Thing query, int owner, long delta)
        {
            if (!ownTotals.TryGetValue(query, out Dictionary<int, long> owners))
                ownTotals.Add(query, owners = new Dictionary<int, long>());
            owners.TryGetValue(owner, out long old);
            if (old + delta <= 0) owners.Remove(owner);
            else owners[owner] = old + delta;
            if (owners.Count == 0) ownTotals.Remove(query);
        }

        // 设备拆除/复位要求释放全部 claim 时必须强制结束"提取中"的租约；
        // 否则它们会因 Release 的保留语义（提取中的租约等外层提取结束才释放）而永远留在账本里。
        public void ReleaseOwner(Map map, int owner) => ReleaseMatching(map, owner, null, force: true);
        public void ForgetMap(Map map) => ReleaseMatching(map, null, null, force: false);
        public void ForgetVault(Building_OuterrealmVault vault) => ReleaseMatching(null, null, vault, force: false);

        private void ReleaseMatching(Map map, int? owner, Building_OuterrealmVault vault, bool force)
        {
            lock (gate)
            {
                List<object> remove = null;
                foreach (OuterrealmBeamLease lease in leases.Values)
                    if ((map == null || lease.Map == map) && (!owner.HasValue || lease.Owner == owner.Value)
                        && (vault == null || lease.Source.Vault == vault) && (force || !lease.Extracting))
                    {
                        if (remove == null) remove = new List<object>();
                        remove.Add(lease.Transfer);
                    }
                // 强制路径必须传 finishExtraction=true，否则 Release 会按保留语义直接返回、什么也不清。
                if (remove != null) for (int i = 0; i < remove.Count; i++) Release(remove[i], force);
            }
        }

        // ── IOuterrealmExternalReservation：把光束预留注入超维存储的全局可用量口径 ──

        long IOuterrealmExternalReservation.ReservedFor(OuterrealmEntry entry) => Reserved(entry);

        // 查询上下文（当前线程/当前物品/当前操作者）由适配器维护，这里只做转发。
        bool IOuterrealmExternalReservation.HasForeignReservation(Pawn caller, Thing query, OuterrealmEntry entry)
            => OuterrealmBeamAdapter.HasForeignReservation(caller, query, entry);

        long IOuterrealmExternalReservation.AdjustAvailable(Pawn caller, Thing query, long available)
            => OuterrealmBeamAdapter.ReservationAvailable(caller, query, available);

        int IOuterrealmExternalReservation.AdjustRequest(Pawn caller, Thing query, int requested, long available)
            => OuterrealmBeamAdapter.ReservationRequest(caller, query, requested, available);
    }
}
