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
        public const int MinWorkRadius = 1;
        public const int MaxWorkRadius = 256;

        private bool automationEnabled = true;
        private int workRadius = 30;
        private int legacyWorkRadiusIndex = 1;

        public bool AutomationEnabled => automationEnabled;
        public int WorkRadius => workRadius;

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
            Scribe_Values.Look(ref workRadius, "workRadius", -1);
            Scribe_Values.Look(ref legacyWorkRadiusIndex, "workRadiusIndex", 1);
            if (workRadius < MinWorkRadius)
            {
                int[] legacyRadii = { 15, 30, 60, 100 };
                workRadius = legacyRadii[Mathf.Clamp(legacyWorkRadiusIndex, 0, legacyRadii.Length - 1)];
            }
            workRadius = Mathf.Clamp(workRadius, MinWorkRadius, MaxWorkRadius);
        }

        public void SetWorkRadius(int value)
        {
            int clamped = Mathf.Clamp(value, MinWorkRadius, MaxWorkRadius);
            if (workRadius == clamped) return;
            workRadius = clamped;
            Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
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
                action = () => Find.WindowStack.Add(new Dialog_OmniWorkstationRadius(this))
            };

            MapComponent_OmniWorkstation manager = Map?.GetComponent<MapComponent_OmniWorkstation>();
            if (manager != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "OmniWorkstation_ProxyLimit".Translate(manager.ConfiguredProxyCount),
                    defaultDesc = "OmniWorkstation_ProxyLimitDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    action = () => Find.WindowStack.Add(new Dialog_OmniWorkstationProxyLimit(manager))
                };

                yield return new Command_Action
                {
                    defaultLabel = "OmniWorkstation_OpenStatus".Translate(manager.ActiveProxyCount),
                    defaultDesc = "OmniWorkstation_OpenStatusDesc".Translate(),
                    icon = TexCommand.Draft,
                    action = () => Find.WindowStack.Add(new Dialog_OmniWorkstationStatus(manager))
                };
            }
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

    /// <summary>工作范围设置窗口：滑块负责快速调整，输入框负责精确数值。</summary>
    public sealed class Dialog_OmniWorkstationRadius : Window
    {
        private readonly Building_OmniWorkstation station;
        private int radius;
        private string radiusBuffer;

        public override Vector2 InitialSize => new Vector2(420f, 210f);

        public Dialog_OmniWorkstationRadius(Building_OmniWorkstation station)
        {
            this.station = station;
            radius = station.WorkRadius;
            radiusBuffer = radius.ToString();
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (station == null || station.Destroyed)
            {
                Close();
                return;
            }

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("OmniWorkstation_RadiusCurrent".Translate(radius));

            int sliderValue = Mathf.RoundToInt(listing.Slider(radius,
                Building_OmniWorkstation.MinWorkRadius, Building_OmniWorkstation.MaxWorkRadius));
            if (sliderValue != radius)
            {
                radius = sliderValue;
                radiusBuffer = radius.ToString();
                station.SetWorkRadius(radius);
            }

            Rect inputRect = listing.GetRect(30f);
            Widgets.Label(inputRect.LeftPart(0.4f), "OmniWorkstation_ValueInput".Translate());
            string edited = Widgets.TextField(inputRect.RightPart(0.6f), radiusBuffer);
            if (edited != radiusBuffer)
            {
                radiusBuffer = edited;
                if (int.TryParse(radiusBuffer, out int parsed) &&
                    parsed >= Building_OmniWorkstation.MinWorkRadius &&
                    parsed <= Building_OmniWorkstation.MaxWorkRadius)
                {
                    radius = parsed;
                    station.SetWorkRadius(radius);
                }
            }
            listing.End();
        }
    }

    /// <summary>每张地图共享一个代理上限，任意万能工作站均可打开此窗口修改。</summary>
    public sealed class Dialog_OmniWorkstationProxyLimit : Window
    {
        private readonly MapComponent_OmniWorkstation manager;
        private int proxyCount;
        private string proxyCountBuffer;

        public override Vector2 InitialSize => new Vector2(420f, 210f);

        public Dialog_OmniWorkstationProxyLimit(MapComponent_OmniWorkstation manager)
        {
            this.manager = manager;
            proxyCount = manager.ConfiguredProxyCount;
            proxyCountBuffer = proxyCount.ToString();
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("OmniWorkstation_ProxyLimitCurrent".Translate(proxyCount));

            int sliderValue = Mathf.RoundToInt(listing.Slider(proxyCount,
                MapComponent_OmniWorkstation.MinConfigurableProxyCount,
                MapComponent_OmniWorkstation.MaxConfigurableProxyCount));
            if (sliderValue != proxyCount)
            {
                proxyCount = sliderValue;
                proxyCountBuffer = proxyCount.ToString();
                manager.SetConfiguredProxyCount(proxyCount);
            }

            Rect inputRect = listing.GetRect(30f);
            Widgets.Label(inputRect.LeftPart(0.4f), "OmniWorkstation_ValueInput".Translate());
            string edited = Widgets.TextField(inputRect.RightPart(0.6f), proxyCountBuffer);
            if (edited != proxyCountBuffer)
            {
                proxyCountBuffer = edited;
                if (int.TryParse(proxyCountBuffer, out int parsed) &&
                    parsed >= MapComponent_OmniWorkstation.MinConfigurableProxyCount &&
                    parsed <= MapComponent_OmniWorkstation.MaxConfigurableProxyCount)
                {
                    proxyCount = parsed;
                    manager.SetConfiguredProxyCount(proxyCount);
                }
            }
            listing.End();
        }
    }

    public enum OmniWorkSearchState
    {
        Waiting,
        Queued,
        Searching,
        Continuing,
        Backoff,
        NoIdleProxy
    }

    public struct OmniWorkProxyStatus
    {
        public string name;
        public string work;

        public OmniWorkProxyStatus(string name, string work)
        {
            this.name = name;
            this.work = work;
        }
    }

    /// <summary>只在窗口打开时低频生成工作报告字符串，不增加常驻调度开销。</summary>
    public sealed class Dialog_OmniWorkstationStatus : Window
    {
        private readonly MapComponent_OmniWorkstation manager;
        private readonly List<OmniWorkProxyStatus> activeRows = new List<OmniWorkProxyStatus>();
        private Vector2 scrollPosition;
        private int lastRefreshFrame = -1000;

        public override Vector2 InitialSize => new Vector2(760f, 620f);

        public Dialog_OmniWorkstationStatus(MapComponent_OmniWorkstation manager)
        {
            this.manager = manager;
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Time.frameCount - lastRefreshFrame >= 30)
            {
                manager.FillActiveProxyStatuses(activeRows);
                lastRefreshFrame = Time.frameCount;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "OmniWorkstation_StatusTitle".Translate());
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0f, 38f, inRect.width, 24f),
                "OmniWorkstation_StatusCounts".Translate(manager.ActiveProxyCount, manager.TotalProxyCount,
                    manager.ConfiguredProxyCount));
            Widgets.Label(new Rect(0f, 64f, inRect.width, 24f),
                "OmniWorkstation_SearchState".Translate(SearchStateText(manager.SearchState)));
            Widgets.Label(new Rect(0f, 90f, inRect.width, 24f),
                "OmniWorkstation_NextSearch".Translate(manager.TicksUntilNextSearch));
            Widgets.Label(new Rect(0f, 116f, inRect.width, 24f),
                "OmniWorkstation_LastSearch".Translate(manager.LastSearchWorker, manager.LastSearchWork,
                    manager.LastSearchTick < 0 ? "-" : manager.LastSearchTick.ToString()));

            float listTop = 150f;
            Widgets.DrawLineHorizontal(0f, listTop - 6f, inRect.width);
            Widgets.Label(new Rect(4f, listTop, 150f, 26f), "OmniWorkstation_ProxyColumn".Translate());
            Widgets.Label(new Rect(160f, listTop, inRect.width - 164f, 26f), "OmniWorkstation_WorkColumn".Translate());

            Rect outRect = new Rect(0f, listTop + 28f, inRect.width, inRect.height - listTop - 72f);
            float viewHeight = Mathf.Max(outRect.height, activeRows.Count * 30f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            for (int i = 0; i < activeRows.Count; i++)
            {
                Rect row = new Rect(0f, i * 30f, viewRect.width, 30f);
                if ((i & 1) == 1) Widgets.DrawLightHighlight(row);
                Widgets.Label(new Rect(4f, row.y + 3f, 150f, 24f), activeRows[i].name);
                Widgets.Label(new Rect(160f, row.y + 3f, viewRect.width - 164f, 24f), activeRows[i].work);
            }
            Widgets.EndScrollView();
        }

        private static string SearchStateText(OmniWorkSearchState state)
        {
            return ("OmniWorkstation_Search_" + state).Translate();
        }
    }

    [DefOf]
    public static class OmniWorkstationDefOf
    {
        public static PawnKindDef FAOC_OmniWorkProxy;
        public static HediffDef FAOC_OmniWorkProxyBoost;

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
        private static readonly HashSet<Pawn> ActiveProxies = new HashSet<Pawn>();

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
            if (pawn == null) return;
            Assignments.Remove(pawn);
            ActiveProxies.Remove(pawn);
        }

        public static void SetActive(Pawn pawn, bool active)
        {
            if (pawn == null) return;
            if (active)
                ActiveProxies.Add(pawn);
            else
                ActiveProxies.Remove(pawn);
        }

        public static bool IsActive(Pawn pawn)
        {
            return pawn != null && ActiveProxies.Contains(pawn);
        }

        public static bool TryGetStation(Pawn pawn, out Building_OmniWorkstation station)
        {
            if (pawn != null && Assignments.TryGetValue(pawn, out station) &&
                station != null && station.Spawned && station.Map == pawn.Map)
                return true;

            station = null;
            return false;
        }

        /// <summary>
        /// 清除代理的生活状态和第三方附加状态。此方法只在创建、读档和低频维护时执行。
        /// </summary>
        public static void Sanitize(Pawn pawn)
        {
            if (pawn == null || !IsProxy(pawn)) return;

            if (pawn.needs?.mood?.thoughts?.memories != null)
            {
                MemoryThoughtHandler memoryHandler = pawn.needs.mood.thoughts.memories;
                List<Thought_Memory> memories = memoryHandler.Memories;
                for (int i = memories.Count - 1; i >= 0; i--)
                {
                    Thought_Memory memory = memories[i];
                    if (memory != null && memory.MoodOffset() < 0f)
                        memoryHandler.RemoveMemory(memory);
                }
            }

            if (pawn.needs != null && pawn.needs.AllNeeds.Count > 0)
            {
                // 不调用 Mod Need 的回调，避免其在清理阶段重新注入状态。
                pawn.needs.AllNeeds.Clear();
                pawn.needs.MiscNeeds.Clear();
                pawn.needs.BindDirectNeedFields();
            }

            HediffDef boost = OmniWorkstationDefOf.FAOC_OmniWorkProxyBoost;
            if (pawn.health != null)
            {
                List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
                for (int i = hediffs.Count - 1; i >= 0; i--)
                {
                    if (i >= hediffs.Count) continue;
                    Hediff hediff = hediffs[i];
                    if (hediff != null && hediff.def != boost)
                        pawn.health.RemoveHediff(hediff);
                }
                if (boost != null && !pawn.health.hediffSet.HasHediff(boost))
                    pawn.health.AddHediff(boost);
            }

            if (pawn.skills == null) return;
            List<SkillRecord> skills = pawn.skills.skills;
            for (int i = 0; i < skills.Count; i++)
            {
                SkillRecord skill = skills[i];
                skill.levelInt = 999;
                skill.xpSinceLastLevel = 0f;
                skill.xpSinceMidnight = 0f;
                skill.passion = Passion.Major;
            }
        }
    }

    /// <summary>
    /// 每张地图唯一的万能工作站调度器。工作站数量不会增加逐 Tick 搜索次数；
    /// 真正运行中的原版 Job 数由固定代理池上限约束。
    /// </summary>
    public sealed class MapComponent_OmniWorkstation : MapComponent
    {
        public const int MinConfigurableProxyCount = 1;
        public const int MaxConfigurableProxyCount = 1024;
        private const int DefaultProxyCount = 8;
        private const int MaxProxyCreatesPerAssignment = 8;
        private const int MaxProxyRemovalsPerAssignment = 16;
        private const int AssignmentInterval = 60;
        private const int IsolationMaintenanceInterval = 250;
        private const int EmptySearchBackoff = 60;
        private const int MaxStationsCheckedPerAssignment = 16;

        private readonly List<Building_OmniWorkstation> stations =
            new List<Building_OmniWorkstation>();
        private readonly List<ProxyRecord> proxies = new List<ProxyRecord>();
        private readonly JobGiver_Work workGiver = new JobGiver_Work();

        private int stationCursor;
        private int proxySearchCursor;
        private int nextGlobalSearchTick;
        private bool searchContinuationPending;
        private OmniWorkSearchState searchState = OmniWorkSearchState.Waiting;
        private int lastSearchTick = -1;
        private string lastSearchWorker = "-";
        private string lastSearchWork = "-";
        private bool proxiesRecovered;
        private int configuredProxyCount = DefaultProxyCount;
        private int nextWorkerSequence = 1;

        public int ConfiguredProxyCount => configuredProxyCount;
        public int TotalProxyCount => proxies.Count;
        public OmniWorkSearchState SearchState => searchState;
        public int LastSearchTick => lastSearchTick;
        public string LastSearchWorker => lastSearchWorker;
        public string LastSearchWork => lastSearchWork;
        public int TicksUntilNextSearch => Mathf.Max(0, nextGlobalSearchTick - Find.TickManager.TicksGame);

        public int ActiveProxyCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < proxies.Count; i++)
                    if (OmniWorkProxyUtility.IsActive(proxies[i].pawn)) count++;
                return count;
            }
        }

        private sealed class ProxyRecord
        {
            public Pawn pawn;
            public Building_OmniWorkstation station;
            public Job issuedJob;
        }

        public MapComponent_OmniWorkstation(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref configuredProxyCount, "omniWorkstationProxyCount", DefaultProxyCount);
            Scribe_Values.Look(ref nextWorkerSequence, "omniWorkstationNextWorkerSequence", 1);
            configuredProxyCount = Mathf.Clamp(configuredProxyCount,
                MinConfigurableProxyCount, MaxConfigurableProxyCount);
            if (nextWorkerSequence < 1) nextWorkerSequence = 1;
        }

        public void SetConfiguredProxyCount(int value)
        {
            configuredProxyCount = Mathf.Clamp(value,
                MinConfigurableProxyCount, MaxConfigurableProxyCount);
            WakeSearchPump();
        }

        public void Register(Building_OmniWorkstation station)
        {
            if (station != null && !stations.Contains(station))
            {
                stations.Add(station);
                WakeSearchPump();
            }
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
            WakeSearchPump();
        }

        public void NotifyConfigurationChanged(Building_OmniWorkstation station)
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.station != station) continue;
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(record.pawn);
            }
            // 范围扩大后，立即退出全局“无工作”退避并启动单通道搜索。
            WakeSearchPump();
        }

        private void WakeSearchPump()
        {
            nextGlobalSearchTick = Find.TickManager?.TicksGame ?? 0;
            searchContinuationPending = true;
            searchState = OmniWorkSearchState.Queued;
        }

        public void NotifyProxyBecameIdle()
        {
            WakeSearchPump();
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
            if (tick % IsolationMaintenanceInterval == map.uniqueID % IsolationMaintenanceInterval)
            {
                for (int i = 0; i < proxies.Count; i++)
                    OmniWorkProxyUtility.Sanitize(proxies[i].pawn);
            }
            if (tick % AssignmentInterval == map.uniqueID % AssignmentInterval)
            {
                RemoveInvalidStations();
                EnsureProxyCount();
                for (int i = 0; i < proxies.Count; i++)
                    RefreshProxyState(proxies[i]);
                if (tick >= nextGlobalSearchTick)
                    searchContinuationPending = true;
            }

            if (!searchContinuationPending) return;
            if (!TryGetNextIdleProxy(out ProxyRecord record))
            {
                searchContinuationPending = false;
                nextGlobalSearchTick = tick + AssignmentInterval;
                searchState = OmniWorkSearchState.NoIdleProxy;
                return;
            }

            // 每 Tick 至多执行一次昂贵搜索。成功则下一 Tick 立即交给下一个代理；失败则整体退避。
            searchState = OmniWorkSearchState.Searching;
            lastSearchTick = tick;
            lastSearchWorker = record.pawn?.Name?.ToStringShort ?? "-";
            lastSearchWork = "-";
            if (TryAssignWork(record))
            {
                nextGlobalSearchTick = tick;
                lastSearchWork = SafeJobReport(record.pawn, record.issuedJob);
                searchState = OmniWorkSearchState.Continuing;
            }
            else
            {
                searchContinuationPending = false;
                nextGlobalSearchTick = tick + EmptySearchBackoff;
                searchState = OmniWorkSearchState.Backoff;
            }
        }

        public void FillActiveProxyStatuses(List<OmniWorkProxyStatus> output)
        {
            output.Clear();
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (!OmniWorkProxyUtility.IsActive(record.pawn) || record.issuedJob == null) continue;
                string name = record.pawn.Name?.ToStringShort ?? "Worker";
                output.Add(new OmniWorkProxyStatus(name, SafeJobReport(record.pawn, record.issuedJob)));
            }
        }

        private static string SafeJobReport(Pawn pawn, Job job)
        {
            if (job == null) return "-";
            try
            {
                string report = job.GetReport(pawn);
                if (!report.NullOrEmpty()) return report;
            }
            catch (Exception)
            {
                // 第三方 JobDriver 的报告生成失败时只降级显示 Def，不影响调度窗口。
            }
            return job.def?.label ?? job.def?.defName ?? "-";
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
                EnsureWorkerName(pawn);
                PrepareProxy(pawn);
                proxies.Add(new ProxyRecord { pawn = pawn });
            }
            WakeSearchPump();
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

            // 代理上限属于整张地图；只要存在一个可用工作站，所有代理都可由其并行调度。
            int wanted = operationalCount > 0 ? configuredProxyCount : 0;
            int created = 0;
            while (proxies.Count < wanted && created < MaxProxyCreatesPerAssignment)
            {
                Pawn pawn = CreateProxy();
                if (pawn == null) break;
                proxies.Add(new ProxyRecord { pawn = pawn });
                created++;
            }

            // 只回收空闲代理；正在收尾的 Job 会在下一个调度周期回收，避免吞掉携带物。
            int removed = 0;
            for (int i = proxies.Count - 1;
                 i >= 0 && proxies.Count > wanted && removed < MaxProxyRemovalsPerAssignment;
                 i--)
            {
                ProxyRecord record = proxies[i];
                if (record.issuedJob != null) continue;
                OmniWorkProxyUtility.Unassign(record.pawn);
                if (record.pawn != null && !record.pawn.Destroyed)
                    record.pawn.Destroy(DestroyMode.Vanish);
                proxies.RemoveAt(i);
                removed++;
            }
        }

        private Pawn CreateProxy()
        {
            Building_OmniWorkstation station = FirstOperationalStation();
            if (station == null || OmniWorkstationDefOf.FAOC_OmniWorkProxy == null) return null;

            try
            {
                Pawn pawn = PawnGenerator.GeneratePawn(OmniWorkstationDefOf.FAOC_OmniWorkProxy, Faction.OfPlayer);
                EnsureWorkerName(pawn);
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
            OmniWorkProxyUtility.SetActive(pawn, false);
            OmniWorkProxyUtility.Sanitize(pawn);

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

            // 代理仍是合法 Spawned Pawn，但不进入殖民者、警报和普通 AI 使用的 MapPawns 列表。
            map.mapPawns.DeRegisterPawn(pawn);
        }

        private void EnsureWorkerName(Pawn pawn)
        {
            if (pawn.Name is NameSingle existing && existing.Name.StartsWith("Worker", StringComparison.Ordinal) &&
                int.TryParse(existing.Name.Substring(6), out int sequence) && sequence > 0)
            {
                if (sequence >= nextWorkerSequence) nextWorkerSequence = sequence + 1;
                return;
            }

            pawn.Name = new NameSingle("Worker" + nextWorkerSequence, true);
            nextWorkerSequence++;
        }

        private Building_OmniWorkstation FirstOperationalStation()
        {
            for (int i = 0; i < stations.Count; i++)
                if (stations[i].Operational) return stations[i];
            return null;
        }

        private bool RefreshProxyState(ProxyRecord record)
        {
            Pawn pawn = record.pawn;
            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map != map) return false;

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

            if (record.issuedJob != null) return false;

            // 原 Job 结束时 JobTracker 可能立即启动一个 Wait/生活 Job，统一停止后再由调度器分配。
            if (pawn.CurJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);

            return pawn.CurJob == null;
        }

        private bool TryGetNextIdleProxy(out ProxyRecord result)
        {
            result = null;
            int count = proxies.Count;
            for (int checkedCount = 0; checkedCount < count; checkedCount++)
            {
                if (proxySearchCursor >= count) proxySearchCursor = 0;
                ProxyRecord candidate = proxies[proxySearchCursor++];
                if (!RefreshProxyState(candidate)) continue;
                result = candidate;
                return true;
            }
            return false;
        }

        private bool TryAssignWork(ProxyRecord record)
        {
            Pawn pawn = record.pawn;
            int checkedStations = 0;
            while (checkedStations < MaxStationsCheckedPerAssignment && stations.Count > 0)
            {
                if (stationCursor >= stations.Count) stationCursor = 0;
                Building_OmniWorkstation station = stations[stationCursor++];
                checkedStations++;
                if (!station.Operational) continue;

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
                    return false;
                }
                else
                {
                    OmniWorkProxyUtility.SetActive(pawn, true);
                }
                return true;
            }

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
            OmniWorkProxyUtility.SetActive(record.pawn, false);
            if (record.pawn != null && record.pawn.CurJob != null)
                record.pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            record.issuedJob = null;
        }
    }

    /// <summary>原版技能读取会把 levelInt 截断到 20；代理需要向工作及 Mod 门槛报告真实的 999。</summary>
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.GetLevel))]
    public static class Patch_OmniWorkProxy_SkillLevel
    {
        [HarmonyPostfix]
        public static void Postfix(SkillRecord __instance, ref int __result)
        {
            if (OmniWorkProxyUtility.IsProxy(__instance.Pawn))
                __result = 999;
        }
    }

    /// <summary>空闲代理完全跳过 Pawn Tick；被调度到工作后才恢复 JobDriver 等必要更新。</summary>
    [HarmonyPatch(typeof(Pawn), "Tick")]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_FreezeWhenInactive
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            return !OmniWorkProxyUtility.IsProxy(__instance) || OmniWorkProxyUtility.IsActive(__instance);
        }
    }

    /// <summary>原任务结束的同一刻立即冻结，阻止普通思考树接管空闲代理。</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_DeactivateOnJobEnd
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn ___pawn)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return;
            bool wasActive = OmniWorkProxyUtility.IsActive(___pawn);
            OmniWorkProxyUtility.SetActive(___pawn, false);
            if (wasActive && ___pawn.Spawned)
                ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>().NotifyProxyBecameIdle();
        }
    }

    /// <summary>代理仅作为 JobDriver 载体，始终跳过身体、装备、阴影等地图绘制。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DynamicDrawPhaseAt))]
    public static class Patch_OmniWorkProxy_HideMapDraw
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            return !OmniWorkProxyUtility.IsProxy(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawGUIOverlay))]
    public static class Patch_OmniWorkProxy_HideMapOverlay
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;

            // 工作中只绘制 Worker 序号名称；空闲时不绘制名称或任何其他覆盖层。
            if (OmniWorkProxyUtility.IsActive(__instance) && __instance.Spawned &&
                !__instance.Map.fogGrid.IsFogged(__instance.Position))
            {
                GenMapUI.DrawPawnLabel(__instance,
                    GenMapUI.LabelDrawPosFor(__instance, -0.6f));
            }
            return false;
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
