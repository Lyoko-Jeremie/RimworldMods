using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>DoBill 的预算、来源校验和取料事务。查询物只在所有权边界兑换，队列按槽位迁移。</summary>
    internal static class OuterrealmBillJobUtility
    {
        internal static OuterrealmBillResourceLedger Ledger => GameComponent_OuterrealmStorage.Instance?.Runtime.Bills;
        internal static bool IsBill(Job job) => job?.def == JobDefOf.DoBill;
        // 只有逐槽 countQueue 的驱动共用此协议；总量递减的 Reload/RefuelAtomic 不适用。
        internal static bool UsesIngredientQueue(Job job) => IsBill(job) || job?.def == JobDefOf.EnterBiosculpterPod;
        internal static bool Managed(in OuterrealmSource source) => source.Kind == OuterrealmSourceKind.Projection || source.Kind == OuterrealmSourceKind.SubspaceCanonical;

        internal static bool CanUse(in OuterrealmSource source, Pawn pawn)
        {
            if (pawn?.Map == null || source.Entry == null || source.Entry.Count <= 0) return false;
            if (source.Kind == OuterrealmSourceKind.SubspaceCanonical) return SubspaceAccessUtility.CanAutoTake(pawn);
            Building_OuterrealmVault vault = source.Vault;
            return vault != null && vault.Spawned && vault.Map == pawn.Map && !vault.Frozen
                && (!vault.NoWithdraw || vault.AllowTakeForUse) && vault.CanShow(source.QueryThing)
                && !source.QueryThing.IsForbidden(pawn);
        }

        internal static long Available(OuterrealmSource source, Pawn pawn, Job job)
        {
            if (!CanUse(source, pawn) || Ledger?.IsTransferring(source.Entry) == true) return 0;
            return OuterrealmQuantityBudget<OuterrealmEntry>.Available(source.Entry.Count,
                GameComponent_OuterrealmStorage.Instance.ReservedCountOf(source.Entry), Ledger?.Own(job, source.Entry) ?? 0);
        }

        private static bool AddTarget(OuterrealmBillResourcePlan plan, Thing thing, int count)
        {
            OuterrealmSource source;
            if (!OuterrealmSourceResolver.TryResolve(thing, out source)) return !OuterrealmVaultUtil.IsProjection(thing);
            if (!Managed(source)) return true;
            if (count <= 0 || !CanUse(source, plan.Pawn)) return false;
            plan.Sources[thing] = source;
            plan.Remaining.Add(source.Entry, count);
            return plan.Remaining.Get(source.Entry) <= int.MaxValue;
        }

        private static bool BuildPlan(Pawn pawn, Job job, bool includeCurrent, out OuterrealmBillResourcePlan plan)
        {
            plan = new OuterrealmBillResourcePlan { Pawn = pawn, Job = job, Map = pawn.Map, Recovering = includeCurrent };
            List<LocalTargetInfo> queue = job.targetQueueB;
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (!AddTarget(plan, queue[i].Thing, job.countQueue != null && i < job.countQueue.Count ? job.countQueue[i] : 0)) return false;
            if (includeCurrent && !AddTarget(plan, job.targetB.Thing, job.count)) return false;
            return plan.Sources.Count == 0 || queue.NullOrEmpty()
                || (job.countQueue != null && job.countQueue.Count == queue.Count);
        }

        // 返回是否接管；接管时 result 表示整个预留事务是否成功。
        internal static bool Prepare(JobDriver driver, bool errorOnFailed, out bool result)
        {
            result = false;
            Job job = driver.job;
            if (!UsesIngredientQueue(job) || Ledger == null) return false;
            bool affected = false;
            if (job.targetQueueB != null)
                for (int i = 0; i < job.targetQueueB.Count; i++)
                {
                    Thing target = job.targetQueueB[i].Thing;
                    OuterrealmSource source;
                    if (OuterrealmVaultUtil.IsProjection(target)
                        || (OuterrealmSourceResolver.TryResolve(target, out source) && Managed(source)))
                    { affected = true; break; }
                }
            if (!affected) return false;
            OuterrealmBillResourcePlan plan;
            bool valid = BuildPlan(driver.pawn, job, false, out plan);
            if (valid && plan.Sources.Count == 0) return false;
            try
            {
                if (!valid || Ledger.IsBlocked(driver.pawn, job.bill) || !Ledger.Begin(plan)) return true;
                Ledger.TryGet(job, out plan);
                Pawn pawn = driver.pawn;
                Thing giver = job.targetA.Thing;
                if (!pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed)
                    || (IsBill(job) && giver != null && giver.def.hasInteractionCell && !pawn.ReserveSittableOrSpot(giver.InteractionCell, job, errorOnFailed))) return true;
                result = ReserveTargets(pawn, job, plan, false, errorOnFailed);
                return true;
            }
            finally
            {
                if (!result)
                {
                    Ledger.Block(driver.pawn, job, "ingredient-reservation");
                    // 未开始任何 toil 时，也应同步清理本次取得的实物和预留。
                    driver.pawn.Map?.reservationManager.ReleaseClaimedBy(driver.pawn, job);
                    Ledger.Release(job);
                    SubspaceAccessUtility.ReturnUnreservedPending();
                }
            }
        }

        private static bool ReserveTargets(Pawn pawn, Job job, OuterrealmBillResourcePlan plan, bool includeCurrent, bool errorOnFailed)
        {
            List<LocalTargetInfo> queue = job.targetQueueB;
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (!ReserveTarget(pawn, job, plan, queue[i].Thing, job.countQueue[i], i, errorOnFailed)) return false;
            if (includeCurrent && !ReserveTarget(pawn, job, plan, job.targetB.Thing, job.count, -1, errorOnFailed)) return false;
            // 已交付的权威原堆可能与候选是同一对象。完成提前取出后移除旧候选身份，避免重复预留再次取出。
            List<Thing> canonicalSources = null;
            foreach (KeyValuePair<Thing, OuterrealmSource> pair in plan.Sources)
            {
                if (pair.Value.Kind != OuterrealmSourceKind.SubspaceCanonical) continue;
                if (canonicalSources == null) canonicalSources = new List<Thing>();
                canonicalSources.Add(pair.Key);
            }
            if (canonicalSources != null) for (int i = 0; i < canonicalSources.Count; i++) plan.Sources.Remove(canonicalSources[i]);
            return true;
        }

        internal static bool ReserveTarget(Pawn pawn, Job job, OuterrealmBillResourcePlan plan, Thing thing, int count, int queueIndex, bool errorOnFailed)
        {
            if (thing == null) return true;
            OuterrealmSource source;
            if (!plan.Sources.TryGetValue(thing, out source))
                return pawn.Reserve(thing, job, stackCount: count, errorOnFailed: errorOnFailed);
            if (source.IsVaultQuery)
                return pawn.Reserve(thing, job, stackCount: (int)plan.Remaining.Get(source.Entry), errorOnFailed: errorOnFailed);

            // 随身候选无可寻路的查询位置，保留提前取出的例外。使用原始来源快照处理重复目标。
            if (!Ledger.Spend(job, source.Entry, count)) return false;
            Thing actual = null;
            bool committed = false;
            try
            {
                actual = OuterrealmSourceResolver.Checkout(source, count);
                if (actual == null || actual.Destroyed || actual.stackCount != count) return false;
                GenSpawn.Spawn(actual, pawn.Position, pawn.Map);
                SubspaceAccessUtility.MarkPendingCheckout(actual);
                if (queueIndex >= 0) job.targetQueueB[queueIndex] = actual;
                else job.SetTarget(TargetIndex.B, actual);
                if (!pawn.Reserve(actual, job, stackCount: count, errorOnFailed: errorOnFailed)) return false;
                committed = true;
                return true;
            }
            finally
            {
                try
                {
                    if (!committed)
                    {
                        if (actual != null && !actual.Destroyed && actual.holdingOwner == null)
                        {
                            if (actual.Spawned) actual.DeSpawn();
                            GameComponent_OuterrealmStorage.Instance.Deposit(actual);
                        }
                        Ledger.Restore(plan, source.Entry, count);
                    }
                }
                finally { Ledger.EndTransfer(source.Entry); }
            }
        }

        internal static bool EnsurePlan(Pawn pawn, Job job)
        {
            if (OuterrealmTotalJobUtility.Supports(job)) return OuterrealmTotalJobUtility.Ensure(pawn, job, true);
            OuterrealmBillResourcePlan plan;
            if (Ledger.TryGet(job, out plan)) return true;
            // 读档恢复的活跃任务不重新调用 TryMakePreToilReservations；在第一个执行边界重建。
            if (!BuildPlan(pawn, job, true, out plan) || !Ledger.Begin(plan)) return false;
            return ReserveTargets(pawn, job, plan, true, false);
        }

        private static bool Validate(Pawn pawn, Job job, OuterrealmSource source, int required)
        {
            if (Ledger.IsTransferring(source.Entry) || !EnsurePlan(pawn, job) || required <= 0 || !CanUse(source, pawn)) return false;
            long own = Ledger.Own(job, source.Entry);
            return own >= required && Available(source, pawn, job) >= own;
        }

        internal static void Fail(Pawn pawn, Job job, string reason)
        {
            Ledger?.Block(pawn, job, reason);
            if (pawn?.CurJob == job) pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
            else Ledger?.Release(job);
        }

        internal static bool Extract(Toil toil, TargetIndex index, bool failIfTooBig)
        {
            if (OuterrealmTotalJobUtility.Extract(toil, index)) return true;
            Pawn pawn = toil.actor;
            Job job = pawn?.CurJob;
            if (!UsesIngredientQueue(job) || index != TargetIndex.B || job.targetQueueB.NullOrEmpty()) return false;
            Thing thing = job.targetQueueB[0].Thing;
            OuterrealmSource source;
            if (!OuterrealmSourceResolver.TryResolve(thing, out source))
            {
                if (!OuterrealmVaultUtil.IsProjection(thing)) return false;
                Fail(pawn, job, "expired-projection");
                return true;
            }
            if (!Managed(source)) return false;
            if (!EnsurePlan(pawn, job)) { Fail(pawn, job, "restore-plan"); return true; }
            thing = job.targetQueueB[0].Thing;
            if (!OuterrealmSourceResolver.TryResolve(thing, out source)) return false;
            int need = job.countQueue.NullOrEmpty() ? 0 : job.countQueue[0];
            if (!Validate(pawn, job, source, need)) { Fail(pawn, job, "queue-budget"); return true; }
            if (pawn.CurJob != job) return true;
            // 不关闭其他原版调用方的数量检查；此分支只处理已验证预算的 DoBill 超维目标。
            job.SetTarget(index, job.targetQueueB[0]);
            job.targetQueueB.RemoveAt(0);
            job.count = need;
            job.countQueue.RemoveAt(0);
            return true;
        }

        internal static bool StartCarry(Toil toil, TargetIndex index, bool putRemainderInQueue,
            bool subtractNumTakenFromJobCount, bool failIfStackCountLessThanJobCount, bool reserve)
        {
            if (OuterrealmTotalJobUtility.StartCarry(toil, index, reserve)) return true;
            Pawn pawn = toil.actor;
            Job job = pawn?.CurJob;
            if (!UsesIngredientQueue(job) || index != TargetIndex.B) return false;
            Thing query = job.GetTarget(index).Thing;
            OuterrealmSource source;
            if (!OuterrealmSourceResolver.TryResolve(query, out source))
            {
                if (!OuterrealmVaultUtil.IsProjection(query)) return false;
                Fail(pawn, job, "expired-carry-target"); return true;
            }
            if (!Managed(source)) return false;
            if (!EnsurePlan(pawn, job)) { Fail(pawn, job, "restore-plan"); return true; }
            query = job.GetTarget(index).Thing;
            if (!OuterrealmSourceResolver.TryResolve(query, out source)) return false;
            int need = job.count;
            if (!Validate(pawn, job, source, need)) { Fail(pawn, job, "carry-budget"); return true; }
            // 恢复随身候选时 EnsurePlan 可能把当前目标兑换成地图实物，随后应使用原版搬运。
            if (job.GetTarget(index).Thing != query || !OuterrealmSourceResolver.TryResolve(query, out source)) return false;
            int request = Math.Min(need, pawn.carryTracker.AvailableStackSpace(query.def));
            int taken = request > 0 ? pawn.carryTracker.TryStartCarry(query, request, reserve) : 0;
            if (pawn.CurJob != job) return true;
            if (taken <= 0) { Fail(pawn, job, "carry-rejected"); return true; }
            if (putRemainderInQueue && taken < need)
            {
                job.GetTargetQueue(index).Insert(0, query);
                job.countQueue.Insert(0, need - taken);
            }
            if (subtractNumTakenFromJobCount) job.count -= taken;
            job.SetTarget(index, pawn.carryTracker.CarriedThing);
            pawn.records.Increment(RecordDefOf.ThingsHauled);
            return true;
        }

        internal static int CarryFromProjection(Pawn_CarryTracker carry, OuterrealmSource source, int count, bool reserve)
        {
            Pawn pawn = carry.pawn;
            Job job = pawn.CurJob;
            if (pawn.Dead || pawn.Downed || !Validate(pawn, job, source, OuterrealmTotalJobUtility.Supports(job) ? count : job.count)
                || (carry.CarriedThing != null && !carry.CarriedThing.CanStackWith(source.QueryThing))) return 0;
            count = Math.Min(count, carry.AvailableStackSpace(source.QueryThing.def));
            OuterrealmBillResourcePlan plan;
            if (count <= 0 || !Ledger.TryGet(job, out plan) || !Ledger.Spend(job, source.Entry, count)) return 0;
            Thing actual = null;
            int delivered = 0;
            int before = carry.CarriedThing?.stackCount ?? 0;
            try
            {
                actual = OuterrealmSourceResolver.Checkout(source, count);
                if (actual == null || actual.Destroyed || actual.stackCount <= 0) return 0;
                // 不再次对取得物调用带数量的 TryAdd，避免无意义的第二次 SplitOff。
                carry.innerContainer.TryAdd(actual, true);
                delivered = Math.Max(0, Math.Min(count, (carry.CarriedThing?.stackCount ?? 0) - before));
                if (pawn.CurJob != job) return delivered;
                if (delivered > 0)
                {
                    job.SetTarget(TargetIndex.B, carry.CarriedThing);
                    if (reserve) pawn.Reserve(carry.CarriedThing, job);
                    source.QueryThing.def.soundPickup.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                    pawn.Map.resourceCounter.UpdateResourceCounts();
                }
                return delivered;
            }
            finally
            {
                // 容器回调可在交付后抛异常。以真实所有权/合并结果为准，不能把已交付物再存一遍。
                delivered = Math.Max(delivered, Math.Max(0, Math.Min(count, (carry.CarriedThing?.stackCount ?? 0) - before)));
                try
                {
                    if (actual != null && !actual.Destroyed && actual.holdingOwner == null && !actual.Spawned && actual.stackCount > 0)
                        GameComponent_OuterrealmStorage.Instance.Deposit(actual);
                    else if (actual != null && (actual.holdingOwner != null || actual.Spawned))
                        delivered = Math.Max(delivered, Math.Min(count, actual.stackCount));
                    Ledger.Restore(plan, source.Entry, count - delivered);
                }
                finally { Ledger.EndTransfer(source.Entry); }
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_DoBill), "TryMakePreToilReservations")]
    internal static class Patch_OuterrealmBillReservations
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(JobDriver_DoBill __instance, bool errorOnFailed, ref bool __result)
        {
            bool result;
            if (!OuterrealmBillJobUtility.Prepare(__instance, errorOnFailed, out result)) return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver_EnterBiosculpterPod), "TryMakePreToilReservations")]
    internal static class Patch_OuterrealmBiosculpterReservations
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(JobDriver_EnterBiosculpterPod __instance, bool errorOnFailed, ref bool __result)
        {
            bool result;
            if (!OuterrealmBillJobUtility.Prepare(__instance, errorOnFailed, out result)) return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(Toils_JobTransforms), "ExtractNextTargetFromQueue")]
    internal static class Patch_OuterrealmBillExtract
    {
        private static void Postfix(Toil __result, TargetIndex ind, bool failIfCountFromQueueTooBig)
        {
            Action original = __result.initAction;
            __result.initAction = () =>
            {
                if (!OuterrealmBillJobUtility.Extract(__result, ind, failIfCountFromQueueTooBig)) original?.Invoke();
            };
        }
    }

    [HarmonyPatch(typeof(JobDriver), "Cleanup")]
    internal static class Patch_OuterrealmBillCleanup
    {
        private static void Prefix(JobDriver __instance, JobCondition condition)
        {
            OuterrealmBillResourcePlan plan;
            if (OuterrealmBillJobUtility.Ledger?.TryGet(__instance.job, out plan) == true
                && (condition == JobCondition.Incompletable || condition == JobCondition.Errored))
                OuterrealmBillJobUtility.Ledger.Block(__instance.pawn, __instance.job, "job-" + condition);
        }

        private static Exception Finalizer(JobDriver __instance, Exception __exception)
        {
            OuterrealmBillJobUtility.Ledger?.Release(__instance.job);
            return __exception;
        }
    }
}
