using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    internal sealed class OuterrealmBillResourcePlan
    {
        public Pawn Pawn;
        public Job Job;
        public Map Map;
        public bool Recovering;
        // 总量任务保留原版 job.count，不向 countQueue 写入逐槽数量。
        public Dictionary<Thing, int> TargetRemaining;
        public List<LocalTargetInfo> TotalQueue;
        public readonly OuterrealmQuantityBudget<OuterrealmEntry> Remaining = new OuterrealmQuantityBudget<OuterrealmEntry>();
        public readonly Dictionary<Thing, OuterrealmSource> Sources = new Dictionary<Thing, OuterrealmSource>();
    }

    /// <summary>每局 DoBill 的未兑现预留；原版投影 reservation 仅作生命周期桥接，不重复计数。</summary>
    internal sealed class OuterrealmBillResourceLedger
    {
        private readonly Dictionary<Job, OuterrealmBillResourcePlan> plans = new Dictionary<Job, OuterrealmBillResourcePlan>();
        private readonly Dictionary<OuterrealmEntry, long> totals = new Dictionary<OuterrealmEntry, long>();
        private readonly HashSet<OuterrealmEntry> transfers = new HashSet<OuterrealmEntry>();
        private readonly ConditionalWeakTable<Pawn, FailureState> failures = new ConditionalWeakTable<Pawn, FailureState>();

        private sealed class FailureState
        {
            public int Tick = -1;
            public readonly HashSet<Bill> Bills = new HashSet<Bill>();
            public readonly HashSet<Thing> TotalTargets = new HashSet<Thing>();
        }

        public bool TryGet(Job job, out OuterrealmBillResourcePlan plan)
        {
            plan = null;
            return job != null && plans.TryGetValue(job, out plan);
        }

        public bool IsTransferring(OuterrealmEntry entry) => entry != null && transfers.Contains(entry);
        public bool TryBeginTransfer(OuterrealmEntry entry) => entry != null && transfers.Add(entry);
        public void EndTransfer(OuterrealmEntry entry) => transfers.Remove(entry);

        public long Own(Job job, OuterrealmEntry entry)
        {
            OuterrealmBillResourcePlan plan;
            return entry != null && TryGet(job, out plan) ? plan.Remaining.Get(entry) : 0;
        }

        public bool IsBridge(Job job, Thing target)
        {
            OuterrealmBillResourcePlan plan;
            return target != null && TryGet(job, out plan) && plan.Sources.ContainsKey(target);
        }

        public void AddTotalsTo(Dictionary<OuterrealmEntry, long> destination)
        {
            foreach (KeyValuePair<OuterrealmEntry, long> pair in totals)
            {
                long current;
                destination[pair.Key] = (destination.TryGetValue(pair.Key, out current) ? current : 0) + pair.Value;
            }
        }

        public bool Begin(OuterrealmBillResourcePlan plan)
        {
            if (plans.ContainsKey(plan.Job)) return true;
            GameComponent_OuterrealmStorage storage = GameComponent_OuterrealmStorage.Instance;
            Dictionary<OuterrealmEntry, long> legacyByEntry = null;
            if (plan.Recovering)
            {
                legacyByEntry = new Dictionary<OuterrealmEntry, long>();
                List<ReservationManager.Reservation> reservations = plan.Pawn.Map.reservationManager.ReservationsReadOnly;
                for (int i = 0; i < reservations.Count; i++)
                {
                    ReservationManager.Reservation reservation = reservations[i];
                    OuterrealmSource source;
                    if (reservation.Job != plan.Job || reservation.Claimant != plan.Pawn || reservation.Target.Thing == null
                        || !plan.Sources.TryGetValue(reservation.Target.Thing, out source)
                        || source.Kind != OuterrealmSourceKind.Projection) continue;
                    long old;
                    long count = reservation.StackCount < 0 ? Math.Min(source.Entry.Count, source.QueryThing.def.stackLimit) : reservation.StackCount;
                    legacyByEntry[source.Entry] = (legacyByEntry.TryGetValue(source.Entry, out old) ? old : 0) + count;
                }
            }
            foreach (KeyValuePair<OuterrealmEntry, long> pair in plan.Remaining.Entries)
            {
                if (IsTransferring(pair.Key)) return false;
                // 读档恢复时允许把该任务已有的地图预留迁移为精确预算，不能扣掉自身两次。
                long ownLegacy = 0;
                legacyByEntry?.TryGetValue(pair.Key, out ownLegacy);
                if (pair.Value > OuterrealmQuantityBudget<OuterrealmEntry>.Available(pair.Key.Count, storage.ReservedCountOf(pair.Key), ownLegacy))
                    return false;
            }
            plans.Add(plan.Job, plan);
            foreach (KeyValuePair<OuterrealmEntry, long> pair in plan.Remaining.Entries) Adjust(pair.Key, pair.Value);
            storage.NotifyReservationChanged();
            return true;
        }

        private void Adjust(OuterrealmEntry entry, long change)
        {
            long old;
            long next = (totals.TryGetValue(entry, out old) ? old : 0) + change;
            if (next <= 0) totals.Remove(entry);
            else totals[entry] = next;
        }

        public bool Spend(Job job, OuterrealmEntry entry, int count)
        {
            OuterrealmBillResourcePlan plan;
            if (IsTransferring(entry) || !TryGet(job, out plan) || !plan.Remaining.TrySpend(entry, count)) return false;
            transfers.Add(entry);
            Adjust(entry, -count);
            GameComponent_OuterrealmStorage.Instance.NotifyReservationChanged();
            return true;
        }

        public void Restore(OuterrealmBillResourcePlan plan, OuterrealmEntry entry, int count)
        {
            OuterrealmBillResourcePlan current;
            if (count <= 0 || !TryGet(plan.Job, out current) || current != plan) return;
            plan.Remaining.Add(entry, count);
            Adjust(entry, count);
            GameComponent_OuterrealmStorage.Instance.NotifyReservationChanged();
        }

        public void Release(Job job)
        {
            OuterrealmBillResourcePlan plan;
            if (!TryGet(job, out plan)) return;
            plans.Remove(job);
            foreach (KeyValuePair<OuterrealmEntry, long> pair in plan.Remaining.Entries) Adjust(pair.Key, -pair.Value);
            GameComponent_OuterrealmStorage.Instance.NotifyReservationChanged();
        }

        public void ReleasePawn(Pawn pawn)
        {
            List<Job> remove = null;
            foreach (KeyValuePair<Job, OuterrealmBillResourcePlan> pair in plans)
            {
                if (pair.Value.Pawn != pawn) continue;
                if (remove == null) remove = new List<Job>();
                remove.Add(pair.Key);
            }
            if (remove != null) for (int i = 0; i < remove.Count; i++) Release(remove[i]);
        }

        public void ReleaseMap(Map map)
        {
            List<Job> remove = null;
            foreach (KeyValuePair<Job, OuterrealmBillResourcePlan> pair in plans)
            {
                if (pair.Value.Map != map) continue;
                if (remove == null) remove = new List<Job>();
                remove.Add(pair.Key);
            }
            if (remove != null) for (int i = 0; i < remove.Count; i++) Release(remove[i]);
        }

        public bool IsTotalBlocked(Pawn pawn, Thing target)
        {
            FailureState state;
            return pawn != null && target != null && failures.TryGetValue(pawn, out state)
                && state.Tick == Find.TickManager.TicksGame && state.TotalTargets.Contains(target);
        }

        public bool IsBlocked(Pawn pawn, Bill bill)
        {
            FailureState state;
            return pawn != null && bill != null && failures.TryGetValue(pawn, out state)
                && state.Tick == Find.TickManager.TicksGame && state.Bills.Contains(bill);
        }

        public void Block(Pawn pawn, Job job, string reason)
        {
            if (pawn == null || job == null || (job.bill == null && !OuterrealmTotalJobUtility.Supports(job))) return;
            FailureState state = failures.GetOrCreateValue(pawn);
            int tick = Find.TickManager.TicksGame;
            if (state.Tick != tick) { state.Tick = tick; state.Bills.Clear(); state.TotalTargets.Clear(); }
            if (job.bill == null)
            {
                if (job.targetA.Thing != null && state.TotalTargets.Add(job.targetA.Thing) && Prefs.DevMode)
                    Log.Warning("[OuterrealmStorage] Total resource plan rejected: pawn=" + pawn.ThingID + ", reason=" + reason + ", tick=" + tick);
                return;
            }
            if (state.Bills.Add(job.bill) && Prefs.DevMode)
                Log.Warning("[OuterrealmStorage] DoBill resource plan rejected: pawn=" + pawn.ThingID + ", recipe="
                    + job.bill.recipe.defName + ", reason=" + reason + ", tick=" + tick);
        }
    }
}
