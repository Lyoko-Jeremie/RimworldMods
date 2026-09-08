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
        private List<string> disabledWorkTypeDefNames = new List<string>();
        private bool allowUnclassifiedWork = true;
        [Unsaved] private HashSet<string> disabledWorkTypeSet;

        public bool AutomationEnabled => automationEnabled;
        public int WorkRadius => workRadius;
        public bool AllowUnclassifiedWork => allowUnclassifiedWork;

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
            Scribe_Collections.Look(ref disabledWorkTypeDefNames, "disabledWorkTypeDefNames", LookMode.Value);
            Scribe_Values.Look(ref allowUnclassifiedWork, "allowUnclassifiedWork", true);
            if (disabledWorkTypeDefNames == null) disabledWorkTypeDefNames = new List<string>();
            disabledWorkTypeSet = null;
            if (workRadius < MinWorkRadius)
            {
                int[] legacyRadii = { 15, 30, 60, 100 };
                workRadius = legacyRadii[Mathf.Clamp(legacyWorkRadiusIndex, 0, legacyRadii.Length - 1)];
            }
            workRadius = Mathf.Clamp(workRadius, MinWorkRadius, MaxWorkRadius);
        }

        public bool AllowsWorkType(WorkTypeDef workType)
        {
            if (workType == null) return allowUnclassifiedWork;
            EnsureWorkTypeSet();
            return !disabledWorkTypeSet.Contains(workType.defName);
        }

        public void SetWorkTypeEnabled(WorkTypeDef workType, bool enabled)
        {
            if (workType == null)
            {
                if (allowUnclassifiedWork == enabled) return;
                allowUnclassifiedWork = enabled;
            }
            else
            {
                EnsureWorkTypeSet();
                bool changed = enabled
                    ? disabledWorkTypeSet.Remove(workType.defName)
                    : disabledWorkTypeSet.Add(workType.defName);
                if (!changed) return;
                SyncDisabledWorkTypeList();
            }

            Map?.GetComponent<MapComponent_OmniWorkstation>()
                .NotifyWorkFilterChanged(this, workType, enabled);
        }

        public void SetAllWorkTypesEnabled(bool enabled)
        {
            EnsureWorkTypeSet();
            disabledWorkTypeSet.Clear();
            if (!enabled)
            {
                List<WorkTypeDef> workTypes = OmniWorkCatalog.WorkTypes;
                for (int i = 0; i < workTypes.Count; i++)
                    disabledWorkTypeSet.Add(workTypes[i].defName);
            }
            allowUnclassifiedWork = enabled;
            SyncDisabledWorkTypeList();
            Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
        }

        public void CopyWorkFilterFrom(Building_OmniWorkstation source)
        {
            if (source == null || source == this) return;
            source.EnsureWorkTypeSet();
            disabledWorkTypeDefNames = new List<string>(source.disabledWorkTypeSet);
            disabledWorkTypeSet = new HashSet<string>(source.disabledWorkTypeSet, StringComparer.Ordinal);
            allowUnclassifiedWork = source.allowUnclassifiedWork;
        }

        private void EnsureWorkTypeSet()
        {
            if (disabledWorkTypeSet != null) return;
            disabledWorkTypeSet = new HashSet<string>(StringComparer.Ordinal);
            if (disabledWorkTypeDefNames == null) return;
            for (int i = 0; i < disabledWorkTypeDefNames.Count; i++)
            {
                string defName = disabledWorkTypeDefNames[i];
                if (!defName.NullOrEmpty()) disabledWorkTypeSet.Add(defName);
            }
        }

        private void SyncDisabledWorkTypeList()
        {
            disabledWorkTypeDefNames.Clear();
            foreach (string defName in disabledWorkTypeSet)
                disabledWorkTypeDefNames.Add(defName);
            disabledWorkTypeDefNames.Sort(StringComparer.Ordinal);
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

            yield return new Command_Action
            {
                defaultLabel = "OmniWorkstation_WorkFilter".Translate(),
                defaultDesc = "OmniWorkstation_WorkFilterDesc".Translate(),
                icon = TexCommand.ForbidOff,
                action = () => Find.WindowStack.Add(new Dialog_OmniWorkstationWorkFilter(this))
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

    public sealed class OmniWorkGiverGroup
    {
        public WorkTypeDef workType;
        public int priorityInType;
        public readonly List<WorkGiverDef> giverDefs = new List<WorkGiverDef>();
    }

    /// <summary>所有工作目录只在 Def 加载后构建一次；新增 Mod WorkGiver 会自动进入目录。</summary>
    [StaticConstructorOnStartup]
    public static class OmniWorkCatalog
    {
        public static readonly List<WorkTypeDef> WorkTypes = new List<WorkTypeDef>();
        public static readonly List<OmniWorkGiverGroup> Groups = new List<OmniWorkGiverGroup>();

        static OmniWorkCatalog()
        {
            List<WorkTypeDef> allTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < allTypes.Count; i++)
            {
                WorkTypeDef workType = allTypes[i];
                if (workType.workGiversByPriority.Count > 0) WorkTypes.Add(workType);
            }
            WorkTypes.Sort(CompareWorkTypes);

            for (int i = 0; i < WorkTypes.Count; i++)
                AddGroupsForType(WorkTypes[i], WorkTypes[i].workGiversByPriority);

            List<WorkGiverDef> allGivers = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            List<WorkGiverDef> unclassified = new List<WorkGiverDef>();
            for (int i = 0; i < allGivers.Count; i++)
                if (allGivers[i].workType == null) unclassified.Add(allGivers[i]);
            unclassified.Sort((a, b) => b.priorityInType.CompareTo(a.priorityInType));
            AddGroupsForType(null, unclassified);
        }

        private static int CompareWorkTypes(WorkTypeDef a, WorkTypeDef b)
        {
            int priority = b.naturalPriority.CompareTo(a.naturalPriority);
            return priority != 0
                ? priority
                : string.Compare(a.defName, b.defName, StringComparison.Ordinal);
        }

        private static void AddGroupsForType(WorkTypeDef workType, List<WorkGiverDef> defs)
        {
            OmniWorkGiverGroup group = null;
            int lastPriority = int.MinValue;
            for (int i = 0; i < defs.Count; i++)
            {
                WorkGiverDef def = defs[i];
                if (group == null || def.priorityInType != lastPriority)
                {
                    group = new OmniWorkGiverGroup
                    {
                        workType = workType,
                        priorityInType = def.priorityInType
                    };
                    Groups.Add(group);
                    lastPriority = def.priorityInType;
                }
                // Worker 保持原版懒加载；单个 Mod 构造器异常不会让整个静态目录初始化失败。
                group.giverDefs.Add(def);
            }
        }

        public static string WorkTypeLabel(WorkTypeDef workType)
        {
            if (workType == null) return "OmniWorkstation_UnclassifiedWork".Translate();
            if (!workType.labelShort.NullOrEmpty()) return workType.labelShort.CapitalizeFirst();
            return workType.LabelCap;
        }
    }

    /// <summary>每个工作站独立保存过滤器；重叠范围采用“任一覆盖站允许即可”的并集语义。</summary>
    public sealed class Dialog_OmniWorkstationWorkFilter : Window
    {
        private readonly Building_OmniWorkstation station;
        private Vector2 scrollPosition;
        private string searchText = string.Empty;

        public override Vector2 InitialSize => new Vector2(680f, 720f);

        public Dialog_OmniWorkstationWorkFilter(Building_OmniWorkstation station)
        {
            this.station = station;
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

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "OmniWorkstation_WorkFilterTitle".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 38f, inRect.width, 44f), "OmniWorkstation_WorkFilterHelp".Translate());

            Widgets.Label(new Rect(0f, 88f, 90f, 28f), "OmniWorkstation_FilterSearch".Translate());
            searchText = Widgets.TextField(new Rect(94f, 86f, inRect.width - 94f, 30f), searchText);

            float buttonY = 124f;
            float gap = 6f;
            float buttonWidth = (inRect.width - gap * 3f) / 4f;
            if (Widgets.ButtonText(new Rect(0f, buttonY, buttonWidth, 30f), "OmniWorkstation_EnableAll".Translate()))
                station.SetAllWorkTypesEnabled(true);
            if (Widgets.ButtonText(new Rect(buttonWidth + gap, buttonY, buttonWidth, 30f), "OmniWorkstation_DisableAll".Translate()))
                station.SetAllWorkTypesEnabled(false);
            if (Widgets.ButtonText(new Rect((buttonWidth + gap) * 2f, buttonY, buttonWidth, 30f), "OmniWorkstation_ResetFilter".Translate()))
                station.SetAllWorkTypesEnabled(true);
            if (Widgets.ButtonText(new Rect((buttonWidth + gap) * 3f, buttonY, buttonWidth, 30f), "OmniWorkstation_ApplyAllStations".Translate()))
                station.Map?.GetComponent<MapComponent_OmniWorkstation>().ApplyWorkFilterToAll(station);

            Rect outRect = new Rect(0f, 164f, inRect.width, inRect.height - 208f);
            int visibleCount = CountVisibleRows();
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(outRect.height, visibleCount * 34f));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            float y = 0f;
            DrawUnclassifiedRow(viewRect.width, ref y);
            List<WorkTypeDef> workTypes = OmniWorkCatalog.WorkTypes;
            for (int i = 0; i < workTypes.Count; i++)
            {
                WorkTypeDef workType = workTypes[i];
                if (!MatchesSearch(workType)) continue;
                Rect row = new Rect(0f, y, viewRect.width, 32f);
                if ((Mathf.RoundToInt(y / 34f) & 1) == 1) Widgets.DrawLightHighlight(row);
                bool enabled = station.AllowsWorkType(workType);
                string label = OmniWorkCatalog.WorkTypeLabel(workType);
                Widgets.CheckboxLabeled(row, label, ref enabled);
                TooltipHandler.TipRegion(row, workType.defName + GetModSuffix(workType));
                if (enabled != station.AllowsWorkType(workType))
                    station.SetWorkTypeEnabled(workType, enabled);
                y += 34f;
            }
            Widgets.EndScrollView();
        }

        private void DrawUnclassifiedRow(float width, ref float y)
        {
            if (!searchText.NullOrEmpty() &&
                !"OmniWorkstation_UnclassifiedWork".Translate().ToString()
                    .ToLowerInvariant().Contains(searchText.ToLowerInvariant())) return;

            Rect row = new Rect(0f, y, width, 32f);
            bool enabled = station.AllowUnclassifiedWork;
            Widgets.CheckboxLabeled(row, "OmniWorkstation_UnclassifiedWork".Translate(), ref enabled);
            TooltipHandler.TipRegion(row, "OmniWorkstation_UnclassifiedWorkDesc".Translate());
            if (enabled != station.AllowUnclassifiedWork)
                station.SetWorkTypeEnabled(null, enabled);
            y += 34f;
        }

        private int CountVisibleRows()
        {
            int count = searchText.NullOrEmpty() ||
                        "OmniWorkstation_UnclassifiedWork".Translate().ToString()
                            .ToLowerInvariant().Contains(searchText.ToLowerInvariant()) ? 1 : 0;
            List<WorkTypeDef> workTypes = OmniWorkCatalog.WorkTypes;
            for (int i = 0; i < workTypes.Count; i++)
                if (MatchesSearch(workTypes[i])) count++;
            return count;
        }

        private bool MatchesSearch(WorkTypeDef workType)
        {
            if (searchText.NullOrEmpty()) return true;
            string needle = searchText.ToLowerInvariant();
            if (OmniWorkCatalog.WorkTypeLabel(workType).ToLowerInvariant().Contains(needle) ||
                workType.defName.ToLowerInvariant().Contains(needle)) return true;
            string modName = workType.modContentPack?.Name;
            return !modName.NullOrEmpty() && modName.ToLowerInvariant().Contains(needle);
        }

        private static string GetModSuffix(WorkTypeDef workType)
        {
            string modName = workType.modContentPack?.Name;
            return modName.NullOrEmpty() ? string.Empty : "\n" + modName;
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
            Widgets.Label(new Rect(0f, 142f, inRect.width, 24f),
                "OmniWorkstation_LastSearchSource".Translate(manager.LastSearchSource));

            float listTop = 176f;
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
        public static BackstoryDef FAOC_OmniWorkProxyChildhood;
        public static BackstoryDef FAOC_OmniWorkProxyAdulthood;

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

            if (pawn.story != null)
            {
                pawn.story.Childhood = OmniWorkstationDefOf.FAOC_OmniWorkProxyChildhood;
                pawn.story.Adulthood = OmniWorkstationDefOf.FAOC_OmniWorkProxyAdulthood;
                pawn.story.traits?.allTraits.Clear();
                pawn.story.Title = null;
            }

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
    public sealed class MapComponent_OmniWorkstation : MapComponent, IThingHolder
    {
        public const int MinConfigurableProxyCount = 1;
        public const int MaxConfigurableProxyCount = 1024;
        private const int DefaultProxyCount = 8;
        private const int MaxProxyCreatesPerAssignment = 8;
        private const int MaxProxyRemovalsPerAssignment = 16;
        private const int AssignmentInterval = 60;
        private const int IsolationMaintenanceInterval = 250;
        private const int EmptySearchBackoff = 60;

        private readonly List<Building_OmniWorkstation> stations =
            new List<Building_OmniWorkstation>();
        private readonly Dictionary<Building_OmniWorkstation, StationRuntime> stationStates =
            new Dictionary<Building_OmniWorkstation, StationRuntime>();
        private readonly List<ProxyRecord> proxies = new List<ProxyRecord>();
        private ThingOwner<Pawn> sleepingProxies;
        private readonly JobGiver_Work workGiver = new JobGiver_Work();
        private readonly Predicate<Thing> thingSearchValidator;
        private readonly Func<Thing, float> thingPriorityGetter;

        private Pawn searchPawn;
        private Building_OmniWorkstation searchStation;
        private WorkGiver_Scanner searchScanner;

        private int stationCursor;
        private int proxySearchCursor;
        private int nextPumpTick;
        private OmniWorkSearchState searchState = OmniWorkSearchState.Waiting;
        private int lastSearchTick = -1;
        private string lastSearchWorker = "-";
        private string lastSearchWork = "-";
        private Building_OmniWorkstation lastSearchStation;
        private WorkTypeDef lastSearchWorkType;
        private WorkGiverDef lastSearchGiver;
        private int lastSearchPriority;
        private bool lastSearchGroupValid;
        private bool proxiesRecovered;
        private int configuredProxyCount = DefaultProxyCount;
        private int nextWorkerSequence = 1;

        public int ConfiguredProxyCount => configuredProxyCount;
        public int TotalProxyCount => proxies.Count;
        public OmniWorkSearchState SearchState => searchState;
        public int LastSearchTick => lastSearchTick;
        public string LastSearchWorker => lastSearchWorker;
        public string LastSearchWork => lastSearchWork;
        public string LastSearchSource
        {
            get
            {
                if (lastSearchStation == null) return "-";
                string source = "OmniWorkstation_SearchStationFormat".Translate(lastSearchStation.LabelCap,
                    lastSearchStation.Position.x, lastSearchStation.Position.z);
                if (lastSearchGroupValid)
                    source = "OmniWorkstation_SearchGroupFormat".Translate(source,
                        OmniWorkCatalog.WorkTypeLabel(lastSearchWorkType), lastSearchPriority);
                if (lastSearchGiver != null)
                    source = "OmniWorkstation_SearchGiverFormat".Translate(source, lastSearchGiver.defName);
                return source;
            }
        }
        public int TicksUntilNextSearch => nextPumpTick == int.MaxValue
            ? 0
            : Mathf.Max(0, nextPumpTick - Find.TickManager.TicksGame);

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

        private sealed class StationRuntime
        {
            public Building_OmniWorkstation station;
            public int nextSearchTick;
            public int consecutiveFailures;
            public bool hot;
            public int groupCursor;
            public int burstGroupIndex = -1;
            public int burstRemaining;
        }

        private enum WorkSearchStepResult
        {
            Found,
            Continue,
            Exhausted
        }

        public MapComponent_OmniWorkstation(Map map) : base(map)
        {
            sleepingProxies = new ThingOwner<Pawn>(this, false, LookMode.Deep);
            sleepingProxies.dontTickContents = true;
            thingSearchValidator = ValidateThingCandidate;
            thingPriorityGetter = GetThingPriority;
        }

        public IThingHolder ParentHolder => map;

        public ThingOwner GetDirectlyHeldThings()
        {
            return sleepingProxies;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, sleepingProxies);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref sleepingProxies, "omniWorkstationSleepingProxies", this);
            if (sleepingProxies == null)
                sleepingProxies = new ThingOwner<Pawn>(this, false, LookMode.Deep);
            sleepingProxies.dontTickContents = true;
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
            WakeAllStations();
        }

        public void Register(Building_OmniWorkstation station)
        {
            if (station != null && !stations.Contains(station))
            {
                stations.Add(station);
                GetOrCreateStationRuntime(station).nextSearchTick = CurrentTick;
                WakePumpNow();
            }
        }

        public void Deregister(Building_OmniWorkstation station)
        {
            stations.Remove(station);
            stationStates.Remove(station);
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.station != station) continue;
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(record.pawn);
            }
            WakePumpNow();
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
            // 范围或开关变化只唤醒当前工作站，不影响其他工作站的独立退避状态。
            StationRuntime runtime = GetOrCreateStationRuntime(station);
            runtime.nextSearchTick = CurrentTick;
            runtime.consecutiveFailures = 0;
            runtime.hot = true;
            runtime.groupCursor = 0;
            runtime.burstGroupIndex = -1;
            runtime.burstRemaining = 0;
            WakePumpNow();
        }

        public void NotifyWorkFilterChanged(Building_OmniWorkstation station, WorkTypeDef workType, bool enabled)
        {
            if (!enabled)
            {
                for (int i = 0; i < proxies.Count; i++)
                {
                    ProxyRecord record = proxies[i];
                    if (record.station != station || record.issuedJob?.workGiverDef?.workType != workType) continue;
                    StopIssuedJob(record);
                    record.station = null;
                    OmniWorkProxyUtility.Unassign(record.pawn);
                }
            }

            StationRuntime runtime = GetOrCreateStationRuntime(station);
            runtime.nextSearchTick = CurrentTick;
            runtime.consecutiveFailures = 0;
            runtime.hot = true;
            runtime.groupCursor = 0;
            runtime.burstGroupIndex = -1;
            runtime.burstRemaining = 0;
            WakePumpNow();
        }

        public void ApplyWorkFilterToAll(Building_OmniWorkstation source)
        {
            for (int i = 0; i < stations.Count; i++)
            {
                Building_OmniWorkstation station = stations[i];
                if (station == source) continue;
                station.CopyWorkFilterFrom(source);
                NotifyConfigurationChanged(station);
            }
            Messages.Message("OmniWorkstation_FilterApplied".Translate(stations.Count), MessageTypeDefOf.TaskCompletion, false);
        }

        private int CurrentTick => Find.TickManager?.TicksGame ?? 0;

        private void WakePumpNow()
        {
            nextPumpTick = CurrentTick;
            searchState = OmniWorkSearchState.Queued;
        }

        private void WakeAllStations()
        {
            int tick = CurrentTick;
            for (int i = 0; i < stations.Count; i++)
            {
                StationRuntime runtime = GetOrCreateStationRuntime(stations[i]);
                runtime.nextSearchTick = tick;
                runtime.consecutiveFailures = 0;
                runtime.hot = true;
                runtime.groupCursor = 0;
                runtime.burstGroupIndex = -1;
                runtime.burstRemaining = 0;
            }
            WakePumpNow();
        }

        public void NotifyProxyBecameIdle(Pawn pawn = null)
        {
            if (pawn != null)
            {
                for (int i = 0; i < proxies.Count; i++)
                {
                    ProxyRecord record = proxies[i];
                    if (record.pawn != pawn) continue;
                    record.issuedJob = null;
                    record.station = null;
                    OmniWorkProxyUtility.Unassign(pawn);
                    break;
                }
            }
            WakePumpNow();
        }

        private StationRuntime GetOrCreateStationRuntime(Building_OmniWorkstation station)
        {
            if (!stationStates.TryGetValue(station, out StationRuntime runtime))
            {
                runtime = new StationRuntime
                {
                    station = station,
                    nextSearchTick = CurrentTick,
                    hot = true
                };
                stationStates.Add(station, runtime);
            }
            return runtime;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RecoverExistingThings();
        }

        public override void MapRemoved()
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                Pawn pawn = proxies[i].pawn;
                OmniWorkProxyUtility.Unassign(pawn);
                if (pawn != null && pawn.Spawned && pawn.Map == map)
                    pawn.Destroy(DestroyMode.Vanish);
            }
            sleepingProxies.ClearAndDestroyContents();
            proxies.Clear();
            stations.Clear();
            stationStates.Clear();
            base.MapRemoved();
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (!proxiesRecovered) RecoverExistingThings();

            // JobTracker 的结束回调只做标记；在地图组件中统一完成安全的反生成和收容。
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord sleepingRecord = proxies[i];
                if (sleepingRecord.issuedJob == null && sleepingRecord.pawn != null && sleepingRecord.pawn.Spawned &&
                    !OmniWorkProxyUtility.IsActive(sleepingRecord.pawn))
                    PutProxyToSleep(sleepingRecord);
            }

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
                int scheduledTick = ComputeNextPumpTick(tick);
                if (scheduledTick < nextPumpTick) nextPumpTick = scheduledTick;
            }

            if (tick < nextPumpTick) return;
            if (!TryGetNextIdleProxy(out ProxyRecord record))
            {
                nextPumpTick = int.MaxValue;
                searchState = OmniWorkSearchState.NoIdleProxy;
                return;
            }

            if (!TryGetNextDueStation(tick, out StationRuntime stationRuntime, out int earliestTick))
            {
                nextPumpTick = earliestTick;
                searchState = OmniWorkSearchState.Waiting;
                return;
            }

            // 每 Tick 至多搜索一个工作站；成功和失败都轮转到下一站。
            searchState = OmniWorkSearchState.Searching;
            lastSearchTick = tick;
            lastSearchWorker = record.pawn?.Name?.ToStringShort ?? "-";
            lastSearchWork = "-";
            lastSearchStation = stationRuntime.station;
            lastSearchWorkType = null;
            lastSearchGiver = null;
            lastSearchGroupValid = false;
            WorkSearchStepResult stepResult = TryAssignWork(record, stationRuntime);
            if (stepResult == WorkSearchStepResult.Found)
            {
                stationRuntime.nextSearchTick = tick + 1;
                stationRuntime.consecutiveFailures = 0;
                stationRuntime.hot = true;
                lastSearchWork = SafeJobReport(record.pawn, record.issuedJob);
                searchState = OmniWorkSearchState.Continuing;
            }
            else if (stepResult == WorkSearchStepResult.Exhausted)
            {
                stationRuntime.nextSearchTick = tick + EmptySearchBackoff;
                stationRuntime.consecutiveFailures++;
                stationRuntime.hot = false;
                searchState = OmniWorkSearchState.Backoff;
            }
            else
            {
                stationRuntime.nextSearchTick = tick + 1;
                searchState = OmniWorkSearchState.Queued;
            }
            // 下一 Tick 再选择工作站；若届时没有到期站点，选择器会一次性算出最早唤醒时间。
            nextPumpTick = tick + 1;
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
            stationStates.Clear();
            proxies.Clear();

            List<Pawn> sleeping = sleepingProxies.InnerListForReading;
            for (int i = 0; i < sleeping.Count; i++)
            {
                Pawn pawn = sleeping[i];
                if (pawn == null || pawn.Destroyed || !OmniWorkProxyUtility.IsProxy(pawn)) continue;
                EnsureWorkerName(pawn);
                PrepareProxy(pawn);
                proxies.Add(new ProxyRecord { pawn = pawn });
            }

            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (buildings[i] is Building_OmniWorkstation station)
                {
                    stations.Add(station);
                    GetOrCreateStationRuntime(station).nextSearchTick = CurrentTick;
                }
            }

            List<Thing> pawns = map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn);
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                if (!(pawns[i] is Pawn pawn) || !OmniWorkProxyUtility.IsProxy(pawn)) continue;
                EnsureWorkerName(pawn);
                PrepareProxy(pawn);
                ProxyRecord record = new ProxyRecord { pawn = pawn };
                proxies.Add(record);
                PutProxyToSleep(record);
            }
            WakePumpNow();
        }

        private void RemoveInvalidStations()
        {
            for (int i = stations.Count - 1; i >= 0; i--)
            {
                Building_OmniWorkstation station = stations[i];
                if (station == null || station.Destroyed || !station.Spawned || station.Map != map)
                {
                    if (station != null) stationStates.Remove(station);
                    stations.RemoveAt(i);
                }
            }
            if (stationCursor >= stations.Count) stationCursor = 0;
        }

        private void EnsureProxyCount()
        {
            for (int i = proxies.Count - 1; i >= 0; i--)
            {
                Pawn pawn = proxies[i].pawn;
                if (pawn != null && !pawn.Destroyed &&
                    ((pawn.Spawned && pawn.Map == map) || sleepingProxies.Contains(pawn))) continue;
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
            if (created > 0) WakePumpNow();

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
                {
                    if (sleepingProxies.Contains(record.pawn)) sleepingProxies.Remove(record.pawn);
                    record.pawn.Destroy(DestroyMode.Vanish);
                }
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
                PrepareProxy(pawn);
                if (!sleepingProxies.TryAdd(pawn))
                {
                    pawn.Destroy(DestroyMode.Vanish);
                    return null;
                }
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

            // 工作中的代理仍是合法 Pawn，但不进入殖民者、警报和普通 AI 使用的 MapPawns 列表。
            if (pawn.Spawned && pawn.Map == map)
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
            if (pawn == null || pawn.Destroyed) return false;
            if (!pawn.Spawned)
                return record.issuedJob == null && sleepingProxies.Contains(pawn);
            if (pawn.Map != map) return false;

            if (record.issuedJob != null && (record.station == null || !record.station.Operational))
            {
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
            }

            if (record.issuedJob != null && pawn.CurJob != record.issuedJob)
            {
                // 原版可能插入机会任务或 finalizer；自主思考入口已被禁止，因此当前 Job
                // 非空时可以安全视为同一工作链并继续跟踪。
                if (pawn.CurJob != null && !IsIdleJob(pawn.CurJob))
                {
                    record.issuedJob = pawn.CurJob;
                    OmniWorkProxyUtility.SetActive(pawn, true);
                    return false;
                }

                record.issuedJob = null;
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
            }

            if (record.issuedJob != null) return false;

            // 原 Job 结束时 JobTracker 可能立即启动一个 Wait/生活 Job，统一停止后再由调度器分配。
            if (pawn.CurJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            PutProxyToSleep(record);
            return sleepingProxies.Contains(pawn);
        }

        private static bool IsIdleJob(Job job)
        {
            return job?.def == JobDefOf.Wait || job?.def == JobDefOf.Wait_MaintainPosture;
        }

        /// <summary>按休眠舱的方式反生成并深保存代理；容器本身从不 Tick 内容物。</summary>
        private void PutProxyToSleep(ProxyRecord record)
        {
            Pawn pawn = record?.pawn;
            if (pawn == null || pawn.Destroyed) return;

            OmniWorkProxyUtility.SetActive(pawn, false);
            OmniWorkProxyUtility.Unassign(pawn);
            record.issuedJob = null;
            record.station = null;
            if (sleepingProxies.Contains(pawn)) return;

            if (pawn.Spawned)
            {
                if (pawn.Map != map) return;
                pawn.DeSpawn(DestroyMode.Vanish);
            }
            if (pawn.holdingOwner != null)
                pawn.holdingOwner.Remove(pawn);
            if (!sleepingProxies.TryAdd(pawn))
                Log.ErrorOnce("[OmniWorkstation] Failed to put work proxy into cryptosleep storage: " + pawn,
                    Gen.HashCombineInt(pawn.thingIDNumber, 19377421));
        }

        /// <summary>仅在搜索原版工作或执行 Job 时出舱；respawningAfterLoad 避免人口统计副作用。</summary>
        private bool WakeProxyAtStation(Pawn pawn, Building_OmniWorkstation station)
        {
            if (pawn == null || pawn.Destroyed || station == null || !station.Spawned) return false;
            IntVec3 cell = station.Position.Standable(map)
                ? station.Position
                : CellFinder.StandableCellNear(station.Position, map, 5f);
            if (!pawn.Spawned)
            {
                GenSpawn.Spawn(pawn, cell, map, Rot4.North, WipeMode.Vanish,
                    respawningAfterLoad: true);
                if (!pawn.Spawned) return false;
                // PawnComponentsUtility 会在出舱生成时重建 Need 等组件，必须再次清理。
                pawn.mindState.Active = false;
                OmniWorkProxyUtility.Sanitize(pawn);
                map.mapPawns.DeRegisterPawn(pawn);
            }
            else
            {
                MoveProxyToStation(pawn, station);
            }
            return true;
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

        private bool TryGetNextDueStation(int tick, out StationRuntime result, out int earliestTick)
        {
            result = null;
            earliestTick = int.MaxValue;
            int count = stations.Count;
            for (int checkedCount = 0; checkedCount < count; checkedCount++)
            {
                if (stationCursor >= count) stationCursor = 0;
                Building_OmniWorkstation station = stations[stationCursor++];
                if (station == null || !station.Operational) continue;

                StationRuntime runtime = GetOrCreateStationRuntime(station);
                if (runtime.nextSearchTick < earliestTick)
                    earliestTick = runtime.nextSearchTick;
                if (runtime.nextSearchTick > tick) continue;

                result = runtime;
                return true;
            }
            return false;
        }

        private int ComputeNextPumpTick(int minimumTick)
        {
            int earliest = int.MaxValue;
            for (int i = 0; i < stations.Count; i++)
            {
                Building_OmniWorkstation station = stations[i];
                if (station == null || !station.Operational) continue;
                int stationTick = GetOrCreateStationRuntime(station).nextSearchTick;
                if (stationTick < earliest) earliest = stationTick;
            }
            if (earliest == int.MaxValue) return int.MaxValue;
            return Mathf.Max(minimumTick, earliest);
        }

        private WorkSearchStepResult TryAssignWork(ProxyRecord record, StationRuntime runtime)
        {
            Pawn pawn = record.pawn;
            Building_OmniWorkstation station = runtime.station;
            List<OmniWorkGiverGroup> groups = OmniWorkCatalog.Groups;
            if (station == null || !station.Operational || groups.Count == 0)
                return WorkSearchStepResult.Exhausted;

            int groupIndex;
            if (runtime.burstRemaining > 0 && runtime.burstGroupIndex >= 0 &&
                runtime.burstGroupIndex < groups.Count &&
                station.AllowsWorkType(groups[runtime.burstGroupIndex].workType))
            {
                groupIndex = runtime.burstGroupIndex;
            }
            else
            {
                runtime.burstGroupIndex = -1;
                runtime.burstRemaining = 0;
                // 被过滤的组只做 O(1) 跳过；每 Tick 最多实际执行一个允许组的昂贵搜索。
                while (runtime.groupCursor < groups.Count &&
                       !station.AllowsWorkType(groups[runtime.groupCursor].workType))
                    runtime.groupCursor++;
                groupIndex = runtime.groupCursor++;
            }

            if (groupIndex >= groups.Count)
            {
                runtime.groupCursor = 0;
                return WorkSearchStepResult.Exhausted;
            }

            OmniWorkGiverGroup group = groups[groupIndex];
            lastSearchWorkType = group.workType;
            lastSearchPriority = group.priorityInType;
            lastSearchGroupValid = true;

            if (!WakeProxyAtStation(pawn, station))
                return WorkSearchStepResult.Continue;
            record.station = station;
            OmniWorkProxyUtility.Assign(pawn, station);

            Job job = TryFindJobInGroup(pawn, station, group);
            if (job == null)
            {
                PutProxyToSleep(record);
                if (runtime.burstGroupIndex == groupIndex)
                {
                    runtime.burstGroupIndex = -1;
                    runtime.burstRemaining = 0;
                    runtime.groupCursor = groupIndex + 1;
                }
                if (runtime.groupCursor >= groups.Count)
                {
                    runtime.groupCursor = 0;
                    return WorkSearchStepResult.Exhausted;
                }
                return WorkSearchStepResult.Continue;
            }

            if (runtime.burstGroupIndex == groupIndex)
            {
                runtime.burstRemaining--;
                if (runtime.burstRemaining <= 0)
                {
                    runtime.burstGroupIndex = -1;
                    runtime.burstRemaining = 0;
                    runtime.groupCursor = groupIndex + 1;
                }
            }
            else if (sleepingProxies.Count > 0)
            {
                // 同一组连续填满当前所有空闲代理；填满后继续后续组，避免搬运类永久饥饿。
                runtime.burstGroupIndex = groupIndex;
                runtime.burstRemaining = sleepingProxies.Count;
            }
            record.issuedJob = job;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced, jobGiver: workGiver,
                tag: job.workGiverDef?.tagToGive, preToilReservationsCanFail: true);
            if (pawn.CurJob == null)
            {
                PutProxyToSleep(record);
                return WorkSearchStepResult.Continue;
            }

            // StartJob 可能先插入机会任务并把原任务入队，跟踪实际运行中的 Job。
            record.issuedJob = pawn.CurJob;
            OmniWorkProxyUtility.SetActive(pawn, true);
            return WorkSearchStepResult.Found;
        }

        private void MoveProxyToStation(Pawn pawn, Building_OmniWorkstation station)
        {
            IntVec3 cell = station.Position.Standable(map)
                ? station.Position
                : CellFinder.StandableCellNear(station.Position, map, 5f);
            if (pawn.Position == cell) return;
            pawn.Position = cell;
            pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
        }

        /// <summary>通过原版三种 WorkGiver 入口搜索，不识别具体 Job 或 Mod。</summary>
        private Job TryFindJobInGroup(Pawn pawn, Building_OmniWorkstation station, OmniWorkGiverGroup group)
        {
            for (int giverIndex = 0; giverIndex < group.giverDefs.Count; giverIndex++)
            {
                WorkGiverDef giverDef = group.giverDefs[giverIndex];
                lastSearchGiver = giverDef;
                if (IsBlacklistedTag(giverDef.tagToGive)) continue;

                try
                {
                    WorkGiver giver = giverDef.Worker;
                    if (!CanUseWorkGiver(pawn, giver)) continue;
                    Job nonScanJob = giver.NonScanJob(pawn);
                    if (nonScanJob != null)
                    {
                        nonScanJob.workGiverDef = giver.def;
                        if (IsSafeJob(nonScanJob, pawn) && JobHasAnchorInRange(nonScanJob, station))
                            return nonScanJob;
                        JobMaker.ReturnToPool(nonScanJob);
                    }

                    if (!(giver is WorkGiver_Scanner scanner)) continue;
                    if (giver.def.scanThings)
                    {
                        Thing target = FindThingForScanner(pawn, station, scanner);
                        if (target != null)
                        {
                            Job job = scanner.JobOnThing(pawn, target);
                            if (PrepareScannedJob(job, giver.def, pawn)) return job;
                        }
                    }

                    if (giver.def.scanCells)
                    {
                        IntVec3 cell = FindCellForScanner(pawn, station, scanner);
                        if (cell.IsValid)
                        {
                            Job job = scanner.JobOnCell(pawn, cell);
                            if (PrepareScannedJob(job, giver.def, pawn)) return job;
                        }
                    }
                }
                catch (Exception error)
                {
                    int key = Gen.HashCombineInt(giverDef.shortHash, station.thingIDNumber);
                    Log.ErrorOnce("[OmniWorkstation] WorkGiver '" + giverDef.defName +
                                  "' failed while scanning: " + error, key);
                }
            }
            return null;
        }

        private static bool PrepareScannedJob(Job job, WorkGiverDef giverDef, Pawn pawn)
        {
            if (job == null) return false;
            job.workGiverDef = giverDef;
            if (IsSafeJob(job, pawn)) return true;
            JobMaker.ReturnToPool(job);
            return false;
        }

        private Thing FindThingForScanner(Pawn pawn, Building_OmniWorkstation station,
            WorkGiver_Scanner scanner)
        {
            searchPawn = pawn;
            searchStation = station;
            searchScanner = scanner;
            IEnumerable<Thing> customSet = scanner.PotentialWorkThingsGlobal(pawn);
            IEnumerable<Thing> searchSet = customSet ??
                (IEnumerable<Thing>)map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
            Func<Thing, float> priority = scanner.Prioritized ? thingPriorityGetter : null;
            float searchDistance = station.WorkRadius + 5f;

            if (scanner.AllowUnreachable)
                return GenClosest.ClosestThing_Global(pawn.Position, searchSet, searchDistance,
                    thingSearchValidator, priority);

            if (scanner.Prioritized || customSet != null)
                return GenClosest.ClosestThing_Global_Reachable(pawn.Position, map, searchSet,
                    scanner.PathEndMode, TraverseParms.For(pawn, scanner.MaxPathDanger(pawn)),
                    searchDistance, thingSearchValidator, priority);

            return GenClosest.ClosestThingReachable(pawn.Position, map, scanner.PotentialWorkThingRequest,
                scanner.PathEndMode, TraverseParms.For(pawn, scanner.MaxPathDanger(pawn)),
                searchDistance, thingSearchValidator, searchRegionsMax: scanner.MaxRegionsToScanBeforeGlobalSearch);
        }

        private bool ValidateThingCandidate(Thing thing)
        {
            return thing != null && thing != searchPawn && thing.Spawned && thing.Map == map &&
                   searchStation.Covers(thing.Position) && !thing.IsForbidden(searchPawn) &&
                   searchScanner.HasJobOnThing(searchPawn, thing);
        }

        private float GetThingPriority(Thing thing)
        {
            return searchScanner.GetPriority(searchPawn, thing);
        }

        private IntVec3 FindCellForScanner(Pawn pawn, Building_OmniWorkstation station,
            WorkGiver_Scanner scanner)
        {
            IEnumerable<IntVec3> cells = scanner.PotentialWorkCellsGlobal(pawn);
            if (cells == null) return IntVec3.Invalid;

            IntVec3 bestCell = IntVec3.Invalid;
            float bestDistance = float.MaxValue;
            float bestPriority = float.MinValue;
            Danger maxDanger = scanner.MaxPathDanger(pawn);
            IList<IntVec3> list = cells as IList<IntVec3>;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                    ConsiderCellCandidate(pawn, station, scanner, maxDanger, list[i], ref bestCell,
                        ref bestDistance, ref bestPriority);
            }
            else
            {
                foreach (IntVec3 cell in cells)
                    ConsiderCellCandidate(pawn, station, scanner, maxDanger, cell, ref bestCell,
                        ref bestDistance, ref bestPriority);
            }
            return bestCell;
        }

        private static void ConsiderCellCandidate(Pawn pawn, Building_OmniWorkstation station,
            WorkGiver_Scanner scanner, Danger maxDanger, IntVec3 cell, ref IntVec3 bestCell,
            ref float bestDistance, ref float bestPriority)
        {
            if (!cell.IsValid || !station.Covers(cell) || cell.IsForbidden(pawn)) return;
            float distance = (cell - pawn.Position).LengthHorizontalSquared;
            if (!scanner.Prioritized && distance >= bestDistance) return;
            if (!scanner.HasJobOnCell(pawn, cell)) return;
            if (!scanner.AllowUnreachable &&
                !pawn.CanReach(cell, scanner.PathEndMode, maxDanger)) return;

            float priority = scanner.Prioritized ? scanner.GetPriority(pawn, cell) : 0f;
            if (scanner.Prioritized &&
                (priority < bestPriority || Mathf.Approximately(priority, bestPriority) && distance >= bestDistance))
                return;

            bestCell = cell;
            bestDistance = distance;
            bestPriority = priority;
        }

        private static bool CanUseWorkGiver(Pawn pawn, WorkGiver giver)
        {
            WorkGiverDef def = giver.def;
            if (!(def.nonColonistsCanDo || pawn.IsColonist || pawn.IsColonyMech || pawn.IsColonySubhuman))
                return false;
            if (giver.ShouldSkip(pawn) || giver.MissingRequiredCapacity(pawn) != null)
                return false;
            return !pawn.RaceProps.IsMechanoid || def.canBeDoneByMechs;
        }

        private static readonly HashSet<string> BlacklistedJobDefs = new HashSet<string>(StringComparer.Ordinal)
        {
            "Wait", "Wait_MaintainPosture", "Goto", "LayDown", "Ingest", "SocialRelax",
            "Lovin", "Meditate", "Flee", "ExitMapBest", "JoinCaravan"
        };

        private static bool IsBlacklistedTag(JobTag tag)
        {
            return tag == JobTag.Idle || tag == JobTag.InMentalState || tag == JobTag.SatisfyingNeeds ||
                   tag == JobTag.DraftedOrder || tag == JobTag.TuckedIntoBed ||
                   tag == JobTag.RestingForMedicalReasons || tag == JobTag.ChangingApparel ||
                   tag == JobTag.Escaping || tag == JobTag.JoiningCaravan;
        }

        private static bool IsSafeJob(Job job, Pawn pawn)
        {
            if (job?.def == null || BlacklistedJobDefs.Contains(job.def.defName)) return false;
            if (TargetIsPawn(job.GetTarget(TargetIndex.A), pawn) ||
                TargetIsPawn(job.GetTarget(TargetIndex.B), pawn) ||
                TargetIsPawn(job.GetTarget(TargetIndex.C), pawn)) return false;

            List<LocalTargetInfo> queue = job.GetTargetQueue(TargetIndex.A);
            if (QueueTargetsPawn(queue, pawn)) return false;
            queue = job.GetTargetQueue(TargetIndex.B);
            return !QueueTargetsPawn(queue, pawn);
        }

        private static bool QueueTargetsPawn(List<LocalTargetInfo> queue, Pawn pawn)
        {
            if (queue == null) return false;
            for (int i = 0; i < queue.Count; i++)
                if (TargetIsPawn(queue[i], pawn)) return true;
            return false;
        }

        private static bool TargetIsPawn(LocalTargetInfo target, Pawn pawn)
        {
            return target.IsValid && target.HasThing && target.Thing == pawn;
        }

        private static bool JobHasAnchorInRange(Job job, Building_OmniWorkstation station)
        {
            if (TargetIsInRange(job.GetTarget(TargetIndex.A), station) ||
                TargetIsInRange(job.GetTarget(TargetIndex.B), station) ||
                TargetIsInRange(job.GetTarget(TargetIndex.C), station)) return true;

            List<LocalTargetInfo> queue = job.GetTargetQueue(TargetIndex.A);
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (TargetIsInRange(queue[i], station)) return true;

            queue = job.GetTargetQueue(TargetIndex.B);
            if (queue != null)
                for (int i = 0; i < queue.Count; i++)
                    if (TargetIsInRange(queue[i], station)) return true;

            return false;
        }

        private static bool TargetIsInRange(LocalTargetInfo target, Building_OmniWorkstation station)
        {
            if (!target.IsValid) return false;
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                return thing != null && thing.Spawned && thing.Map == station.Map && station.Covers(thing.Position);
            }
            return target.Cell.IsValid && station.Covers(target.Cell);
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

    /// <summary>背景、特质或第三方基因均不得禁用通用代理的工作。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.WorkTypeIsDisabled))]
    public static class Patch_OmniWorkProxy_EnableEveryWorkType
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.WorkTagIsDisabled))]
    public static class Patch_OmniWorkProxy_EnableEveryWorkTag
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;
            __result = false;
            return false;
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

    /// <summary>任务链真正结束时冻结；若原版启动 finalizer，则继续保持代理活跃。</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_DeactivateOnJobEnd
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn ___pawn)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return;
            bool wasActive = OmniWorkProxyUtility.IsActive(___pawn);
            Job current = ___pawn.CurJob;
            if (current != null && current.def != JobDefOf.Wait && current.def != JobDefOf.Wait_MaintainPosture)
            {
                OmniWorkProxyUtility.SetActive(___pawn, true);
                return;
            }

            OmniWorkProxyUtility.SetActive(___pawn, false);
            if (wasActive && ___pawn.Spawned)
                ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>().NotifyProxyBecameIdle(___pawn);
        }
    }

    /// <summary>无论由原版还是第三方发起，代理都不能成为社交互动的任一方。</summary>
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Patch_OmniWorkProxy_BlockSocialInteraction
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn, Pawn recipient, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn) && !OmniWorkProxyUtility.IsProxy(recipient))
                return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 代理只允许由全局工作泵显式派工。原版会在 Job 结束或当前 Job 为空时同步调用
    /// TryFindAndStartJob；若不拦截，代理可在同一 Tick 内通过普通思考树连续启动大量 Job。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "TryFindAndStartJob")]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_BlockAutonomousJobSearch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn)
        {
            return !OmniWorkProxyUtility.IsProxy(___pawn);
        }
    }

    /// <summary>
    /// 机会任务会把工作泵派发的原 Job 放入队列，再依靠普通思考树恢复。代理禁用了自主思考，
    /// 因此直接跳过机会任务，避免合法工作被永久留在队列中；显式 finalizer 不受影响。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryOpportunisticJob))]
    public static class Patch_OmniWorkProxy_DisableOpportunisticJobs
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn, ref Job __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            __result = null;
            return false;
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
