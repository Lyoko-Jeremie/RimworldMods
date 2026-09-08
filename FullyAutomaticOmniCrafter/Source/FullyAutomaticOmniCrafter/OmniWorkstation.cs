using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站只保存玩家配置；所有工作搜索和代理管理均由地图组件集中完成。
    /// </summary>
    public sealed class Building_OmniWorkstation : Building
    {
        private static readonly int[] WorkRadii = { 15, 30, 60, 100 };

        private bool automationEnabled = true;
        private int workRadiusIndex = 1;

        public bool AutomationEnabled => automationEnabled;
        public int WorkRadius => WorkRadii[Mathf.Clamp(workRadiusIndex, 0, WorkRadii.Length - 1)];

        public bool Operational
        {
            get
            {
                if (!Spawned || !automationEnabled || this.IsBurning() || this.IsBrokenDown()) return false;
                CompFlickable flickable = GetComp<CompFlickable>();
                if (flickable != null && !flickable.SwitchIsOn) return false;
                CompPowerTrader power = GetComp<CompPowerTrader>();
                return power == null || power.PowerOn;
            }
        }

        public bool Covers(IntVec3 cell)
        {
            return Spawned && cell.InBounds(Map) && Position.InHorDistOf(cell, WorkRadius);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<MapComponent_OmniWorkstation>().Register(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            if (map != null)
                map.GetComponent<MapComponent_OmniWorkstation>().Deregister(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref automationEnabled, "automationEnabled", true);
            Scribe_Values.Look(ref workRadiusIndex, "workRadiusIndex", 1);
            workRadiusIndex = Mathf.Clamp(workRadiusIndex, 0, WorkRadii.Length - 1);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
                yield return gizmo;

            yield return new Command_Toggle
            {
                defaultLabel = "OmniWorkstation_Toggle".Translate(),
                defaultDesc = "OmniWorkstation_ToggleDesc".Translate(),
                icon = TexCommand.DesirePower,
                isActive = () => automationEnabled,
                toggleAction = () =>
                {
                    automationEnabled = !automationEnabled;
                    Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "OmniWorkstation_Radius".Translate(WorkRadius),
                defaultDesc = "OmniWorkstation_RadiusDesc".Translate(),
                icon = TexCommand.Install,
                action = () =>
                {
                    workRadiusIndex = (workRadiusIndex + 1) % WorkRadii.Length;
                    Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
                }
            };
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (Spawned)
                GenDraw.DrawRadiusRing(Position, WorkRadius, automationEnabled ? Color.cyan : Color.gray);
        }

        public override string GetInspectString()
        {
            string original = base.GetInspectString();
            string status = automationEnabled
                ? "OmniWorkstation_StatusEnabled".Translate(WorkRadius)
                : "OmniWorkstation_StatusDisabled".Translate();
            return original.NullOrEmpty() ? status : original + "\n" + status;
        }
    }

    [DefOf]
    public static class OmniWorkstationDefOf
    {
        public static PawnKindDef FAOC_OmniWorkProxy;

        static OmniWorkstationDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OmniWorkstationDefOf));
        }
    }

    /// <summary>
    /// 代理的运行上下文。使用静态字典是为了让高频路径补丁保持 O(1)，
    /// 地图组件会在代理销毁或地图卸载时主动移除记录。
    /// </summary>
    public static class OmniWorkProxyUtility
    {
        private static readonly Dictionary<Pawn, Building_OmniWorkstation> Assignments =
            new Dictionary<Pawn, Building_OmniWorkstation>();

        public static bool IsProxy(Pawn pawn)
        {
            return pawn != null && pawn.kindDef == OmniWorkstationDefOf.FAOC_OmniWorkProxy;
        }

        public static void Assign(Pawn pawn, Building_OmniWorkstation station)
        {
            if (pawn != null) Assignments[pawn] = station;
        }

        public static void Unassign(Pawn pawn)
        {
            if (pawn != null) Assignments.Remove(pawn);
        }

        public static bool TryGetStation(Pawn pawn, out Building_OmniWorkstation station)
        {
            if (pawn != null && Assignments.TryGetValue(pawn, out station) &&
                station != null && station.Spawned && station.Map == pawn.Map)
                return true;

            station = null;
            return false;
        }
    }

    /// <summary>
    /// 每张地图唯一的万能工作站调度器。工作站数量不会增加逐 Tick 搜索次数；
    /// 真正运行中的原版 Job 数由固定代理池上限约束。
    /// </summary>
    public sealed class MapComponent_OmniWorkstation : MapComponent
    {
        private const int MaxProxyCount = 8;
        private const int AssignmentInterval = 30;
        private const int EmptySearchBackoff = 250;
        private const int MaxStationsCheckedPerAssignment = 16;

        private readonly List<Building_OmniWorkstation> stations =
            new List<Building_OmniWorkstation>();
        private readonly List<ProxyRecord> proxies = new List<ProxyRecord>();
        private readonly JobGiver_Work workGiver = new JobGiver_Work();

        private int stationCursor;
        private bool proxiesRecovered;

        private sealed class ProxyRecord
        {
            public Pawn pawn;
            public Building_OmniWorkstation station;
            public Job issuedJob;
            public int nextSearchTick;
        }

        public MapComponent_OmniWorkstation(Map map) : base(map)
        {
        }

        public void Register(Building_OmniWorkstation station)
        {
            if (station != null && !stations.Contains(station))
                stations.Add(station);
        }

        public void Deregister(Building_OmniWorkstation station)
        {
            stations.Remove(station);
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.station != station) continue;
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(record.pawn);
            }
        }

        public void NotifyConfigurationChanged(Building_OmniWorkstation station)
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.station != station) continue;
                StopIssuedJob(record);
                record.station = null;
                record.nextSearchTick = Find.TickManager.TicksGame;
                OmniWorkProxyUtility.Unassign(record.pawn);
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RecoverExistingThings();
        }

        public override void MapRemoved()
        {
            for (int i = 0; i < proxies.Count; i++)
                OmniWorkProxyUtility.Unassign(proxies[i].pawn);
            proxies.Clear();
            stations.Clear();
            base.MapRemoved();
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (!proxiesRecovered) RecoverExistingThings();

            int tick = Find.TickManager.TicksGame;
            if (tick % AssignmentInterval != map.uniqueID % AssignmentInterval) return;

            RemoveInvalidStations();
            EnsureProxyCount();

            for (int i = 0; i < proxies.Count; i++)
                ServiceProxy(proxies[i], tick);
        }

        private void RecoverExistingThings()
        {
            proxiesRecovered = true;
            stations.Clear();
            proxies.Clear();

            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (buildings[i] is Building_OmniWorkstation station)
                    stations.Add(station);
            }

            List<Thing> pawns = map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (!(pawns[i] is Pawn pawn) || !OmniWorkProxyUtility.IsProxy(pawn)) continue;
                PrepareProxy(pawn);
                proxies.Add(new ProxyRecord { pawn = pawn, nextSearchTick = Find.TickManager.TicksGame });
            }
        }

        private void RemoveInvalidStations()
        {
            for (int i = stations.Count - 1; i >= 0; i--)
            {
                Building_OmniWorkstation station = stations[i];
                if (station == null || station.Destroyed || !station.Spawned || station.Map != map)
                    stations.RemoveAt(i);
            }
            if (stationCursor >= stations.Count) stationCursor = 0;
        }

        private void EnsureProxyCount()
        {
            for (int i = proxies.Count - 1; i >= 0; i--)
            {
                Pawn pawn = proxies[i].pawn;
                if (pawn != null && !pawn.Destroyed && pawn.Spawned && pawn.Map == map) continue;
                OmniWorkProxyUtility.Unassign(pawn);
                proxies.RemoveAt(i);
            }

            int operationalCount = 0;
            for (int i = 0; i < stations.Count; i++)
                if (stations[i].Operational) operationalCount++;

            int wanted = Mathf.Min(MaxProxyCount, operationalCount);
            while (proxies.Count < wanted)
            {
                Pawn pawn = CreateProxy();
                if (pawn == null) break;
                proxies.Add(new ProxyRecord { pawn = pawn, nextSearchTick = Find.TickManager.TicksGame });
            }

            // 只回收空闲代理；正在收尾的 Job 会在下一个调度周期回收，避免吞掉携带物。
            for (int i = proxies.Count - 1; i >= 0 && proxies.Count > wanted; i--)
            {
                ProxyRecord record = proxies[i];
                if (record.issuedJob != null) continue;
                OmniWorkProxyUtility.Unassign(record.pawn);
                if (record.pawn != null && !record.pawn.Destroyed)
                    record.pawn.Destroy(DestroyMode.Vanish);
                proxies.RemoveAt(i);
            }
        }

        private Pawn CreateProxy()
        {
            Building_OmniWorkstation station = FirstOperationalStation();
            if (station == null || OmniWorkstationDefOf.FAOC_OmniWorkProxy == null) return null;

            try
            {
                Pawn pawn = PawnGenerator.GeneratePawn(OmniWorkstationDefOf.FAOC_OmniWorkProxy, Faction.OfPlayer);
                IntVec3 spawnCell = CellFinder.StandableCellNear(station.Position, map, 5f);
                GenSpawn.Spawn(pawn, spawnCell, map);
                PrepareProxy(pawn);
                return pawn;
            }
            catch (Exception error)
            {
                Log.ErrorOnce("[OmniWorkstation] Failed to create work proxy: " + error, 197403221);
                return null;
            }
        }

        private void PrepareProxy(Pawn pawn)
        {
            if (pawn == null) return;
            pawn.mindState.Active = false;
            pawn.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            if (pawn.workSettings != null)
            {
                List<WorkTypeDef> workTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
                for (int i = 0; i < workTypes.Count; i++)
                {
                    WorkTypeDef workType = workTypes[i];
                    if (!pawn.WorkTypeIsDisabled(workType)) pawn.workSettings.SetPriority(workType, 1);
                }
            }

            if (pawn.skills != null)
            {
                List<SkillRecord> skills = pawn.skills.skills;
                for (int i = 0; i < skills.Count; i++)
                {
                    skills[i].Level = 20;
                    skills[i].passion = Passion.Major;
                }
            }

            // 代理仍是合法 Spawned Pawn，但不进入殖民者、警报和普通 AI 使用的 MapPawns 列表。
            map.mapPawns.DeRegisterPawn(pawn);
        }

        private Building_OmniWorkstation FirstOperationalStation()
        {
            for (int i = 0; i < stations.Count; i++)
                if (stations[i].Operational) return stations[i];
            return null;
        }

        private void ServiceProxy(ProxyRecord record, int tick)
        {
            Pawn pawn = record.pawn;
            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map != map) return;

            if (record.issuedJob != null && (record.station == null || !record.station.Operational))
            {
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
            }

            if (record.issuedJob != null && pawn.CurJob != record.issuedJob)
            {
                record.issuedJob = null;
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
            }

            if (record.issuedJob != null) return;

            // 原 Job 结束时 JobTracker 可能立即启动一个 Wait/生活 Job，统一停止后再由调度器分配。
            if (pawn.CurJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);

            if (tick < record.nextSearchTick) return;

            int checkedStations = 0;
            while (checkedStations < MaxStationsCheckedPerAssignment && stations.Count > 0)
            {
                if (stationCursor >= stations.Count) stationCursor = 0;
                Building_OmniWorkstation station = stations[stationCursor++];
                checkedStations++;
                if (!station.Operational || StationAlreadyServiced(station)) continue;

                MoveProxyToStation(pawn, station);
                record.station = station;
                OmniWorkProxyUtility.Assign(pawn, station);

                Job job = TryFindBuildingJob(pawn, station);
                if (job == null)
                {
                    record.station = null;
                    OmniWorkProxyUtility.Unassign(pawn);
                    continue;
                }

                record.issuedJob = job;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced, jobGiver: workGiver,
                    tag: job.workGiverDef?.tagToGive, preToilReservationsCanFail: true);
                if (pawn.CurJob != job)
                {
                    record.issuedJob = null;
                    record.station = null;
                    OmniWorkProxyUtility.Unassign(pawn);
                }
                return;
            }

            record.nextSearchTick = tick + EmptySearchBackoff;
        }

        private bool StationAlreadyServiced(Building_OmniWorkstation station)
        {
            for (int i = 0; i < proxies.Count; i++)
                if (proxies[i].issuedJob != null && proxies[i].station == station) return true;
            return false;
        }

        private void MoveProxyToStation(Pawn pawn, Building_OmniWorkstation station)
        {
            if (station.Covers(pawn.Position)) return;
            IntVec3 cell = CellFinder.StandableCellNear(station.Position, map, 5f);
            pawn.Position = cell;
            pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
        }

        /// <summary>
        /// 通用发现：不识别任何具体 JobDef 或建筑类型，只把范围内建筑交给所有已加载 WorkGiver。
        /// 因而原版或 Mod 新增 WorkGiver 后无需更新本 Mod。
        /// </summary>
        private Job TryFindBuildingJob(Pawn pawn, Building_OmniWorkstation station)
        {
            if (pawn.workSettings == null) return null;
            List<WorkGiver> givers = pawn.workSettings.WorkGiversInOrderNormal;
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;

            for (int giverIndex = 0; giverIndex < givers.Count; giverIndex++)
            {
                WorkGiver giver = givers[giverIndex];
                if (!CanUseWorkGiver(pawn, giver)) continue;

                try
                {
                    Job nonScanJob = giver.NonScanJob(pawn);
                    if (nonScanJob != null)
                    {
                        nonScanJob.workGiverDef = giver.def;
                        if (JobHasBuildingInRange(nonScanJob, station)) return nonScanJob;
                        JobMaker.ReturnToPool(nonScanJob);
                    }

                    if (!(giver is WorkGiver_Scanner scanner) || !giver.def.scanThings) continue;
                    ThingRequest request = scanner.PotentialWorkThingRequest;
                    for (int buildingIndex = 0; buildingIndex < buildings.Count; buildingIndex++)
                    {
                        Building target = buildings[buildingIndex];
                        if (target == null || target == station || !station.Covers(target.Position) ||
                            !request.Accepts(target) || target.IsForbidden(pawn)) continue;
                        if (!scanner.HasJobOnThing(pawn, target)) continue;

                        Job job = scanner.JobOnThing(pawn, target);
                        if (job == null) continue;
                        job.workGiverDef = giver.def;
                        if (JobHasBuildingInRange(job, station)) return job;
                        JobMaker.ReturnToPool(job);
                    }
                }
                catch (Exception error)
                {
                    int key = Gen.HashCombineInt(giver.def.shortHash, station.thingIDNumber);
                    Log.ErrorOnce("[OmniWorkstation] WorkGiver '" + giver.def.defName +
                                  "' failed while scanning: " + error, key);
                }
            }

            return null;
        }

        private static bool CanUseWorkGiver(Pawn pawn, WorkGiver giver)
        {
            WorkGiverDef def = giver.def;
            if (!(def.nonColonistsCanDo || pawn.IsColonist || pawn.IsColonyMech || pawn.IsColonySubhuman))
                return false;
            if (pawn.WorkTagIsDisabled(def.workTags) ||
                def.workType != null && pawn.WorkTypeIsDisabled(def.workType) ||
                giver.ShouldSkip(pawn) || giver.MissingRequiredCapacity(pawn) != null)
                return false;
            return !pawn.RaceProps.IsMechanoid || def.canBeDoneByMechs;
        }

        private static bool JobHasBuildingInRange(Job job, Building_OmniWorkstation station)
        {
            if (job == null) return false;
            if (TargetIsBuildingInRange(job.GetTarget(TargetIndex.A), station) ||
                TargetIsBuildingInRange(job.GetTarget(TargetIndex.B), station) ||
                TargetIsBuildingInRange(job.GetTarget(TargetIndex.C), station))
                return true;

            List<LocalTargetInfo> queue = job.GetTargetQueue(TargetIndex.A);
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (TargetIsBuildingInRange(queue[i], station)) return true;

            queue = job.GetTargetQueue(TargetIndex.B);
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (TargetIsBuildingInRange(queue[i], station)) return true;

            return false;
        }

        private static bool TargetIsBuildingInRange(LocalTargetInfo target, Building_OmniWorkstation station)
        {
            if (!target.IsValid) return false;
            if (target.HasThing)
                return target.Thing.GetInnerIfMinified() is Building && station.Covers(target.Cell);
            return station.Covers(target.Cell) && target.Cell.GetEdifice(station.Map) != null;
        }

        private static void StopIssuedJob(ProxyRecord record)
        {
            if (record.pawn != null && record.pawn.CurJob != null)
                record.pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            record.issuedJob = null;
        }
    }

    /// <summary>代理 Pawn 的允许区域由当前租用的工作站动态决定，不创建上千个 Area 对象。</summary>
    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.InAllowedArea))]
    public static class Patch_OmniWorkProxy_AllowedArea
    {
        [HarmonyPrefix]
        public static bool Prefix(IntVec3 c, Pawn forPawn, ref bool __result)
        {
            if (!OmniWorkProxyUtility.TryGetStation(forPawn, out Building_OmniWorkstation station))
                return true;
            __result = station.Covers(c);
            return false;
        }
    }

    /// <summary>隐藏代理 Pawn，避免其参与选择和渲染。</summary>
    [HarmonyPatch(typeof(InvisibilityUtility), nameof(InvisibilityUtility.IsHiddenFromPlayer))]
    public static class Patch_OmniWorkProxy_Hidden
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// 仅替换代理 Pawn 的空间移动。预约、携带、存放和工作 Toil 仍由原 JobDriver 原样执行。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    public static class Patch_OmniWorkProxy_StartPath
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn, LocalTargetInfo dest, PathEndMode peMode)
        {
            Pawn pawn = ___pawn;
            if (!OmniWorkProxyUtility.TryGetStation(pawn, out Building_OmniWorkstation station))
                return true;

            if (!TryResolveArrivalCell(pawn, station, dest, ref peMode, out IntVec3 arrival))
            {
                pawn.pather.StopDead();
                pawn.jobs.curDriver?.Notify_PatherFailed();
                return false;
            }

            pawn.Position = arrival;
            pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
            pawn.pather.StopDead();
            pawn.jobs.curDriver?.Notify_PatherArrived();
            return false;
        }

        private static bool TryResolveArrivalCell(Pawn pawn, Building_OmniWorkstation station,
            LocalTargetInfo destination, ref PathEndMode peMode, out IntVec3 arrival)
        {
            arrival = IntVec3.Invalid;
            if (!destination.IsValid || destination.HasThing && destination.ThingDestroyed) return false;

            TargetInfo resolvedInfo = GenPath.ResolvePathMode(pawn,
                destination.ToTargetInfo(pawn.Map), ref peMode);
            LocalTargetInfo resolved = (LocalTargetInfo)resolvedInfo;
            if (!resolved.IsValid || !station.Covers(resolved.Cell)) return false;

            if (!pawn.Map.reachability.CanReach(pawn.Position, resolved, peMode, TraverseParms.For(pawn)))
                return false;

            if (peMode == PathEndMode.OnCell)
            {
                IntVec3 cell = resolved.Cell;
                if (CanStandAt(pawn, station, cell))
                {
                    arrival = cell;
                    return true;
                }
                return false;
            }

            CellRect rect = resolved.HasThing
                ? resolved.Thing.OccupiedRect().ExpandedBy(1)
                : CellRect.CenteredOn(resolved.Cell, 1);
            float bestDistance = float.MaxValue;
            foreach (IntVec3 cell in rect)
            {
                if (!CanStandAt(pawn, station, cell) ||
                    !ReachabilityImmediate.CanReachImmediate(cell, resolved, pawn.Map, peMode, pawn)) continue;
                float distance = (cell - pawn.Position).LengthHorizontalSquared;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                arrival = cell;
            }
            return arrival.IsValid;
        }

        private static bool CanStandAt(Pawn pawn, Building_OmniWorkstation station, IntVec3 cell)
        {
            return cell.InBounds(pawn.Map) && station.Covers(cell) && cell.Standable(pawn.Map) &&
                   !cell.IsForbidden(pawn);
        }
    }
}
