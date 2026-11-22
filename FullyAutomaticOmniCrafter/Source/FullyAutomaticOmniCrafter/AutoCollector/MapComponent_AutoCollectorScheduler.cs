using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 收集器外部提示（不携带 Thing 引用，可跨线程调用）。
    /// </summary>
    internal readonly struct CollectorHint
    {
        public readonly IntVec3 Cell;

        public CollectorHint(IntVec3 cell)
        {
            Cell = cell;
        }
    }

    /// <summary>
    /// 收集器调度器的公开提示入口。
    ///
    /// 任何在调度器之外改变“地图上可搬运物品集合”的代码都可以调用这里：
    /// - 物品生成/落地；
    /// - 物品被外部搬运者拿走、销毁、禁用；
    /// - 存储区/存储建筑发生变化。
    ///
    /// 这里只写入无锁队列，绝不跨线程触碰 Thing/Map；真正的扫描仍在主线程的
    /// MapComponent 中完成。调用方不需要知道目标地图上是否存在调度器。
    /// </summary>
    public static class AutoCollectorScheduler
    {
        private const int MaxHintsPerMap = 4096;

        private static readonly ConcurrentDictionary<int, WeakReference> Schedulers =
            new ConcurrentDictionary<int, WeakReference>();

        private static readonly ConcurrentDictionary<int, ConcurrentQueue<CollectorHint>> HintQueues =
            new ConcurrentDictionary<int, ConcurrentQueue<CollectorHint>>();

        private static int droppedHintCount;

        /// <summary>通知某张地图：可搬运物品集合可能发生变化。</summary>
        public static void NotifyExternalChange(int mapUniqueId)
        {
            EnqueueHint(mapUniqueId, IntVec3.Invalid);
        }

        /// <summary>通知某张地图：某个格子的可搬运物品集合可能发生变化。</summary>
        public static void NotifyExternalChange(int mapUniqueId, IntVec3 cell)
        {
            EnqueueHint(mapUniqueId, cell);
        }

        /// <summary>被丢弃的提示数量（队列已满时递增），用于诊断。</summary>
        public static int DroppedHintCount
        {
            get { return Volatile.Read(ref droppedHintCount); }
        }

        internal static void Register(int mapUniqueId, MapComponent_AutoCollectorScheduler scheduler)
        {
            if (mapUniqueId <= 0 || scheduler == null)
            {
                return;
            }

            Schedulers[mapUniqueId] = new WeakReference(scheduler);
        }

        internal static void Unregister(int mapUniqueId, MapComponent_AutoCollectorScheduler scheduler)
        {
            if (mapUniqueId <= 0)
            {
                return;
            }

            WeakReference weak;
            if (Schedulers.TryGetValue(mapUniqueId, out weak)
                && (weak.Target == null || ReferenceEquals(weak.Target, scheduler)))
            {
                Schedulers.TryRemove(mapUniqueId, out _);
            }

            // 地图已卸载，残留提示没有意义。
            HintQueues.TryRemove(mapUniqueId, out _);
        }

        internal static void DrainHints(int mapUniqueId, List<CollectorHint> buffer)
        {
            if (mapUniqueId <= 0 || buffer == null)
            {
                return;
            }

            ConcurrentQueue<CollectorHint> queue;
            if (!HintQueues.TryGetValue(mapUniqueId, out queue))
            {
                return;
            }

            CollectorHint hint;
            while (queue.TryDequeue(out hint))
            {
                buffer.Add(hint);
            }
        }

        private static void EnqueueHint(int mapUniqueId, IntVec3 cell)
        {
            if (mapUniqueId <= 0)
            {
                return;
            }

            ConcurrentQueue<CollectorHint> queue = HintQueues.GetOrAdd(mapUniqueId, CreateHintQueue);
            if (queue.Count >= MaxHintsPerMap)
            {
                Interlocked.Increment(ref droppedHintCount);
                return;
            }

            queue.Enqueue(new CollectorHint(cell));
        }

        private static ConcurrentQueue<CollectorHint> CreateHintQueue(int mapUniqueId)
        {
            return new ConcurrentQueue<CollectorHint>();
        }
    }

    /// <summary>
    /// 每张地图一个的收集器调度器。
    ///
    /// 线程模型（与设计文档一致）：
    /// - Thing/Map 只在主线程被解引用；
    /// - 外部提示走 ConcurrentQueue，队列里只有 thingID/cell，没有 Thing；
    /// - 租约/worker 注册表使用 ConcurrentDictionary，主线程读写，外部只读/通知；
    /// - 主线程私有结构（def 桶、活跃索引、重试表）使用普通 Dictionary/Queue。
    ///
    /// 单个建筑的请求周期仍由建筑自己的 scanInterval + hash 决定；建筑每次请求
    /// 最多执行 maxStacksPerCycle 组，因此“每次一组”是单次搬运单位，而不是每周期上限。
    /// </summary>
    public class MapComponent_AutoCollectorScheduler : MapComponent
    {
        private const int Lookahead = 8;
        private const int DefaultLeaseTtl = 90;
        private const int MinBackoff = 60;
        private const int MaxBackoff = 600;
        private const int AgingTicks = 600;
        private const int AgingBoost = 4;
        private const int MaxDeficit = 32;
        private const int TokenRefillPerTick = 8;
        private const int TokenCapacity = 64;

        private sealed class DefBucket
        {
            public Queue<CollectorTask> Tasks = new Queue<CollectorTask>();
            public int OldestEnqueueTick;
            public int Deficit;
        }

        private sealed class WorkerRegistration
        {
            public readonly int WorkerId;
            public readonly Building_AutoItemCollector Building;

            public WorkerRegistration(int workerId, Building_AutoItemCollector building)
            {
                WorkerId = workerId;
                Building = building;
            }
        }

        internal readonly struct CollectorTask
        {
            public readonly int ThingId;
            public readonly ThingDef Def;
            public readonly IntVec3 Cell;
            public readonly int EnqueueTick;
            public readonly int Attempts;

            public CollectorTask(int thingId, ThingDef def, IntVec3 cell, int enqueueTick, int attempts)
            {
                ThingId = thingId;
                Def = def;
                Cell = cell;
                EnqueueTick = enqueueTick;
                Attempts = attempts;
            }
        }

        private readonly struct Lease
        {
            public readonly int ThingId;
            public readonly int OwnerWorkerId;
            public readonly int AcquiredTick;
            public readonly int ExpireTick;
            public readonly int Attempt;

            public Lease(int thingId, int ownerWorkerId, int acquiredTick, int expireTick, int attempt)
            {
                ThingId = thingId;
                OwnerWorkerId = ownerWorkerId;
                AcquiredTick = acquiredTick;
                ExpireTick = expireTick;
                Attempt = attempt;
            }
        }

        // ── 主线程私有状态 ───────────────────────────────────────────────────
        private readonly Dictionary<ThingDef, DefBucket> buckets = new Dictionary<ThingDef, DefBucket>();
        private readonly List<DefBucket> bucketList = new List<DefBucket>();
        private readonly Dictionary<int, Thing> liveIndex = new Dictionary<int, Thing>();
        private readonly HashSet<int> queuedIds = new HashSet<int>();
        private readonly Dictionary<int, CollectorTask> leasedTasks = new Dictionary<int, CollectorTask>();
        private readonly Dictionary<int, int> retryAfter = new Dictionary<int, int>();
        private readonly Dictionary<int, int> attempts = new Dictionary<int, int>();
        private readonly List<Thing> scanBuffer = new List<Thing>();
        private readonly List<CollectorTask> deferredBuffer = new List<CollectorTask>();
        private readonly List<CollectorHint> hintBuffer = new List<CollectorHint>();
        private readonly List<Building_AutoItemCollector> workerList = new List<Building_AutoItemCollector>();

        private int bucketCursor;
        private int lastScanTick = -1;
        private bool dirty = true;
        private int tokens = TokenCapacity;
        private int lastTokenTick = -1;
        private int registeredMapId = -1;

        // ── 跨线程共享状态：主线程写，外部提示只读 ───────────────────────────
        private readonly ConcurrentDictionary<int, Lease> leases = new ConcurrentDictionary<int, Lease>();
        private readonly ConcurrentDictionary<int, WorkerRegistration> workers =
            new ConcurrentDictionary<int, WorkerRegistration>();

        private int completedCount;
        private int failedCount;
        private int expiredLeaseCount;

        public MapComponent_AutoCollectorScheduler(Map map) : base(map)
        {
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            EnsureRegistered();
            dirty = true;
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            EnsureRegistered();
            DrainHints();
            PruneWorkers();
            MaintainLeases();
            PruneRetry();

            if (dirty && workerList.Count > 0)
            {
                EnsureScan();
            }
        }

        public override void MapRemoved()
        {
            AutoCollectorScheduler.Unregister(map.uniqueID, this);
            ClearState();
            base.MapRemoved();
        }

        // ── Worker 注册 ─────────────────────────────────────────────────────

        internal void RegisterWorker(Building_AutoItemCollector worker)
        {
            if (worker == null || !worker.Spawned || worker.Map != map)
            {
                return;
            }

            int id = worker.thingIDNumber;
            if (!workers.ContainsKey(id))
            {
                workers[id] = new WorkerRegistration(id, worker);
            }

            if (!workerList.Contains(worker))
            {
                workerList.Add(worker);
            }

            dirty = true;
        }

        internal void UnregisterWorker(Building_AutoItemCollector worker)
        {
            if (worker == null)
            {
                return;
            }

            int id = worker.thingIDNumber;
            if (workers.TryRemove(id, out _))
            {
                workerList.Remove(worker);
                ReleaseLeases(id);
            }

            dirty = true;
        }

        internal void NotifyWorkerChanged(Building_AutoItemCollector worker)
        {
            dirty = true;
        }

        // ── 请求入口 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 某个建筑请求最多 budget 组搬运。返回实际成功组数。
        /// 只会把任务派给调用者自己，不会跨建筑派活。
        /// </summary>
        internal int RequestWork(Building_AutoItemCollector worker, int budget, out bool hadFailure)
        {
            hadFailure = false;

            if (worker == null || budget <= 0 || !worker.Spawned || worker.Map != map)
            {
                return 0;
            }

            RegisterWorker(worker);
            EnsureScan();

            if (workerList.Count == 0)
            {
                return 0;
            }

            int worked = 0;
            HashSet<int> failedThisRequest = null;

            for (int i = 0; i < budget; i++)
            {
                if (!TryConsumeToken())
                {
                    break;
                }

                CollectorTask task;
                if (!TryAcquireTask(worker, failedThisRequest, out task))
                {
                    break;
                }

                Thing thing;
                if (!liveIndex.TryGetValue(task.ThingId, out thing) || thing == null)
                {
                    DropTask(task);
                    continue;
                }

                bool ok = false;
                try
                {
                    ok = worker.ExecuteScheduledCollect(thing);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("AutoItemCollector worker exception: " + e, task.ThingId);
                    ok = false;
                }

                if (ok)
                {
                    CompleteTask(task);
                    worked++;
                }
                else
                {
                    if (failedThisRequest == null)
                    {
                        failedThisRequest = new HashSet<int>();
                    }

                    failedThisRequest.Add(task.ThingId);
                    FailTask(task, worker);
                }
            }

            hadFailure = failedThisRequest != null;
            return worked;
        }

        // ── 扫描与入队 ───────────────────────────────────────────────────────

        private void EnsureScan()
        {
            if (workerList.Count == 0)
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            DrainHints();

            if (!dirty && now == lastScanTick)
            {
                return;
            }

            dirty = false;
            lastScanTick = now;

            PruneWorkers();
            if (workerList.Count == 0)
            {
                return;
            }

            scanBuffer.Clear();
            List<Thing> all = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Thing thing = all[i];
                    if (IsTrackedGroundThing(thing))
                    {
                        scanBuffer.Add(thing);
                    }
                }
            }

            liveIndex.Clear();
            for (int i = 0; i < scanBuffer.Count; i++)
            {
                Thing thing = scanBuffer[i];
                liveIndex[thing.thingIDNumber] = thing;
            }

            PruneBuckets(now);

            for (int i = 0; i < scanBuffer.Count; i++)
            {
                Thing thing = scanBuffer[i];
                int id = thing.thingIDNumber;

                if (queuedIds.Contains(id))
                {
                    continue;
                }

                int retryTick;
                if (retryAfter.TryGetValue(id, out retryTick) && retryTick > now)
                {
                    continue;
                }

                if (!CanAnyWorkerHandle(thing))
                {
                    continue;
                }

                int attempt;
                attempts.TryGetValue(id, out attempt);
                Enqueue(new CollectorTask(id, thing.def, thing.Position, now, attempt));
            }
        }

        private static bool IsTrackedGroundThing(Thing thing)
        {
            return thing != null
                && !thing.Destroyed
                && thing.Spawned
                && thing.def.category == ThingCategory.Item
                && !thing.IsForbidden(Faction.OfPlayer);
        }

        private bool CanAnyWorkerHandle(Thing thing)
        {
            for (int i = 0; i < workerList.Count; i++)
            {
                Building_AutoItemCollector worker = workerList[i];
                if (worker != null
                    && worker.Spawned
                    && worker.Map == map
                    && worker.CanSchedulerHandle(thing))
                {
                    return true;
                }
            }

            return false;
        }

        private void PruneBuckets(int now)
        {
            for (int i = 0; i < bucketList.Count; i++)
            {
                DefBucket bucket = bucketList[i];
                Queue<CollectorTask> kept = new Queue<CollectorTask>(bucket.Tasks.Count);
                int oldest = 0;

                while (bucket.Tasks.Count > 0)
                {
                    CollectorTask task = bucket.Tasks.Dequeue();

                    Thing thing;
                    if (!liveIndex.TryGetValue(task.ThingId, out thing) || thing == null)
                    {
                        queuedIds.Remove(task.ThingId);
                        continue;
                    }

                    int retryTick;
                    if (retryAfter.TryGetValue(task.ThingId, out retryTick) && retryTick > now)
                    {
                        queuedIds.Remove(task.ThingId);
                        continue;
                    }

                    if (!CanAnyWorkerHandle(thing))
                    {
                        queuedIds.Remove(task.ThingId);
                        continue;
                    }

                    kept.Enqueue(task);
                    if (oldest <= 0 || task.EnqueueTick < oldest)
                    {
                        oldest = task.EnqueueTick;
                    }
                }

                bucket.Tasks = kept;
                bucket.OldestEnqueueTick = oldest;
            }
        }

        // ── DRR 获取任务 ─────────────────────────────────────────────────────

        private bool TryAcquireTask(
            Building_AutoItemCollector worker,
            HashSet<int> skip,
            out CollectorTask task)
        {
            task = default(CollectorTask);

            if (bucketList.Count == 0)
            {
                return false;
            }

            GrantDeficits();

            int count = bucketList.Count;
            for (int n = 0; n < count; n++)
            {
                int index = (bucketCursor + n) % count;
                DefBucket bucket = bucketList[index];

                if (bucket.Tasks.Count == 0 || bucket.Deficit <= 0)
                {
                    continue;
                }

                bucketCursor = (index + 1) % count;
                deferredBuffer.Clear();

                int lookahead = Math.Min(Lookahead, bucket.Tasks.Count);
                bool acquired = false;

                for (int k = 0; k < lookahead; k++)
                {
                    CollectorTask candidate = bucket.Tasks.Dequeue();

                    Thing thing;
                    if (!liveIndex.TryGetValue(candidate.ThingId, out thing) || thing == null)
                    {
                        queuedIds.Remove(candidate.ThingId);
                        continue;
                    }

                    if (skip != null && skip.Contains(candidate.ThingId))
                    {
                        deferredBuffer.Add(candidate);
                        continue;
                    }

                    if (!worker.CanSchedulerHandle(thing))
                    {
                        deferredBuffer.Add(candidate);
                        continue;
                    }

                    if (TryLease(candidate, worker))
                    {
                        task = candidate;
                        acquired = true;
                        break;
                    }

                    deferredBuffer.Add(candidate);
                }

                // 把本桶未取走的候选放回队尾；任务本身很小，Queue 不产生 Thing 引用。
                for (int k = deferredBuffer.Count - 1; k >= 0; k--)
                {
                    bucket.Tasks.Enqueue(deferredBuffer[k]);
                }

                if (acquired)
                {
                    bucket.Deficit--;
                    return true;
                }
            }

            return false;
        }

        private void GrantDeficits()
        {
            int now = Find.TickManager.TicksGame;
            bool any = false;

            for (int i = 0; i < bucketList.Count; i++)
            {
                DefBucket bucket = bucketList[i];
                if (bucket.Tasks.Count == 0)
                {
                    continue;
                }

                // aging：等待超过阈值的桶获得额外配额，避免低优先级 def 长期饥饿。
                if (bucket.OldestEnqueueTick > 0 && now - bucket.OldestEnqueueTick >= AgingTicks)
                {
                    bucket.Deficit = Math.Min(MaxDeficit, bucket.Deficit + AgingBoost);
                }

                if (bucket.Deficit > 0)
                {
                    any = true;
                }
            }

            if (any)
            {
                return;
            }

            for (int i = 0; i < bucketList.Count; i++)
            {
                DefBucket bucket = bucketList[i];
                if (bucket.Tasks.Count > 0)
                {
                    bucket.Deficit = Math.Min(MaxDeficit, bucket.Deficit + 1);
                }
            }
        }

        private bool TryLease(CollectorTask task, Building_AutoItemCollector worker)
        {
            int now = Find.TickManager.TicksGame;
            Lease lease = new Lease(
                task.ThingId,
                worker.thingIDNumber,
                now,
                now + DefaultLeaseTtl,
                task.Attempts);

            if (leases.TryAdd(task.ThingId, lease))
            {
                leasedTasks[task.ThingId] = task;
                return true;
            }

            Lease old;
            if (leases.TryGetValue(task.ThingId, out old) && old.ExpireTick <= now)
            {
                if (leases.TryUpdate(task.ThingId, lease, old))
                {
                    leasedTasks[task.ThingId] = task;
                    return true;
                }
            }

            return false;
        }

        // ── 任务生命周期 ─────────────────────────────────────────────────────

        private void CompleteTask(CollectorTask task)
        {
            Lease ignored;
            leases.TryRemove(task.ThingId, out ignored);
            leasedTasks.Remove(task.ThingId);
            queuedIds.Remove(task.ThingId);
            attempts.Remove(task.ThingId);
            Interlocked.Increment(ref completedCount);
        }

        private void FailTask(CollectorTask task, Building_AutoItemCollector worker)
        {
            Lease ignored;
            leases.TryRemove(task.ThingId, out ignored);
            leasedTasks.Remove(task.ThingId);
            queuedIds.Remove(task.ThingId);

            int attempt;
            attempts.TryGetValue(task.ThingId, out attempt);
            attempt++;
            attempts[task.ThingId] = attempt;

            // 手动目标之间彼此独立：一个满/不匹配的目标不能让其它 worker 也拿不到这个物品。
            // 立刻放回队列，由 failedThisRequest 阻止同一个 worker 在本轮重复领取。
            if (!worker.UsesAutomaticTarget)
            {
                retryAfter.Remove(task.ThingId);
                Enqueue(task);
                return;
            }

            int backoff = MinBackoff;
            for (int i = 1; i < attempt && backoff < MaxBackoff; i++)
            {
                backoff *= 2;
            }

            if (backoff > MaxBackoff)
            {
                backoff = MaxBackoff;
            }

            retryAfter[task.ThingId] = Find.TickManager.TicksGame + backoff;
            Interlocked.Increment(ref failedCount);
        }

        private void DropTask(CollectorTask task)
        {
            Lease ignored;
            leases.TryRemove(task.ThingId, out ignored);
            leasedTasks.Remove(task.ThingId);
            queuedIds.Remove(task.ThingId);
            attempts.Remove(task.ThingId);
        }

        private void DropQueued(int thingId)
        {
            queuedIds.Remove(thingId);
        }

        // ── 队列维护 ─────────────────────────────────────────────────────────

        private void Enqueue(CollectorTask task)
        {
            if (queuedIds.Contains(task.ThingId))
            {
                return;
            }

            DefBucket bucket = GetBucket(task.Def);
            bucket.Tasks.Enqueue(task);
            if (bucket.OldestEnqueueTick <= 0 || task.EnqueueTick < bucket.OldestEnqueueTick)
            {
                bucket.OldestEnqueueTick = task.EnqueueTick;
            }

            queuedIds.Add(task.ThingId);
        }

        private DefBucket GetBucket(ThingDef def)
        {
            DefBucket bucket;
            if (!buckets.TryGetValue(def, out bucket))
            {
                bucket = new DefBucket();
                buckets[def] = bucket;
                bucketList.Add(bucket);
            }

            return bucket;
        }

        private void MaintainLeases()
        {
            int now = Find.TickManager.TicksGame;
            List<int> expired = null;

            foreach (KeyValuePair<int, Lease> pair in leases)
            {
                if (pair.Value.ExpireTick <= now)
                {
                    if (expired == null)
                    {
                        expired = new List<int>();
                    }

                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                int id = expired[i];
                Lease lease;
                if (!leases.TryRemove(id, out lease))
                {
                    continue;
                }

                CollectorTask task;
                if (!leasedTasks.TryGetValue(id, out task))
                {
                    continue;
                }

                leasedTasks.Remove(id);
                queuedIds.Remove(id);

                if (liveIndex.ContainsKey(id))
                {
                    Enqueue(task);
                }

                Interlocked.Increment(ref expiredLeaseCount);
            }
        }

        private void PruneRetry()
        {
            int now = Find.TickManager.TicksGame;
            List<int> expired = null;

            foreach (KeyValuePair<int, int> pair in retryAfter)
            {
                if (pair.Value <= now)
                {
                    if (expired == null)
                    {
                        expired = new List<int>();
                    }

                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                retryAfter.Remove(expired[i]);
            }
        }

        private void PruneWorkers()
        {
            List<int> invalid = null;
            List<Building_AutoItemCollector> invalidBuildings = null;

            foreach (KeyValuePair<int, WorkerRegistration> pair in workers)
            {
                Building_AutoItemCollector building = pair.Value.Building;
                if (building != null && building.Spawned && building.Map == map && !building.Destroyed)
                {
                    continue;
                }

                if (invalid == null)
                {
                    invalid = new List<int>();
                    invalidBuildings = new List<Building_AutoItemCollector>();
                }

                invalid.Add(pair.Key);
                if (building != null)
                {
                    invalidBuildings.Add(building);
                }
            }

            if (invalid == null)
            {
                return;
            }

            for (int i = 0; i < invalid.Count; i++)
            {
                WorkerRegistration ignored;
                workers.TryRemove(invalid[i], out ignored);
                ReleaseLeases(invalid[i]);
            }

            for (int i = invalidBuildings.Count - 1; i >= 0; i--)
            {
                workerList.Remove(invalidBuildings[i]);
            }

            dirty = true;
        }

        private void ReleaseLeases(int workerId)
        {
            List<int> owned = null;

            foreach (KeyValuePair<int, Lease> pair in leases)
            {
                if (pair.Value.OwnerWorkerId == workerId)
                {
                    if (owned == null)
                    {
                        owned = new List<int>();
                    }

                    owned.Add(pair.Key);
                }
            }

            if (owned == null)
            {
                return;
            }

            for (int i = 0; i < owned.Count; i++)
            {
                int id = owned[i];
                Lease lease;
                if (!leases.TryRemove(id, out lease))
                {
                    continue;
                }

                CollectorTask task;
                if (!leasedTasks.TryGetValue(id, out task))
                {
                    continue;
                }

                leasedTasks.Remove(id);
                queuedIds.Remove(id);

                if (liveIndex.ContainsKey(id))
                {
                    Enqueue(task);
                }
            }
        }

        // ── 令牌桶 ───────────────────────────────────────────────────────────

        private bool TryConsumeToken()
        {
            int now = Find.TickManager.TicksGame;

            if (lastTokenTick < 0 || now < lastTokenTick)
            {
                lastTokenTick = now;
                tokens = TokenCapacity;
            }
            else if (now > lastTokenTick)
            {
                long add = (long)(now - lastTokenTick) * TokenRefillPerTick;
                tokens = (int)Math.Min((long)TokenCapacity, (long)tokens + add);
                lastTokenTick = now;
            }

            if (tokens <= 0)
            {
                return false;
            }

            tokens--;
            return true;
        }

        // ── 生命周期清理 ─────────────────────────────────────────────────────

        private void EnsureRegistered()
        {
            if (map.uniqueID <= 0 || registeredMapId == map.uniqueID)
            {
                return;
            }

            AutoCollectorScheduler.Register(map.uniqueID, this);
            registeredMapId = map.uniqueID;
        }

        private void DrainHints()
        {
            if (map.uniqueID <= 0)
            {
                return;
            }

            hintBuffer.Clear();
            AutoCollectorScheduler.DrainHints(map.uniqueID, hintBuffer);

            if (hintBuffer.Count > 0)
            {
                dirty = true;
            }
        }

        private void ClearState()
        {
            buckets.Clear();
            bucketList.Clear();
            liveIndex.Clear();
            queuedIds.Clear();
            leasedTasks.Clear();
            retryAfter.Clear();
            attempts.Clear();
            scanBuffer.Clear();
            deferredBuffer.Clear();
            hintBuffer.Clear();
            workerList.Clear();
            workers.Clear();
            leases.Clear();
            bucketCursor = 0;
            lastScanTick = -1;
            dirty = true;
            tokens = TokenCapacity;
            lastTokenTick = -1;
            registeredMapId = -1;
        }
    }
}
