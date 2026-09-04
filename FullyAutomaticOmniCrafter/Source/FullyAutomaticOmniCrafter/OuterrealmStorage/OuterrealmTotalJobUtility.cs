using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>装填/原子补燃料的总量协议。count 是全任务余量，队列仅保存取料路线。</summary>
    internal static class OuterrealmTotalJobUtility
    {
        private static OuterrealmBillResourceLedger Ledger => OuterrealmBillJobUtility.Ledger;
        internal static bool Supports(Job job) => job != null && (job.def == JobDefOf.Reload || job.def == JobDefOf.RefuelAtomic);

        internal static bool HasSource(Job job, bool current)
        {
            if (current && IsSource(job.targetB.Thing)) return true;
            if (job.targetQueueB != null)
                for (int i = 0; i < job.targetQueueB.Count; i++) if (IsSource(job.targetQueueB[i].Thing)) return true;
            return false;
        }

        private static bool IsSource(Thing thing)
        {
            OuterrealmSource source;
            return OuterrealmVaultUtil.IsProjection(thing) || OuterrealmSourceResolver.TryResolve(thing, out source);
        }

        internal static bool Build(Pawn pawn, Job job, int demand, bool current, bool recovering, out OuterrealmBillResourcePlan plan)
        {
            plan = new OuterrealmBillResourcePlan { Pawn = pawn, Job = job, Map = pawn.Map, Recovering = recovering,
                TargetRemaining = new Dictionary<Thing, int>(), TotalQueue = new List<LocalTargetInfo>() };
            if (demand <= 0 || !job.countQueue.NullOrEmpty()) return false;
            var seen = new HashSet<object>();
            // 出队前恢复不能把上一趟 B 的实物重新计入；携带前恢复才包含当前 B。
            if (current && !Allocate(plan, job.targetB.Thing, false, seen, ref demand)) return false;
            if (job.targetQueueB != null)
                for (int i = 0; i < job.targetQueueB.Count && demand > 0; i++)
                    if (!Allocate(plan, job.targetQueueB[i].Thing, true, seen, ref demand)) return false;
            return demand == 0;
        }

        private static bool Allocate(OuterrealmBillResourcePlan plan, Thing thing, bool queued, HashSet<object> seen, ref int demand)
        {
            if (thing == null || thing.Destroyed) return false;
            OuterrealmSource source;
            bool storage = OuterrealmSourceResolver.TryResolve(thing, out source);
            if (!storage && OuterrealmVaultUtil.IsProjection(thing)) return false;
            object key = storage ? (object)source.Entry : thing;
            if (!seen.Add(key)) return true;
            if (storage && !OuterrealmBillJobUtility.CanUse(source, plan.Pawn)) return false;
            // 恢复时先按库存分配，再由 Begin 将本任务旧桥接预留扣除后整体校验。
            long available = storage ? (plan.Recovering ? source.Entry.Count : OuterrealmBillJobUtility.Available(source, plan.Pawn, null)) : thing.stackCount;
            int take = (int)Math.Min(demand, available);
            if (take <= 0) return true;
            plan.TargetRemaining.Add(thing, take);
            if (queued) plan.TotalQueue.Add(thing);
            if (storage)
            {
                plan.Sources.Add(thing, source);
                plan.Remaining.Add(source.Entry, take);
            }
            demand -= take;
            return true;
        }

        internal static bool Prepare(JobDriver driver, int demand, bool errorOnFailed, out bool result)
        {
            result = false;
            Job job = driver.job;
            if (!Supports(job) || Ledger == null || !HasSource(job, false)) return false;
            OuterrealmBillResourcePlan plan;
            try
            {
                if (Ledger.IsTotalBlocked(driver.pawn, job.targetA.Thing)) return true;
                if (!Ledger.TryGet(job, out plan))
                {
                    if (!Build(driver.pawn, job, demand, false, false, out plan) || !Ledger.Begin(plan)) return true;
                    job.count = demand;
                    job.targetQueueB = plan.TotalQueue;
                }
                if (job.def == JobDefOf.RefuelAtomic && !driver.pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed)) return true;
                result = Reserve(driver.pawn, job, plan, false, errorOnFailed);
                return true;
            }
            finally
            {
                if (!result)
                {
                    Ledger.Block(driver.pawn, job, "total-reservation");
                    driver.pawn.Map?.reservationManager.ReleaseClaimedBy(driver.pawn, job);
                    Ledger.Release(job);
                    SubspaceAccessUtility.ReturnUnreservedPending();
                }
            }
        }

        private static bool Reserve(Pawn pawn, Job job, OuterrealmBillResourcePlan plan, bool current, bool error)
        {
            if (current && !ReserveOne(pawn, job, plan, job.targetB.Thing, -1, error)) return false;
            for (int i = 0; i < job.targetQueueB.Count; i++)
                if (!ReserveOne(pawn, job, plan, job.targetQueueB[i].Thing, i, error)) return false;
            return true;
        }

        private static bool ReserveOne(Pawn pawn, Job job, OuterrealmBillResourcePlan plan, Thing target, int index, bool error)
        {
            int count;
            if (target == null || !plan.TargetRemaining.TryGetValue(target, out count) || count <= 0) return false;
            if (!OuterrealmBillJobUtility.ReserveTarget(pawn, job, plan, target, count, index, error)) return false;
            Thing actual = index < 0 ? job.targetB.Thing : job.targetQueueB[index].Thing;
            if (actual != target)
            {
                plan.TargetRemaining.Remove(target);
                plan.TargetRemaining[actual] = count;
            }
            // 随身主堆可能原样被取空返回；无论是否同引用，都清掉已经兑现的来源身份。
            OuterrealmSource source;
            if (plan.Sources.TryGetValue(target, out source) && source.Kind == OuterrealmSourceKind.SubspaceCanonical)
                plan.Sources.Remove(target);
            return true;
        }

        internal static bool Ensure(Pawn pawn, Job job, bool current)
        {
            OuterrealmBillResourcePlan plan;
            if (Ledger.TryGet(job, out plan)) return plan.TargetRemaining != null;
            if (!Build(pawn, job, job.count, current, true, out plan) || !Ledger.Begin(plan)) return false;
            job.targetQueueB = plan.TotalQueue;
            return Reserve(pawn, job, plan, current, false);
        }

        internal static bool Extract(Toil toil, TargetIndex index)
        {
            Pawn pawn = toil.actor;
            Job job = pawn?.CurJob;
            if (!Supports(job) || index != TargetIndex.B || Ledger == null) return false;
            OuterrealmBillResourcePlan plan;
            if (!Ledger.TryGet(job, out plan) && !HasSource(job, false)) return false;
            if (!Ensure(pawn, job, false) || job.targetQueueB.NullOrEmpty())
            { OuterrealmBillJobUtility.Fail(pawn, job, "total-queue"); return true; }
            job.SetTarget(index, job.targetQueueB[0]);
            job.targetQueueB.RemoveAt(0);
            // 不能把 count 改成这一来源的数量；原版后续 toil 会按全任务余量递减。
            return true;
        }

        internal static bool StartCarry(Toil toil, TargetIndex index, bool reserve)
        {
            Pawn pawn = toil.actor;
            Job job = pawn?.CurJob;
            if (!Supports(job) || index != TargetIndex.B || Ledger == null) return false;
            OuterrealmBillResourcePlan plan;
            if (!Ledger.TryGet(job, out plan) && !HasSource(job, true)) return false;
            if (!Ensure(pawn, job, true) || !Ledger.TryGet(job, out plan))
            { OuterrealmBillJobUtility.Fail(pawn, job, "total-restore"); return true; }
            Thing query = job.targetB.Thing;
            int outstanding;
            if (query == null || !plan.TargetRemaining.TryGetValue(query, out outstanding) || outstanding <= 0 || job.count <= 0)
            { OuterrealmBillJobUtility.Fail(pawn, job, "total-demand"); return true; }
            int request = Math.Min(Math.Min(outstanding, job.count), pawn.carryTracker.AvailableStackSpace(query.def));
            OuterrealmSource source;
            bool storage = OuterrealmSourceResolver.TryResolve(query, out source);
            if (storage && (Ledger.Own(job, source.Entry) < request || OuterrealmBillJobUtility.Available(source, pawn, job) < request)) request = 0;
            if (!storage && (OuterrealmVaultUtil.IsProjection(query) || !query.Spawned || query.IsForbidden(pawn))) request = 0;
            if (!storage) request = Math.Min(request, query.stackCount);
            int taken = request > 0 ? pawn.carryTracker.TryStartCarry(query, request, reserve) : 0;
            if (pawn.CurJob != job) return true;
            if (taken <= 0) { OuterrealmBillJobUtility.Fail(pawn, job, "total-carry"); return true; }
            plan.TargetRemaining[query] = outstanding - taken;
            job.count -= taken;
            // 原版总量任务不自动回队；剩余计划必须保留，才能继续搬超过 carry 容量的库存。
            if (outstanding > taken && job.count > 0) job.targetQueueB.Insert(0, query);
            job.SetTarget(index, pawn.carryTracker.CarriedThing);
            pawn.records.Increment(RecordDefOf.ThingsHauled);
            return true;
        }
    }
}
