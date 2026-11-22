using System;
using System.Collections.Generic;
using System.Text;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 目标选择模式。
    /// </summary>
    public enum CollectorTargetMode
    {
        /// <summary>按原版优先级自动寻找最优存储。</summary>
        Automatic,

        /// <summary>严格只使用玩家手动指定的目标存储。</summary>
        Manual
    }

    /// <summary>
    /// 收集装置状态（只读展示，不参与是否扫描的判定）。
    /// </summary>
    public enum CollectorStatus
    {
        Idle,
        Running,
        Full,
        FilterMismatch,
        TargetInvalid,
        AreaEmpty
    }

    /// <summary>
    /// 自动物品收集器。
    ///
    /// 只在地面扫描可搬运物品，按“原版存储判定链”把物品瞬移进目标存储：
    /// - 普通存储格：StoreUtility 搜索目标格，DeSpawn 后 GenPlace 直接放置；
    /// - 容器型存储：ThingOwner.TryAdd；
    /// - 超维存储：Building_OuterrealmVault.view.TryAdd（尊重 frozen/noDeposit/filter）。
    ///
    /// 不实现 IHaulDestination，避免自己被原版搬运逻辑/自动搜索当成存储目标。
    /// 只实现 IStoreSettingsParent 以复用原版 ITab_Storage 筛选器。
    /// </summary>
    public class Building_AutoItemCollector : Building, IStoreSettingsParent
    {
        private const int DefaultScanInterval = 15;
        private static readonly int[] AllowedScanIntervals = { 1, 5, 15, 30 };
        private const int DefaultMaxStacksPerCycle = 4;
        private static readonly int[] AllowedMaxStacksPerCycle = { 1, 4, 8, 16, 32 };

        // ── 存档状态 ─────────────────────────────────────────────────────────
        private bool enabled = true;
        private bool manuallyPaused;
        private int scanInterval = DefaultScanInterval;
        private int maxStacksPerCycle = DefaultMaxStacksPerCycle;
        private CollectorTargetMode targetMode = CollectorTargetMode.Automatic;
        private Area configuredArea;
        private Zone targetZone;
        private Thing targetThing;
        private StorageSettings storageSettings;

        // ── 运行态（不存档） ─────────────────────────────────────────────────
        private CollectorStatus status = CollectorStatus.Idle;
        private int mergeGroupCursor;
        private int maintenanceCounter;
        private readonly List<Thing> scanBuffer = new List<Thing>();
        private readonly List<Thing> mergeBuffer = new List<Thing>();

        // ── 基础生命周期 ─────────────────────────────────────────────────────

        public override void PostMake()
        {
            base.PostMake();
            EnsureStorageSettings();
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            EnsureStorageSettings();
            scanBuffer.Clear();
            mergeBuffer.Clear();
            mergeGroupCursor = 0;
            maintenanceCounter = 0;

            MapComponent_AutoCollectorScheduler scheduler = GetScheduler();
            if (scheduler != null)
            {
                scheduler.RegisterWorker(this);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            if (Spawned && Map != null)
            {
                MapComponent_AutoCollectorScheduler scheduler = GetScheduler();
                if (scheduler != null)
                {
                    scheduler.UnregisterWorker(this);
                }
            }

            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref manuallyPaused, "manuallyPaused", false);
            Scribe_Values.Look(ref scanInterval, "scanInterval", DefaultScanInterval);
            Scribe_Values.Look(ref maxStacksPerCycle, "maxStacksPerCycle", DefaultMaxStacksPerCycle);
            Scribe_Values.Look(ref targetMode, "targetMode", CollectorTargetMode.Automatic);
            Scribe_References.Look(ref configuredArea, "configuredArea");
            Scribe_References.Look(ref targetZone, "targetZone");
            Scribe_References.Look(ref targetThing, "targetThing");
            Scribe_Deep.Look(ref storageSettings, "storageSettings", (object)this);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                NormalizeScanInterval();
                NormalizeMaxStacksPerCycle();
                EnsureStorageSettings();
                status = CollectorStatus.Idle;
            }
        }

        // ── IStoreSettingsParent ─────────────────────────────────────────────

        public bool StorageTabVisible => true;

        public StorageSettings GetStoreSettings()
        {
            EnsureStorageSettings();
            return storageSettings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            return null;
        }

        public void Notify_SettingsChanged()
        {
            // 筛选器变化后，调度器队列里的旧任务可能不再被本建筑接受；
            // 标记 dirty 让下一次请求重新扫描，旧任务会在取用时被 CanSchedulerHandle 拒绝。
            NotifySchedulerDirty();
        }

        private MapComponent_AutoCollectorScheduler GetScheduler()
        {
            return Spawned && Map != null
                ? Map.GetComponent<MapComponent_AutoCollectorScheduler>()
                : null;
        }

        private void NotifySchedulerDirty()
        {
            MapComponent_AutoCollectorScheduler scheduler = GetScheduler();
            if (scheduler != null)
            {
                scheduler.NotifyWorkerChanged(this);
            }
        }

        private void EnsureStorageSettings()
        {
            if (storageSettings != null)
            {
                return;
            }

            storageSettings = new StorageSettings(this);
            // 新建的 ThingFilter 默认 allowedDefs 为空（全拒），必须显式允许全部可存储物。
            storageSettings.filter.SetAllowAll(null);
        }

        private void NormalizeScanInterval()
        {
            for (int i = 0; i < AllowedScanIntervals.Length; i++)
            {
                if (AllowedScanIntervals[i] == scanInterval)
                {
                    return;
                }
            }

            scanInterval = DefaultScanInterval;
        }

        private void NormalizeMaxStacksPerCycle()
        {
            for (int i = 0; i < AllowedMaxStacksPerCycle.Length; i++)
            {
                if (AllowedMaxStacksPerCycle[i] == maxStacksPerCycle)
                {
                    return;
                }
            }

            maxStacksPerCycle = DefaultMaxStacksPerCycle;
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        protected override void Tick()
        {
            base.Tick();

            if (!enabled || manuallyPaused || !Spawned || Destroyed || Map == null)
            {
                return;
            }

            if (scanInterval <= 0)
            {
                scanInterval = DefaultScanInterval;
            }

            // 原版 hash 错峰：不同建筑按 thingIDNumber 天然落在不同 tick，无需自维护计数器。
            if (!this.IsHashIntervalTick(scanInterval))
            {
                return;
            }

            // 目标/区域可能在两次请求之间被拆除或换图；调度器扫描可能早于本建筑 Tick，
            // 所以请求前先清理失效引用。
            RefreshReferences();

            MapComponent_AutoCollectorScheduler scheduler = GetScheduler();
            if (scheduler != null)
            {
                bool hadFailure;
                int worked = scheduler.RequestWork(this, maxStacksPerCycle, out hadFailure);

                bool maintenanceWorked = false;
                // 维护相位与收集解耦：每 4 次请求做一次整理/合并，避免整理饿死收集。
                maintenanceCounter++;
                if (maintenanceCounter >= 4)
                {
                    maintenanceCounter = 0;
                    maintenanceWorked = RunMaintenance();
                }

                if (worked > 0 || maintenanceWorked)
                {
                    status = CollectorStatus.Running;
                }
                else if (hadFailure)
                {
                    // 已经领到任务但存不进去：最常见是目标满/目标格被占；筛选器不匹配会在
                    // CanSchedulerHandle 阶段被排除，因此这里优先显示“目标已满”。
                    status = CollectorStatus.Full;
                }
                else
                {
                    UpdateStatusWithoutWork();
                }

                return;
            }

            // 调度器缺失时的兜底路径（例如旧存档/异常地图组件）。
            DoCollectorCycle();
        }

        private void UpdateStatusWithoutWork()
        {
            if (targetMode == CollectorTargetMode.Manual)
            {
                IHaulDestination manualDest = ResolveManualTarget();
                if (manualDest == null || !manualDest.HaulDestinationEnabled)
                {
                    status = CollectorStatus.TargetInvalid;
                    return;
                }
            }

            if (configuredArea != null && configuredArea.TrueCount <= 0)
            {
                status = CollectorStatus.AreaEmpty;
                return;
            }

            status = CollectorStatus.Idle;
        }

        private bool RunMaintenance()
        {
            if (!Spawned || Map == null)
            {
                return false;
            }

            IHaulDestination manualDest = targetMode == CollectorTargetMode.Manual
                ? ResolveManualTarget()
                : null;

            if (TryOrganizeOne(manualDest))
            {
                return true;
            }

            return TryMergeOne(manualDest);
        }

        private void DoCollectorCycle()
        {
            RefreshReferences();

            IHaulDestination manualDest = null;
            if (targetMode == CollectorTargetMode.Manual)
            {
                manualDest = ResolveManualTarget();
                if (manualDest == null)
                {
                    status = CollectorStatus.TargetInvalid;
                    return;
                }

                if (!manualDest.HaulDestinationEnabled)
                {
                    status = CollectorStatus.TargetInvalid;
                    return;
                }
            }

            if (configuredArea != null && configuredArea.TrueCount <= 0)
            {
                status = CollectorStatus.AreaEmpty;
                return;
            }

            bool hadCandidate;
            bool hadAcceptedCandidate;
            if (TryCollectOne(manualDest, out hadCandidate, out hadAcceptedCandidate))
            {
                status = CollectorStatus.Running;
                return;
            }

            if (TryOrganizeOne(manualDest))
            {
                status = CollectorStatus.Running;
                return;
            }

            if (TryMergeOne(manualDest))
            {
                status = CollectorStatus.Running;
                return;
            }

            if (!hadCandidate)
            {
                status = CollectorStatus.Idle;
                return;
            }

            if (manualDest != null)
            {
                status = hadAcceptedCandidate
                    ? CollectorStatus.Full
                    : CollectorStatus.FilterMismatch;
                return;
            }

            status = CollectorStatus.Full;
        }

        /// <summary>
        /// 校验区域与目标引用；被删除/换图的引用会在本周期内被清空并显示 TargetInvalid。
        /// </summary>
        private void RefreshReferences()
        {
            if (configuredArea != null
                && (configuredArea.areaManager == null || configuredArea.areaManager != Map.areaManager))
            {
                configuredArea = null;
            }

            if (targetZone != null
                && (targetZone.zoneManager == null || targetZone.Map != Map))
            {
                targetZone = null;
            }

            if (targetThing != null && (targetThing.Destroyed || !targetThing.Spawned || targetThing.Map != Map))
            {
                targetThing = null;
            }
        }

        private IHaulDestination ResolveManualTarget()
        {
            if (targetZone != null)
            {
                if (targetZone.zoneManager == null || targetZone.Map != Map)
                {
                    return null;
                }

                return targetZone as IHaulDestination;
            }

            if (targetThing != null)
            {
                if (targetThing.Destroyed || !targetThing.Spawned || targetThing.Map != Map)
                {
                    return null;
                }

                return targetThing as IHaulDestination;
            }

            return null;
        }

        // ── 收集 ─────────────────────────────────────────────────────────────

        private bool TryCollectOne(IHaulDestination manualDest, out bool hadCandidate, out bool hadAcceptedCandidate)
        {
            hadCandidate = false;
            hadAcceptedCandidate = false;

            CopyHaulableThings();

            for (int i = 0; i < scanBuffer.Count; i++)
            {
                Thing t = scanBuffer[i];
                if (!IsEligibleGroundThing(t))
                {
                    continue;
                }

                IHaulDestination currentDest = StoreUtility.CurrentHaulDestinationOf(t);
                bool properlyStored = currentDest != null
                    && currentDest.HaulDestinationEnabled
                    && currentDest.Accepts(t);

                // 已正确存储的物品交给整理流程处理（低优先级 → 高优先级）。
                if (properlyStored)
                {
                    continue;
                }

                hadCandidate = true;

                if (targetMode == CollectorTargetMode.Manual)
                {
                    if (manualDest == null || !manualDest.HaulDestinationEnabled)
                    {
                        continue;
                    }

                    if (manualDest.Accepts(t))
                    {
                        hadAcceptedCandidate = true;
                    }

                    if (TryStore(t, manualDest, IntVec3.Invalid))
                    {
                        return true;
                    }
                }
                else
                {
                    StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(t);
                    IntVec3 cell;
                    IHaulDestination bestDest;
                    if (!StoreUtility.TryFindBestBetterStorageFor(
                            t, null, Map, currentPriority, Faction.OfPlayer,
                            out cell, out bestDest, false))
                    {
                        continue;
                    }

                    if (bestDest == null)
                    {
                        continue;
                    }

                    hadAcceptedCandidate = true;

                    if (TryStore(t, bestDest, cell))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CopyHaulableThings()
        {
            scanBuffer.Clear();

            List<Thing> all = Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            if (all == null)
            {
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                scanBuffer.Add(all[i]);
            }
        }

        /// <summary>
        /// 地面候选硬过滤。顺序按“廉价 → 昂贵”排列。
        /// </summary>
        private bool IsEligibleGroundThing(Thing t)
        {
            if (t == null || t.Destroyed || !t.Spawned || t.Map != Map)
            {
                return false;
            }

            if (t.def.category != ThingCategory.Item)
            {
                return false;
            }

            if (t.IsForbidden(Faction.OfPlayer))
            {
                return false;
            }

            if (configuredArea != null)
            {
                if (configuredArea.areaManager == null || configuredArea.areaManager != Map.areaManager)
                {
                    return false;
                }

                if (!configuredArea[t.Position])
                {
                    return false;
                }
            }

            // 投影/借出副本是伪 Spawned 或真 Spawned 的视图对象，绝不搬运。
            if (OuterrealmVaultUtil.IsProjection(t)
                || OuterrealmVaultUtil.IsOuterrealmBorrowed(t)
                || OuterrealmVaultUtil.IsProtectedFromAutomaticDeposit(t))
            {
                return false;
            }

            // 任何派系/任何用途的预约都跳过，避免和殖民者 Job 抢物品。
            if (Map.reservationManager.IsReserved(t)
                || Map.physicalInteractionReservationManager.IsReserved(t))
            {
                return false;
            }

            // vault 格上的物品不参与收集/整理/合并；它们要么正在等待吸收，要么是视图副本。
            IHaulDestination dest = StoreUtility.CurrentHaulDestinationOf(t);
            if (dest is Building_OuterrealmVault || t.ParentHolder is Building_OuterrealmVault)
            {
                return false;
            }

            // 本建筑自己的筛选器（ITab_Storage 配置）作为最后一道硬过滤。
            if (!GetStoreSettings().AllowedToAccept(t))
            {
                return false;
            }

            return true;
        }

        // ── 调度器执行接口 ───────────────────────────────────────────────────

        internal bool UsesAutomaticTarget
        {
            get { return targetMode == CollectorTargetMode.Automatic; }
        }

        /// <summary>
        /// 调度器判断本建筑是否可能处理这个地面物品。
        /// 只做“是否会尝试”的快速判断，不实际搜索目标格。
        /// </summary>
        internal bool CanSchedulerHandle(Thing t)
        {
            if (!enabled || manuallyPaused || !Spawned || Map == null)
            {
                return false;
            }

            if (!IsEligibleGroundThing(t))
            {
                return false;
            }

            // 已正确存储的物品不属于“收集”，交给维护相位整理。
            IHaulDestination dest = StoreUtility.CurrentHaulDestinationOf(t);
            if (dest != null && dest.HaulDestinationEnabled && dest.Accepts(t))
            {
                return false;
            }

            if (targetMode == CollectorTargetMode.Manual)
            {
                IHaulDestination manualDest = ResolveManualTarget();
                if (manualDest == null
                    || manualDest.Map != Map
                    || !manualDest.HaulDestinationEnabled
                    || !manualDest.Accepts(t))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 调度器在主线程调用：真正执行一组搬运。
        /// 不重新做全表扫描，只处理调度器已经选中的这一件物品。
        /// </summary>
        internal bool ExecuteScheduledCollect(Thing t)
        {
            if (!Spawned || Map == null || t == null || t.Destroyed || !t.Spawned || t.Map != Map)
            {
                return false;
            }

            // 执行前重新校验：外部搬运者/玩家可能在扫描后拿走了它。
            if (!CanSchedulerHandle(t))
            {
                return false;
            }

            if (targetMode == CollectorTargetMode.Manual)
            {
                IHaulDestination manualDest = ResolveManualTarget();
                if (manualDest == null
                    || manualDest.Map != Map
                    || !manualDest.HaulDestinationEnabled
                    || !manualDest.Accepts(t))
                {
                    return false;
                }

                return TryStore(t, manualDest, IntVec3.Invalid);
            }

            StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(t);
            IntVec3 cell;
            IHaulDestination bestDest;
            if (!StoreUtility.TryFindBestBetterStorageFor(
                    t, null, Map, currentPriority, Faction.OfPlayer,
                    out cell, out bestDest, false))
            {
                return false;
            }

            if (bestDest == null)
            {
                return false;
            }

            return TryStore(t, bestDest, cell);
        }

        // ── 整理（低优先级 → 高优先级） ─────────────────────────────────────

        private bool TryOrganizeOne(IHaulDestination manualDest)
        {
            CopyHaulableThings();

            if (manualDest != null)
            {
                // 手动模式严格只进目标：只把“目标优先级更高”的已存储物品拉进目标，
                // 绝不移出目标，也不碰其它存储。
                ISlotGroupParent targetParent = manualDest as ISlotGroupParent;
                if (targetParent == null || !manualDest.HaulDestinationEnabled)
                {
                    return false;
                }

                StoragePriority targetPriority = manualDest.GetStoreSettings().Priority;

                for (int i = 0; i < scanBuffer.Count; i++)
                {
                    Thing t = scanBuffer[i];
                    if (!IsEligibleGroundThing(t))
                    {
                        continue;
                    }

                    IHaulDestination currentDest = StoreUtility.CurrentHaulDestinationOf(t);
                    if (currentDest == null || ReferenceEquals(currentDest, manualDest))
                    {
                        continue;
                    }

                    // 与原版 IsInValidBestStorage 一致：只整理玩家自己的存储建筑。
                    if (currentDest is Building currentBuilding && currentBuilding.Faction != Faction.OfPlayer)
                    {
                        continue;
                    }

                    if (!currentDest.Accepts(t))
                    {
                        continue;
                    }

                    if (currentDest.GetStoreSettings().Priority >= targetPriority)
                    {
                        continue;
                    }

                    if (!manualDest.Accepts(t))
                    {
                        continue;
                    }

                    // 统一走 TryStore：vault 会先命中 view.TryAdd 分支，绝不会被当成普通存储格
                    // 直接 GenPlace 到 vault 地板上。
                    if (TryStore(t, manualDest, IntVec3.Invalid))
                    {
                        return true;
                    }
                }

                return false;
            }

            // 自动模式：复刻原版 IsInValidBestStorage 的后半段，以当前存储优先级为基准再搜更高优先级。
            for (int i = 0; i < scanBuffer.Count; i++)
            {
                Thing t = scanBuffer[i];
                if (!IsEligibleGroundThing(t))
                {
                    continue;
                }

                IHaulDestination currentDest = StoreUtility.CurrentHaulDestinationOf(t);
                if (currentDest == null
                    || !currentDest.HaulDestinationEnabled
                    || !currentDest.Accepts(t))
                {
                    continue;
                }

                // 与原版 IsInValidBestStorage 一致：只整理玩家自己的存储建筑。
                if (currentDest is Building currentBuilding && currentBuilding.Faction != Faction.OfPlayer)
                {
                    continue;
                }

                if (currentDest is Building_OuterrealmVault)
                {
                    continue;
                }

                StoragePriority currentPriority = currentDest.GetStoreSettings().Priority;
                IntVec3 cell;
                IHaulDestination betterDest;
                if (!StoreUtility.TryFindBestBetterStorageFor(
                        t, null, Map, currentPriority, Faction.OfPlayer,
                        out cell, out betterDest, false))
                {
                    continue;
                }

                if (betterDest == null || ReferenceEquals(betterDest, currentDest))
                {
                    continue;
                }

                if (TryStore(t, betterDest, cell))
                {
                    return true;
                }
            }

            return false;
        }

        // ── 合并 ─────────────────────────────────────────────────────────────

        private bool TryMergeOne(IHaulDestination manualDest)
        {
            if (manualDest != null)
            {
                if (manualDest is Building_OuterrealmVault)
                {
                    return false;
                }

                ISlotGroupParent parent = manualDest as ISlotGroupParent;
                if (parent == null)
                {
                    return false;
                }

                if (parent is Thing parentThing && parentThing.Faction != Faction.OfPlayer)
                {
                    return false;
                }

                SlotGroup manualSlotGroup = parent.GetSlotGroup();
                if (manualSlotGroup == null)
                {
                    return false;
                }

                ISlotGroup manualGroup = manualSlotGroup.StorageGroup ?? (ISlotGroup)manualSlotGroup;
                return TryMergeInGroup(manualGroup);
            }

            List<SlotGroup> groups = Map.haulDestinationManager.AllGroupsListForReading;
            if (groups.Count == 0)
            {
                return false;
            }

            if (mergeGroupCursor < 0 || mergeGroupCursor >= groups.Count)
            {
                mergeGroupCursor = 0;
            }

            // 与原版 WorkGiver_Merge 的节流一致：每周期只处理一个存储组，游标仅运行时保存。
            SlotGroup slotGroup = groups[mergeGroupCursor];
            mergeGroupCursor++;
            if (mergeGroupCursor >= groups.Count)
            {
                mergeGroupCursor = 0;
            }

            if (slotGroup == null
                || slotGroup.parent == null
                || slotGroup.parent is Building_OuterrealmVault
                || slotGroup.parent is Thing groupParentThing && groupParentThing.Faction != Faction.OfPlayer)
            {
                return false;
            }

            ISlotGroup group = slotGroup.StorageGroup ?? (ISlotGroup)slotGroup;
            return TryMergeInGroup(group);
        }

        private bool TryMergeInGroup(ISlotGroup group)
        {
            if (group == null)
            {
                return false;
            }

            mergeBuffer.Clear();
            foreach (Thing held in group.HeldThings)
            {
                if (held != null && !held.Destroyed)
                {
                    mergeBuffer.Add(held);
                }
            }

            // 大堆吸小堆；同 def、双方未满、双方未禁用/未预约、目标格允许且无火。
            for (int i = 0; i < mergeBuffer.Count; i++)
            {
                Thing small = mergeBuffer[i];
                if (!IsEligibleForMerge(small))
                {
                    continue;
                }

                for (int j = 0; j < mergeBuffer.Count; j++)
                {
                    Thing big = mergeBuffer[j];
                    if (ReferenceEquals(big, small))
                    {
                        continue;
                    }

                    if (!IsEligibleForMerge(big))
                    {
                        continue;
                    }

                    if (big.stackCount < small.stackCount)
                    {
                        continue;
                    }

                    if (!big.CanStackWith(small))
                    {
                        continue;
                    }

                    if (!big.Position.IsValidStorageFor(Map, small))
                    {
                        continue;
                    }

                    if (big.Position.ContainsStaticFire(Map))
                    {
                        continue;
                    }

                    int before = big.stackCount;
                    big.TryAbsorbStack(small, true);
                    if (big.stackCount > before)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsEligibleForMerge(Thing t)
        {
            if (t == null || t.Destroyed || !t.Spawned || t.Map != Map)
            {
                return false;
            }

            if (t.def.category != ThingCategory.Item)
            {
                return false;
            }

            if (t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
            {
                return false;
            }

            if (t.IsForbidden(Faction.OfPlayer))
            {
                return false;
            }

            if (OuterrealmVaultUtil.IsProjection(t)
                || OuterrealmVaultUtil.IsOuterrealmBorrowed(t)
                || OuterrealmVaultUtil.IsProtectedFromAutomaticDeposit(t))
            {
                return false;
            }

            if (t.ParentHolder is Building_OuterrealmVault)
            {
                return false;
            }

            // 存储组可能把多个超维端口合并成一个 StorageGroup；不能只看 ParentHolder，
            // 还要看物品当前所在的存储目的地。
            IHaulDestination dest = StoreUtility.CurrentHaulDestinationOf(t);
            if (dest is Building_OuterrealmVault)
            {
                return false;
            }

            if (Map.reservationManager.IsReserved(t)
                || Map.physicalInteractionReservationManager.IsReserved(t))
            {
                return false;
            }

            return true;
        }

        // ── 实际存入 ─────────────────────────────────────────────────────────

        private bool TryStore(Thing t, IHaulDestination dest, IntVec3 knownCell)
        {
            if (dest == null || t == null || t.Destroyed || !t.Spawned)
            {
                return false;
            }

            if (dest is Building_OuterrealmVault vault)
            {
                return TryStoreInVault(t, vault);
            }

            Thing destThing = dest as Thing;
            if (destThing != null && !(dest is ISlotGroupParent))
            {
                ThingOwner container = destThing.TryGetInnerInteractableThingOwner();
                if (container != null)
                {
                    return TryStoreInContainer(t, destThing, container);
                }

                return false;
            }

            ISlotGroupParent slotGroupParent = dest as ISlotGroupParent;
            if (slotGroupParent != null)
            {
                return TryStoreInSlotGroup(t, slotGroupParent, knownCell);
            }

            return false;
        }

        private bool TryStoreInSlotGroup(Thing t, ISlotGroupParent parent, IntVec3 knownCell)
        {
            if (parent == null || !parent.HaulDestinationEnabled || parent.Map != Map)
            {
                return false;
            }

            if (parent is Thing parentThing && parentThing.Faction != Faction.OfPlayer)
            {
                return false;
            }

            SlotGroup slotGroup = parent.GetSlotGroup();
            if (slotGroup == null)
            {
                return false;
            }

            IntVec3 cell = knownCell;
            if (!cell.IsValid)
            {
                StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(t);
                if (!StoreUtility.TryFindBestBetterStoreCellForIn(
                        t, null, Map, currentPriority, Faction.OfPlayer,
                        slotGroup, out cell, true))
                {
                    return false;
                }
            }

            if (!StoreUtility.IsGoodStoreCell(cell, Map, t, null, Faction.OfPlayer))
            {
                return false;
            }

            IntVec3 oldPos = t.PositionHeld;
            Map oldMap = t.MapHeld;

            t.DeSpawn();

            if (GenPlace.TryPlaceThing(t, cell, Map, ThingPlaceMode.Direct))
            {
                return true;
            }

            if (!t.Destroyed)
            {
                GenPlace.TryPlaceThing(t, oldPos, oldMap, ThingPlaceMode.Near);
            }

            return false;
        }

        private bool TryStoreInContainer(Thing t, Thing containerThing, ThingOwner owner)
        {
            IHaulDestination containerDest = containerThing as IHaulDestination;
            if (containerDest == null
                || !containerThing.Spawned
                || containerThing.Map != Map
                || containerThing.Faction != Faction.OfPlayer
                || !containerDest.HaulDestinationEnabled
                || !containerDest.Accepts(t))
            {
                return false;
            }

            if (owner.GetCountCanAccept(t) <= 0)
            {
                return false;
            }

            IntVec3 oldPos = t.PositionHeld;
            Map oldMap = t.MapHeld;

            t.DeSpawn();

            if (owner.TryAdd(t))
            {
                return true;
            }

            if (!t.Destroyed)
            {
                GenPlace.TryPlaceThing(t, oldPos, oldMap, ThingPlaceMode.Near);
            }

            return false;
        }

        private bool TryStoreInVault(Thing t, Building_OuterrealmVault vault)
        {
            if (vault == null || vault.view == null || !vault.Spawned || vault.Map != Map)
            {
                return false;
            }

            if (vault.Faction != Faction.OfPlayer)
            {
                return false;
            }

            if (!vault.HaulDestinationEnabled || !vault.Accepts(t))
            {
                return false;
            }

            IntVec3 oldPos = t.PositionHeld;
            Map oldMap = t.MapHeld;

            t.DeSpawn();

            // 统一走视图层：内部会再次校验 enabled/Accepts/IsProtectedFromAutomaticDeposit，
            // 并完成 Deposit + EnsureCopyFor，不能直接调 GameComponent.Deposit。
            if (vault.view.TryAdd(t, true))
            {
                return true;
            }

            if (!t.Destroyed)
            {
                GenPlace.TryPlaceThing(t, oldPos, oldMap, ThingPlaceMode.Near);
            }

            return false;
        }

        // ── Gizmo ────────────────────────────────────────────────────────────

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            if (Map == null)
            {
                yield break;
            }

            yield return new Command_Toggle
            {
                defaultLabel = "AutoItemCollector_Toggle".Translate(),
                defaultDesc = "AutoItemCollector_ToggleDesc".Translate(),
                icon = BaseContent.WhiteTex,
                isActive = () => enabled,
                toggleAction = delegate
                {
                    enabled = !enabled;
                    if (enabled)
                    {
                        status = CollectorStatus.Idle;
                        NotifySchedulerDirty();
                    }
                }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "AutoItemCollector_ManualPause".Translate(),
                defaultDesc = "AutoItemCollector_ManualPauseDesc".Translate(),
                icon = BaseContent.WhiteTex,
                isActive = () => manuallyPaused,
                toggleAction = delegate
                {
                    manuallyPaused = !manuallyPaused;
                    if (!manuallyPaused)
                    {
                        status = CollectorStatus.Idle;
                        NotifySchedulerDirty();
                    }
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "AutoItemCollector_ScanInterval".Translate(scanInterval),
                defaultDesc = "AutoItemCollector_ScanIntervalDesc".Translate(),
                icon = BaseContent.WhiteTex,
                action = ShowScanIntervalMenu
            };

            yield return new Command_Action
            {
                defaultLabel = "AutoItemCollector_MaxStacks".Translate(maxStacksPerCycle),
                defaultDesc = "AutoItemCollector_MaxStacksDesc".Translate(),
                icon = BaseContent.WhiteTex,
                action = ShowMaxStacksMenu
            };

            yield return new Command_Action
            {
                defaultLabel = "AutoItemCollector_TargetMode".Translate(TargetModeLabel()),
                defaultDesc = "AutoItemCollector_TargetModeDesc".Translate(),
                icon = BaseContent.WhiteTex,
                action = ShowTargetModeMenu
            };

            yield return new Command_Action
            {
                defaultLabel = "AutoItemCollector_SelectArea".Translate(AreaLabel()),
                defaultDesc = "AutoItemCollector_SelectAreaDesc".Translate(),
                icon = BaseContent.WhiteTex,
                action = ShowAreaMenu
            };

            if (targetMode == CollectorTargetMode.Manual)
            {
                yield return new Command_Action
                {
                    defaultLabel = "AutoItemCollector_SelectTarget".Translate(),
                    defaultDesc = "AutoItemCollector_SelectTargetDesc".Translate(),
                    icon = BaseContent.WhiteTex,
                    action = BeginSelectTarget
                };

                yield return new Command_Action
                {
                    defaultLabel = "AutoItemCollector_SelectTargetFromList".Translate(),
                    defaultDesc = "AutoItemCollector_SelectTargetFromListDesc".Translate(),
                    icon = BaseContent.WhiteTex,
                    action = ShowTargetListMenu
                };
            }
        }

        private void ShowScanIntervalMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            for (int i = 0; i < AllowedScanIntervals.Length; i++)
            {
                int interval = AllowedScanIntervals[i];
                string label = interval == scanInterval
                    ? "AutoItemCollector_CurrentOption".Translate(interval)
                    : "AutoItemCollector_ScanIntervalOption".Translate(interval);

                options.Add(new FloatMenuOption(label, delegate
                {
                    scanInterval = interval;
                    status = CollectorStatus.Idle;
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void ShowMaxStacksMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            for (int i = 0; i < AllowedMaxStacksPerCycle.Length; i++)
            {
                int count = AllowedMaxStacksPerCycle[i];
                string label = count == maxStacksPerCycle
                    ? "AutoItemCollector_CurrentOption".Translate(count)
                    : "AutoItemCollector_MaxStacksOption".Translate(count);

                options.Add(new FloatMenuOption(label, delegate
                {
                    maxStacksPerCycle = count;
                    status = CollectorStatus.Idle;
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void ShowTargetModeMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            options.Add(new FloatMenuOption(
                targetMode == CollectorTargetMode.Automatic
                    ? "AutoItemCollector_CurrentOption".Translate("AutoItemCollector_ModeAutomatic".Translate())
                    : "AutoItemCollector_ModeAutomatic".Translate(),
                delegate
                {
                    targetMode = CollectorTargetMode.Automatic;
                    status = CollectorStatus.Idle;
                    NotifySchedulerDirty();
                }));

            options.Add(new FloatMenuOption(
                targetMode == CollectorTargetMode.Manual
                    ? "AutoItemCollector_CurrentOption".Translate("AutoItemCollector_ModeManual".Translate())
                    : "AutoItemCollector_ModeManual".Translate(),
                delegate
                {
                    targetMode = CollectorTargetMode.Manual;
                    status = CollectorStatus.Idle;
                    NotifySchedulerDirty();
                }));

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void ShowAreaMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            options.Add(new FloatMenuOption(
                configuredArea == null
                    ? "AutoItemCollector_CurrentOption".Translate("AutoItemCollector_WholeMap".Translate())
                    : "AutoItemCollector_WholeMap".Translate(),
                delegate
                {
                    configuredArea = null;
                    status = CollectorStatus.Idle;
                    NotifySchedulerDirty();
                }));

            List<Area> allAreas = Map.areaManager.AllAreas;
            List<Area> sorted = new List<Area>();
            for (int i = 0; i < allAreas.Count; i++)
            {
                Area area = allAreas[i];
                if (area == null || area is Area_OmniWorkstation)
                {
                    continue;
                }

                // 稳定插入排序：ListPriority 降序（家 → 屋顶区 → 普通活动区），与原版 UI 顺序一致。
                int insertIndex = sorted.Count;
                for (int j = 0; j < sorted.Count; j++)
                {
                    if (sorted[j].ListPriority < area.ListPriority)
                    {
                        insertIndex = j;
                        break;
                    }
                }

                sorted.Insert(insertIndex, area);
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                Area area = sorted[i];
                string label;
                if (area == configuredArea)
                {
                    label = (string)"AutoItemCollector_CurrentOption".Translate(area.Label);
                }
                else
                {
                    label = area.Label;
                }

                options.Add(new FloatMenuOption(label, delegate
                {
                    configuredArea = area;
                    status = CollectorStatus.Idle;
                    NotifySchedulerDirty();
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void ShowTargetListMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            List<IHaulDestination> destinations = Map.haulDestinationManager.AllHaulDestinationsListForReading;

            for (int i = 0; i < destinations.Count; i++)
            {
                IHaulDestination dest = destinations[i];
                if (dest == null || dest.Map != Map || !dest.HaulDestinationEnabled)
                {
                    continue;
                }

                // 只允许玩家自己的存储建筑/容器；存储区（Zone）没有阵营。
                if (dest is Thing ownerThing && ownerThing.Faction != Faction.OfPlayer)
                {
                    continue;
                }

                bool isSlotGroup = dest is ISlotGroupParent;
                Thing destThing = dest as Thing;
                bool isContainer = destThing != null
                    && destThing.TryGetInnerInteractableThingOwner() != null;

                if (!isSlotGroup && !isContainer)
                {
                    continue;
                }

                IHaulDestination captured = dest;
                string label = "AutoItemCollector_TargetOption".Translate(
                    GetTargetLabel(dest),
                    dest.GetStoreSettings().Priority.Label());

                if (dest is Building_OuterrealmVault)
                {
                    label = "AutoItemCollector_TargetOptionVault".Translate(
                        GetTargetLabel(dest),
                        dest.GetStoreSettings().Priority.Label());
                }

                options.Add(new FloatMenuOption(label, delegate
                {
                    SetManualTarget(captured);
                }));
            }

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("AutoItemCollector_NoTargets".Translate(), delegate { }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private string GetTargetLabel(IHaulDestination dest)
        {
            Zone zone = dest as Zone;
            if (zone != null)
            {
                return zone.RenamableLabel;
            }

            Thing thing = dest as Thing;
            if (thing != null)
            {
                return thing.LabelCap;
            }

            return dest.ToString();
        }

        private void BeginSelectTarget()
        {
            TargetingParameters targetingParameters = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetPawns = false,
                canTargetAnimals = false,
                canTargetHumans = false,
                canTargetMechs = false,
                canTargetItems = false,
                canTargetPlants = false,
                canTargetFires = false,
                canTargetBuildings = true,
                mapObjectTargetsMustBeAutoAttackable = false,
                validator = delegate(TargetInfo targ)
                {
                    if (targ.Map == null || targ.Map != Map)
                    {
                        return false;
                    }

                    if (targ.HasThing && targ.Thing is IHaulDestination thingDest)
                    {
                        return IsPlayerHaulDestination(thingDest);
                    }

                    ISlotGroupParent parent = targ.Map.haulDestinationManager.SlotGroupParentAt(targ.Cell);
                    return IsPlayerHaulDestination(parent);
                }
            };

            Find.Targeter.BeginTargeting(targetingParameters, OnTargetSelected);
        }

        private void OnTargetSelected(LocalTargetInfo targ)
        {
            if (!targ.IsValid)
            {
                return;
            }

            if (targ.HasThing && (targ.Thing == null || targ.Thing.Map != Map))
            {
                return;
            }

            IHaulDestination dest = null;
            if (targ.HasThing)
            {
                dest = targ.Thing as IHaulDestination;
            }

            if (dest == null)
            {
                dest = Map.haulDestinationManager.SlotGroupParentAt(targ.Cell);
            }

            if (dest == null)
            {
                return;
            }

            SetManualTarget(dest);
        }

        private bool IsPlayerHaulDestination(IHaulDestination dest)
        {
            if (dest == null || !dest.HaulDestinationEnabled)
            {
                return false;
            }

            // 存储区（Zone）没有阵营；建筑/容器必须是玩家所有，避免把物品送进敌方仓库。
            return !(dest is Thing thing) || thing.Faction == Faction.OfPlayer;
        }

        private void SetManualTarget(IHaulDestination dest)
        {
            if (!IsPlayerHaulDestination(dest))
            {
                return;
            }

            Zone zone = dest as Zone;
            if (zone != null)
            {
                targetZone = zone;
                targetThing = null;
            }
            else
            {
                Thing thing = dest as Thing;
                if (thing == null)
                {
                    return;
                }

                targetThing = thing;
                targetZone = null;
            }

            targetMode = CollectorTargetMode.Manual;
            status = CollectorStatus.Idle;
            NotifySchedulerDirty();
        }

        // ── Inspect ──────────────────────────────────────────────────────────

        public override string GetInspectString()
        {
            string baseString = base.GetInspectString();
            if (!Spawned)
            {
                return baseString;
            }

            StringBuilder sb = new StringBuilder(baseString);
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append((string)"AutoItemCollector_InspectStatus".Translate(StatusLabel()));

            sb.AppendLine();
            sb.Append((string)"AutoItemCollector_InspectTarget".Translate(TargetLabel()));

            sb.AppendLine();
            sb.Append((string)"AutoItemCollector_InspectArea".Translate(AreaLabel()));

            sb.AppendLine();
            sb.Append((string)"AutoItemCollector_InspectInterval".Translate(scanInterval));

            sb.AppendLine();
            sb.Append((string)"AutoItemCollector_InspectMaxStacks".Translate(maxStacksPerCycle));

            return sb.ToString();
        }

        private string StatusLabel()
        {
            if (!enabled)
            {
                return (string)"AutoItemCollector_StatusDisabled".Translate();
            }

            if (manuallyPaused)
            {
                return (string)"AutoItemCollector_StatusManualPaused".Translate();
            }

            switch (status)
            {
                case CollectorStatus.Running:
                    return (string)"AutoItemCollector_StatusRunning".Translate();
                case CollectorStatus.Full:
                    return (string)"AutoItemCollector_StatusFull".Translate();
                case CollectorStatus.FilterMismatch:
                    return (string)"AutoItemCollector_StatusFilterMismatch".Translate();
                case CollectorStatus.TargetInvalid:
                    return (string)"AutoItemCollector_StatusTargetInvalid".Translate();
                case CollectorStatus.AreaEmpty:
                    return (string)"AutoItemCollector_StatusAreaEmpty".Translate();
                default:
                    return (string)"AutoItemCollector_StatusIdle".Translate();
            }
        }

        private string TargetModeLabel()
        {
            return (string)(targetMode == CollectorTargetMode.Manual
                ? "AutoItemCollector_ModeManual".Translate()
                : "AutoItemCollector_ModeAutomatic".Translate());
        }

        private string AreaLabel()
        {
            return configuredArea != null
                ? configuredArea.Label
                : (string)"AutoItemCollector_WholeMap".Translate();
        }

        private string TargetLabel()
        {
            if (targetMode != CollectorTargetMode.Manual)
            {
                return (string)"AutoItemCollector_TargetAutomatic".Translate();
            }

            if (targetZone != null)
            {
                return targetZone.RenamableLabel;
            }

            if (targetThing != null)
            {
                return targetThing.LabelCap;
            }

            return (string)"AutoItemCollector_TargetNone".Translate();
        }
    }
}
