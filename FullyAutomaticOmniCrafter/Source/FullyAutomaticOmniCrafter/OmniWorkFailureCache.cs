using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>地图内代理共享失败隔离；只在主线程调度中使用，不写入存档。</summary>
    internal sealed class OmniWorkFailureCache
    {
        private readonly struct Key : IEquatable<Key>
        {
            internal readonly WorkGiverDef giver;
            internal readonly LocalTargetInfo target;
            internal readonly Bill bill;
            internal Key(WorkGiverDef giver, LocalTargetInfo target, Bill bill = null) { this.giver = giver; this.target = target; this.bill = bill; }
            public bool Equals(Key other) => giver == other.giver && target == other.target && bill == other.bill;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => ((giver?.GetHashCode() ?? 0) * 397 ^ target.GetHashCode()) * 397 ^ (bill?.GetHashCode() ?? 0);
        }

        private struct Failure
        {
            internal int count;
            internal int retryAt;
            internal int forgetAt;
            internal int probePawn;
            internal IntVec3 position;
        }

        private struct Attempt
        {
            internal Job job;
            internal int startTick;
            internal Key key;
        }

        private readonly Dictionary<Key, Failure> failures = new Dictionary<Key, Failure>();
        private readonly Dictionary<Pawn, Attempt> attempts = new Dictionary<Pawn, Attempt>();
        private readonly HashSet<Pawn> navigationFailures = new HashSet<Pawn>();
        private readonly List<Key> expired = new List<Key>();
        private static int Now => GenTicks.TicksGame;

        private static Key JobKey(Job job)
        {
            // 建造搬运的 A 是材料，C 才是最初被扫描选中的建造需求。
            LocalTargetInfo target = job.def == JobDefOf.HaulToContainer && job.targetC.IsValid
                ? job.targetC : job.targetA;
            return new Key(job.bill == null ? job.workGiverDef : null, target, job.bill);
        }

        private bool Blocked(Key key, Pawn pawn)
        {
            if (!failures.TryGetValue(key, out Failure failure)) return false;
            if (Now >= failure.forgetAt || key.target.ThingDestroyed ||
                key.target.HasThing && key.target.Cell != failure.position)
            {
                failures.Remove(key);
                return false;
            }
            return Now < failure.retryAt && failure.probePawn != pawn.thingIDNumber;
        }

        internal bool Allows(Pawn pawn, WorkGiverDef giver, LocalTargetInfo target)
        {
            return !Blocked(new Key(giver, target), pawn) &&
                (giver == null || !Blocked(new Key(null, target), pawn));
        }

        internal bool AllowsBill(Pawn pawn, Thing giver, Bill bill) => !Blocked(new Key(null, giver, bill), pawn);

        internal void Started(Pawn pawn, Job job)
        {
            if (attempts.TryGetValue(pawn, out Attempt previous) &&
                previous.job == job && previous.startTick == job.startTick) return;
            Key key = JobKey(job);
            attempts[pawn] = new Attempt { job = job, startTick = job.startTick, key = key };
            Lease(key, pawn);
            Lease(new Key(null, job.bill == null ? job.targetA : job.targetB), pawn);
        }

        private void Lease(Key key, Pawn pawn)
        {
            if (!failures.TryGetValue(key, out Failure failure) || Now < failure.retryAt) return;
            // 只有真正启动 Job 才认领试探，候选 HasJob 查询不能占有租约。
            failure.probePawn = pawn.thingIDNumber;
            failure.retryAt = Now + 600;
            failure.forgetAt = Now + 6000;
            failures[key] = failure;
        }

        private void Record(Key key)
        {
            if (!key.target.IsValid || key.target.ThingDestroyed) return;
            if (failures.Count >= 2048 && !failures.ContainsKey(key))
            {
                Prune();
                if (failures.Count >= 2048)
                {
                    // 异常目标很多时也保持内存有界；优先淘汰最早过期的记录。
                    Key oldest = default(Key);
                    int oldestTick = int.MaxValue;
                    foreach (KeyValuePair<Key, Failure> pair in failures)
                        if (pair.Value.forgetAt < oldestTick) { oldest = pair.Key; oldestTick = pair.Value.forgetAt; }
                    failures.Remove(oldest);
                }
            }
            failures.TryGetValue(key, out Failure failure);
            failure.count = Math.Min(failure.count + 1, 5);
            failure.retryAt = Now + (120 << (failure.count - 1));
            failure.forgetAt = Now + 6000;
            failure.probePawn = -1;
            failure.position = key.target.Cell;
            failures[key] = failure;
        }

        internal void NavigationFailed(Pawn pawn, LocalTargetInfo destination)
        {
            Job job = pawn.CurJob;
            if (job == null) return;
            navigationFailures.Add(pawn);
            // 取料失败仅暂缓这份材料，允许同一蓝图改用其他材料；交付失败暂缓需求。
            LocalTargetInfo resource = job.bill == null ? job.targetA : job.targetB;
            if (resource.IsValid && (destination == resource ||
                destination.Cell == resource.Cell) && pawn.carryTracker.CarriedThing == null)
                Record(new Key(null, resource));
            else
                // 多目标交付时 B/C 会变化，应隔离当前交付对象而不是已经完成的第一项。
                Record(job.def == JobDefOf.HaulToContainer && job.targetB.IsValid
                    ? new Key(job.workGiverDef, job.targetB)
                    : attempts.TryGetValue(pawn, out Attempt attempt) ? attempt.key : JobKey(job));
        }

        internal void Ended(Pawn pawn, Job job, JobCondition condition)
        {
            bool navigationFailed = navigationFailures.Remove(pawn);
            Key key = attempts.TryGetValue(pawn, out Attempt attempt) && attempt.job == job &&
                attempt.startTick == job.startTick ? attempt.key : JobKey(job);
            attempts.Remove(pawn);
            if (condition == JobCondition.Succeeded)
            {
                failures.Remove(key);
                ReleaseLeases(pawn, true);
            }
            else
            {
                if (!navigationFailed && (condition == JobCondition.ErroredPather ||
                    condition == JobCondition.Errored || condition == JobCondition.Incompletable))
                    Record(key);
                ReleaseLeases(pawn, false);
            }
        }

        private void ReleaseLeases(Pawn pawn, bool succeeded)
        {
            // 只在任务结束时遍历小型失败表；不在逐候选扫描中分配集合。
            expired.Clear();
            foreach (KeyValuePair<Key, Failure> pair in failures)
                if (pair.Value.probePawn == pawn.thingIDNumber) expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++)
            {
                Key key = expired[i];
                if (succeeded) failures.Remove(key);
                else
                {
                    Failure failure = failures[key];
                    failure.probePawn = -1;
                    failure.retryAt = Now + 120;
                    failures[key] = failure;
                }
            }
            expired.Clear();
        }

        internal void Prune()
        {
            expired.Clear();
            foreach (KeyValuePair<Key, Failure> pair in failures)
                if (Now >= pair.Value.forgetAt || pair.Key.target.ThingDestroyed) expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++) failures.Remove(expired[i]);
            expired.Clear();
        }

        /// <summary>
        /// 整表作废。代理池被整体重建后，以 Pawn 为键的尝试记录与导航失败标记都已失去
        /// 意义，留着只会长期强引用已销毁的代理。
        /// </summary>
        internal void ClearAll()
        {
            failures.Clear();
            attempts.Clear();
            navigationFailures.Clear();
            expired.Clear();
        }

        internal static OmniWorkFailureCache For(Pawn pawn)
        {
            return OmniWorkProxyUtility.IsProxy(pawn) && pawn.Spawned
                ? pawn.Map.GetComponent<MapComponent_OmniWorkstation>().WorkFailures : null;
        }
    }

}
