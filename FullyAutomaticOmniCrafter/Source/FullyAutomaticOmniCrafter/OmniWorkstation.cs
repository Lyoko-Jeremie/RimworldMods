using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站只保存玩家配置；地图组件管理代理生命周期，具体工作由原版思考树搜索。
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
        [Unsaved] private int workFilterVersion;
        [Unsaved] private HashSet<string> disabledWorkTypeSet;
        private static readonly List<IntVec3> RingDrawCells = new List<IntVec3>();
        private static readonly HashSet<IntVec3> InnerBorderCells = new HashSet<IntVec3>();

        public bool AutomationEnabled => automationEnabled;
        public int WorkRadius => workRadius;
        public bool AllowUnclassifiedWork => allowUnclassifiedWork;
        public int WorkFilterVersion => workFilterVersion;

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

            workFilterVersion++;
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
            workFilterVersion++;
            Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
        }

        public void CopyWorkFilterFrom(Building_OmniWorkstation source)
        {
            if (source == null || source == this) return;
            source.EnsureWorkTypeSet();
            disabledWorkTypeDefNames = new List<string>(source.disabledWorkTypeSet);
            disabledWorkTypeSet = new HashSet<string>(source.disabledWorkTypeSet, StringComparer.Ordinal);
            allowUnclassifiedWork = source.allowUnclassifiedWork;
            workFilterVersion++;
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

            yield return new Command_Toggle
            {
                defaultLabel = "OmniWorkstation_MonitorToggle".Translate(),
                defaultDesc = "OmniWorkstation_MonitorToggleDesc".Translate(),
                icon = TexButton.Search,
                isActive = () => OmniWorkstationMonitor.Visible,
                toggleAction = () => OmniWorkstationMonitor.SetVisible(!OmniWorkstationMonitor.Visible)
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
                DrawWorkRadiusRing(Position, WorkRadius, automationEnabled ? Color.cyan : Color.gray);
        }

        private static void DrawWorkRadiusRing(IntVec3 center, float radius, Color color)
        {
            RingDrawCells.Clear();
            InnerBorderCells.Clear();
            int extent = Mathf.CeilToInt(radius);
            float radiusSquared = radius * radius;
            float innerRadiusSquared = (radius - 1f) * (radius - 1f);
            for (int x = -extent; x <= extent; x++)
            {
                float xSquared = x * x;
                if (xSquared > radiusSquared) continue;

                float maxZ = Mathf.Sqrt(radiusSquared - xSquared);
                float minZ = xSquared < innerRadiusSquared
                    ? Mathf.Sqrt(innerRadiusSquared - xSquared)
                    : 0f;
                int zStart = Mathf.CeilToInt(minZ);
                int zEnd = Mathf.FloorToInt(maxZ);

                for (int z = zStart; z <= zEnd; z++)
                {
                    RingDrawCells.Add(center + new IntVec3(x, 0, z));
                    if (z != 0)
                        RingDrawCells.Add(center + new IntVec3(x, 0, -z));
                }
            }

            // DrawFieldEdges 会描出格子集合的所有边界。环带内侧也属于边界，
            // 因此把紧邻环带的内部空格列为忽略边界，只保留范围的外边缘。
            for (int i = 0; i < RingDrawCells.Count; i++)
            {
                IntVec3 cell = RingDrawCells[i];
                AddInnerBorderCell(center, cell + IntVec3.North, innerRadiusSquared);
                AddInnerBorderCell(center, cell + IntVec3.East, innerRadiusSquared);
                AddInnerBorderCell(center, cell + IntVec3.South, innerRadiusSquared);
                AddInnerBorderCell(center, cell + IntVec3.West, innerRadiusSquared);
            }

            if (RingDrawCells.Count > 0)
                GenDraw.DrawFieldEdges(RingDrawCells, color, null, InnerBorderCells);
        }

        private static void AddInnerBorderCell(IntVec3 center, IntVec3 cell, float innerRadiusSquared)
        {
            int x = cell.x - center.x;
            int z = cell.z - center.z;
            if (x * x + z * z < innerRadiusSquared)
                InnerBorderCells.Add(cell);
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

        public override Vector2 InitialSize => new Vector2(440f, 250f);

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
            // 成本护栏提示:估算当前半径的覆盖格数,提醒玩家半径过大会放大逐次搜索成本。
            listing.Label("OmniWorkstation_RadiusCoverageTip".Translate(
                Mathf.RoundToInt(Mathf.PI * radius * radius)));

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

        public override Vector2 InitialSize => new Vector2(440f, 250f);

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
            // 负载提示:空闲代理休眠不产生持续开销,真正成本来自同时执行的原版 Job。
            listing.Label("OmniWorkstation_ProxyLimitTip".Translate());

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

    /// <summary>工作类型目录只在 Def 加载后构建一次，供工作站筛选界面和代理配置同步使用。</summary>
    [StaticConstructorOnStartup]
    public static class OmniWorkCatalog
    {
        public static readonly List<WorkTypeDef> WorkTypes = new List<WorkTypeDef>();
        public static readonly List<WorkGiverDef> UnclassifiedWorkGivers = new List<WorkGiverDef>();

        static OmniWorkCatalog()
        {
            List<WorkTypeDef> allTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < allTypes.Count; i++)
            {
                WorkTypeDef workType = allTypes[i];
                if (workType.workGiversByPriority.Count > 0) WorkTypes.Add(workType);
            }
            WorkTypes.Sort(CompareWorkTypes);

            List<WorkGiverDef> allGivers = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < allGivers.Count; i++)
                if (allGivers[i].workType == null) UnclassifiedWorkGivers.Add(allGivers[i]);
            UnclassifiedWorkGivers.Sort((a, b) => b.priorityInType.CompareTo(a.priorityInType));
        }

        private static int CompareWorkTypes(WorkTypeDef a, WorkTypeDef b)
        {
            int priority = b.naturalPriority.CompareTo(a.naturalPriority);
            return priority != 0
                ? priority
                : string.Compare(a.defName, b.defName, StringComparison.Ordinal);
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

        public override Vector2 InitialSize => new Vector2(760f, 700f);

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
            Widgets.Label(new Rect(0f, 0f, inRect.width - 288f, 32f), "OmniWorkstation_StatusTitle".Translate());
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(inRect.width - 276f, 4f, 132f, 24f),
                "OmniWorkstation_StatsLog".Translate()))
                Log.Message(manager.BuildStatsLogText());
            if (Widgets.ButtonText(new Rect(inRect.width - 136f, 4f, 132f, 24f),
                "OmniWorkstation_StatsReset".Translate()))
                manager.ResetStats();

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

            // ─── 性能探针统计区(诊断用;ResetStats 清零后观察)────────────────
            // 数字一律在 C# 侧格式化为字符串,翻译 key 只使用纯 {N} 占位符,
            // 避免翻译管线不识别 {N:F1} 这类复合格式说明符。
            MapComponent_OmniWorkstation.OmniWorkstationStats stats = manager.GetStatsSnapshot();
            double dispatchRate = stats.spanTicks > 0
                ? stats.foundJobCount / (stats.spanTicks / 60.0)
                : 0.0;
            float statsY = 168f;
            const float statLineHeight = 20f;
            Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                "OmniWorkstation_StatsSpanDispatch".Translate(stats.spanTicks, stats.foundJobCount,
                    stats.exhaustedStepCount, FormatF1(dispatchRate)));
            statsY += statLineHeight;
            Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                "OmniWorkstation_StatsPump".Translate(stats.stepCount, FormatF2(stats.stepAvgMs),
                    FormatF2(stats.stepMaxMs)));
            statsY += statLineHeight;
            Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                "OmniWorkstation_StatsMaintain".Translate(stats.maintainCount,
                    FormatF2(stats.maintainAvgMs), FormatF2(stats.maintainMaxMs)));
            statsY += statLineHeight;
            Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                "OmniWorkstation_StatsProxyFlow".Translate(stats.wakeProxyCount, stats.sleepProxyCount,
                    stats.idleScanCount));
            statsY += statLineHeight + 10f;

            float listTop = statsY;
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

        private static string FormatF1(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string FormatF2(double value)
        {
            return value.ToString("F2", CultureInfo.InvariantCulture);
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
            // 已销毁代理立即视为非活跃;残留记录由 EnsureProxyCount 与 TryGetStation 惰性清理。
            return pawn != null && !pawn.Destroyed && ActiveProxies.Contains(pawn);
        }

        public static bool TryGetStation(Pawn pawn, out Building_OmniWorkstation station)
        {
            if (pawn == null)
            {
                station = null;
                return false;
            }
            if (Assignments.TryGetValue(pawn, out station))
            {
                if (station != null && station.Spawned && station.Map == pawn.Map)
                    return true;
                // 自愈:记录对应的代理或工作站已失效时顺手清理,
                // 避免已销毁 Pawn 被静态容器长期强引用。
                Unassign(pawn);
            }
            station = null;
            return false;
        }

        /// <summary>
        /// 清除代理的生活状态和第三方附加状态。此方法只在创建、读档、出舱以及
        /// 每个工作会话结束入舱时执行;不做周期性全员清洗,避免无谓的线性开销。
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

            // 上述背景、特质和健康状态会参与原版禁用工作缓存；直接修改后必须显式失效，
            // 否则 SkillRecord.TotallyDisabled 可能继续返回代理生成阶段留下的旧结果。
            pawn.Notify_DisabledWorkTypesChanged();

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
    /// 每张地图唯一的万能工作站调度器。每站至多保留一个待决探路唤醒，
    /// 真正运行中的原版 Job 数由共享代理池上限约束。
    /// </summary>
    public sealed class MapComponent_OmniWorkstation : MapComponent
    {
        public const int MinConfigurableProxyCount = 1;
        public const int MaxConfigurableProxyCount = 1024;
        private const int DefaultProxyCount = 8;
        private const int MaxProxyCreatesPerAssignment = 8;
        private const int MaxProxyRemovalsPerAssignment = 16;
        private const int AssignmentInterval = 60;
        private const int EmptySearchBackoffBase = 60;
        private const int EmptySearchBackoffMax = 120;
        // 原版没有提供“不校验禁用状态但更新工作表”的接口。缓存字段访问器，只在工作站
        // 或筛选版本变化时写入，避免在热路径反射，也避免 SetPriority 的第三方禁用校验。
        private static readonly AccessTools.FieldRef<Pawn_WorkSettings, DefMap<WorkTypeDef, int>>
            workPrioritiesRef = AccessTools.FieldRefAccess<Pawn_WorkSettings,
                DefMap<WorkTypeDef, int>>("priorities");

        private readonly List<Building_OmniWorkstation> stations =
            new List<Building_OmniWorkstation>();
        private readonly Dictionary<Building_OmniWorkstation, StationRuntime> stationStates =
            new Dictionary<Building_OmniWorkstation, StationRuntime>();
        private readonly List<ProxyRecord> proxies = new List<ProxyRecord>();
        private ProxySleepHolder sleepHolder;
        private ThingOwner<Pawn> sleepingProxies;
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

        // ─── 性能探针(仅诊断,不参与调度逻辑)────────────────────────────────
        private int statStartTick;
        private long stepCount;
        private long stepNsTotal;
        private long stepNsMax;
        private long maintainCount;
        private long maintainNsTotal;
        private long maintainNsMax;
        private long foundJobCount;
        private long exhaustedStepCount;
        private long wakeProxyCount;
        private long sleepProxyCount;
        private long idleScanCount;

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
            public bool needsSanitize;
            public Job trackedStartedJob;
            public int trackedStartedJobTick = -1;
            // 代理保留上次投影来源；再次租给同一配置版本的工作站时无需重建原版 WorkGiver 缓存。
            public Building_OmniWorkstation configuredStation;
            public int configuredWorkFilterVersion = -1;
        }

        /// <summary>
        /// 不接入 Map.GetChildHolders 的私有休眠舱。代理仍由 MapComponent 深保存，
        /// 但不会被 MapPawns.AllPawnsUnspawned 当成殖民者加入头像栏和工作列表。
        /// </summary>
        private sealed class ProxySleepHolder : IThingHolder
        {
            public ThingOwner<Pawn> contents;

            public IThingHolder ParentHolder => null;

            public ThingOwner GetDirectlyHeldThings()
            {
                return contents;
            }

            public void GetChildHolders(List<IThingHolder> outChildren)
            {
                if (contents != null)
                    ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, contents);
            }
        }

        private sealed class StationRuntime
        {
            public Building_OmniWorkstation station;
            public int nextSearchTick;
            public int consecutiveFailures;
            public bool probePending;
        }

        public MapComponent_OmniWorkstation(Map map) : base(map)
        {
            sleepHolder = new ProxySleepHolder();
            sleepingProxies = new ThingOwner<Pawn>(sleepHolder, false, LookMode.Deep);
            sleepHolder.contents = sleepingProxies;
            sleepingProxies.dontTickContents = true;
            statStartTick = CurrentTick;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (sleepHolder == null) sleepHolder = new ProxySleepHolder();
            Scribe_Deep.Look(ref sleepingProxies, "omniWorkstationSleepingProxies", sleepHolder);
            if (sleepingProxies == null)
                sleepingProxies = new ThingOwner<Pawn>(sleepHolder, false, LookMode.Deep);
            sleepHolder.contents = sleepingProxies;
            sleepingProxies.dontTickContents = true;
            Scribe_Values.Look(ref configuredProxyCount, "omniWorkstationProxyCount", DefaultProxyCount);
            configuredProxyCount = Mathf.Clamp(configuredProxyCount,
                MinConfigurableProxyCount, MaxConfigurableProxyCount);
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
            ResetStationSearchState(GetOrCreateStationRuntime(station), CurrentTick);
            WakePumpNow();
        }

        public void NotifyWorkFilterChanged(Building_OmniWorkstation station, WorkTypeDef workType, bool enabled)
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.station != station) continue;
                if (!enabled)
                {
                    // 原版 JobGiver_Work 不会替 NonScanJob 回填 workGiverDef；这种少数任务
                    // 无法准确还原所属类型，配置变更时保守中止，避免继续执行已禁用工作。
                    WorkGiverDef giverDef = record.issuedJob?.workGiverDef;
                    if (giverDef == null || giverDef.workType == workType)
                    {
                        StopIssuedJob(record);
                        record.station = null;
                        OmniWorkProxyUtility.Unassign(record.pawn);
                        continue;
                    }
                }
                // 被动工作链会在当前 Job 结束后自行选取下一项，必须立即刷新其缓存。
                ApplyStationWorkSettings(record, station);
            }

            StationRuntime filterRuntime = GetOrCreateStationRuntime(station);
            ResetStationSearchState(filterRuntime, CurrentTick);
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

        /// <summary>重置工作站的搜索进度；工作搜索顺序完全交由原版 Pawn_WorkSettings 决定。</summary>
        private static void ResetStationSearchState(StationRuntime runtime, int tick)
        {
            runtime.nextSearchTick = tick;
            runtime.consecutiveFailures = 0;
        }

        private void WakePumpNow()
        {
            nextPumpTick = CurrentTick;
            searchState = OmniWorkSearchState.Queued;
        }

        private void WakeAllStations()
        {
            int tick = CurrentTick;
            for (int i = 0; i < stations.Count; i++)
                ResetStationSearchState(GetOrCreateStationRuntime(stations[i]), tick);
            WakePumpNow();
        }

        public void NotifyProxyBecameIdle(Pawn pawn = null, JobCondition condition = JobCondition.Succeeded)
        {
            Building_OmniWorkstation idleStation = null;
            if (pawn != null)
            {
                for (int i = 0; i < proxies.Count; i++)
                {
                    ProxyRecord record = proxies[i];
                    if (record.pawn != pawn) continue;
                    // 清理前先取会话归属，用于下面的热续复位。
                    idleStation = record.station;
                    record.issuedJob = null;
                    record.trackedStartedJob = null;
                    record.trackedStartedJobTick = -1;
                    record.station = null;
                    OmniWorkProxyUtility.Unassign(pawn);
                    break;
                }

                // 原版在进入这里前已经尝试寻找下一个 Job；当前仍为空闲即表示本次探路穷尽。
                // 使用短退避等待非 Designation 工作变化，避免无工作时逐 Tick 重试。
                if (idleStation != null && idleStation.Spawned && idleStation.Map == map)
                {
                    StationRuntime runtime = GetOrCreateStationRuntime(idleStation);
                    runtime.probePending = false;
                    runtime.consecutiveFailures++;
                    runtime.nextSearchTick = CurrentTick +
                        ExhaustedBackoffTicks(runtime.consecutiveFailures);
                    exhaustedStepCount++;
                    searchState = OmniWorkSearchState.Backoff;
                }
            }
            WakePumpNow();
        }

        /// <summary>
        /// 原版思考树成功启动真实 Job 后更新对象池记录，并允许下一 Tick 渐进唤醒一个代理。
        /// 每次成功只扩一个，避免多个代理同时扫描大型全局候选集。
        /// </summary>
        public void NotifyProxyStartedJob(Pawn pawn, Job job)
        {
            if (pawn == null || job == null || IsIdleJob(job)) return;
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.pawn != pawn || record.station == null) continue;

                if (record.trackedStartedJob == job && record.trackedStartedJobTick == job.startTick)
                {
                    OmniWorkProxyUtility.SetActive(pawn, true);
                    return;
                }
                record.trackedStartedJob = job;
                record.trackedStartedJobTick = job.startTick;
                record.issuedJob = job;
                record.needsSanitize = true;
                OmniWorkProxyUtility.SetActive(pawn, true);
                StationRuntime runtime = GetOrCreateStationRuntime(record.station);
                runtime.probePending = false;
                runtime.consecutiveFailures = 0;
                runtime.nextSearchTick = CurrentTick + 1;
                lastSearchTick = CurrentTick;
                lastSearchWorker = pawn.Name?.ToStringShort ?? "-";
                lastSearchWork = SafeJobReport(pawn, job);
                lastSearchStation = record.station;
                lastSearchGiver = job.workGiverDef;
                lastSearchWorkType = job.workGiverDef?.workType;
                lastSearchPriority = job.workGiverDef?.priorityInType ?? 0;
                lastSearchGroupValid = job.workGiverDef != null;
                foundJobCount++;
                WakePumpNow();
                return;
            }
        }

        /// <summary>玩家新增范围内 Designation 时立即打断空闲退避。</summary>
        public void NotifyDesignationAdded(IntVec3 cell)
        {
            int tick = CurrentTick;
            bool changed = false;
            for (int i = 0; i < stations.Count; i++)
            {
                Building_OmniWorkstation station = stations[i];
                if (station == null || !station.Operational || !station.Covers(cell)) continue;
                StationRuntime runtime = GetOrCreateStationRuntime(station);
                if (runtime.probePending) continue;
                if (runtime.nextSearchTick <= tick) continue;
                ResetStationSearchState(runtime, tick);
                changed = true;
            }
            if (changed) WakePumpNow();
        }

        private StationRuntime GetOrCreateStationRuntime(Building_OmniWorkstation station)
        {
            if (!stationStates.TryGetValue(station, out StationRuntime runtime))
            {
                runtime = new StationRuntime
                {
                    station = station,
                    nextSearchTick = CurrentTick
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

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            OmniWorkstationMonitor.Draw(this);
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (!proxiesRecovered) RecoverExistingThings();

            int tick = Find.TickManager.TicksGame;
            if (tick % AssignmentInterval == map.uniqueID % AssignmentInterval)
            {
                long maintStart = Stopwatch.GetTimestamp();
                RemoveInvalidStations();
                EnsureProxyCount();
                for (int i = 0; i < proxies.Count; i++)
                    RefreshProxyState(proxies[i], false);
                int scheduledTick = ComputeNextPumpTick(tick);
                if (scheduledTick < nextPumpTick) nextPumpTick = scheduledTick;
                RecordMaintainSample(Stopwatch.GetTimestamp() - maintStart);
            }

            if (tick < nextPumpTick) return;
            // 混合被动泵只负责租用并唤醒代理。具体 Job 由代理下一次原版思考自行选择；
            // 每个站同一时刻至多挂起一个探路唤醒，成功找到工作后才逐步扩容。
            long stepStart = Stopwatch.GetTimestamp();
            try
            {
                if (!TryGetNextDueStation(tick, out StationRuntime stationRuntime, out int earliestTick))
                {
                    ReclaimIdleProxies();
                    nextPumpTick = earliestTick;
                    searchState = OmniWorkSearchState.Waiting;
                    return;
                }

                searchState = OmniWorkSearchState.Searching;
                lastSearchTick = tick;
                lastSearchWork = "-";
                lastSearchStation = stationRuntime.station;
                lastSearchWorkType = null;
                lastSearchGiver = null;
                lastSearchGroupValid = false;

                if (!TryGetNextIdleProxy(out ProxyRecord record, deferSleep: true))
                {
                    stationRuntime.nextSearchTick = int.MaxValue;
                    nextPumpTick = int.MaxValue;
                    searchState = OmniWorkSearchState.NoIdleProxy;
                    return;
                }

                lastSearchWorker = record.pawn?.Name?.ToStringShort ?? "-";
                if (WakeProxyForVanillaSearch(record, stationRuntime.station))
                {
                    // 等待占位 Job 结束后，Pawn_JobTracker 会在同一原版调用链中决定实际工作。
                    // 结果返回前不再为该站唤醒第二个代理，防止大型候选集发生并发扫描风暴。
                    stationRuntime.nextSearchTick = int.MaxValue;
                    stationRuntime.probePending = true;
                    searchState = OmniWorkSearchState.Continuing;
                }
                else
                {
                    stationRuntime.probePending = false;
                    PutProxyToSleep(record);
                    stationRuntime.consecutiveFailures++;
                    stationRuntime.nextSearchTick = tick +
                        ExhaustedBackoffTicks(stationRuntime.consecutiveFailures);
                    searchState = OmniWorkSearchState.Backoff;
                    exhaustedStepCount++;
                }

                nextPumpTick = tick + 1;
            }
            finally
            {
                RecordStepSample(Stopwatch.GetTimestamp() - stepStart);
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

        // ─── 性能探针快照与采样(供状态窗口诊断)───────────────────────────────
        public struct OmniWorkstationStats
        {
            public int spanTicks;
            public long stepCount;
            public double stepAvgMs;
            public double stepMaxMs;
            public long foundJobCount;
            public long exhaustedStepCount;
            public long maintainCount;
            public double maintainAvgMs;
            public double maintainMaxMs;
            public long wakeProxyCount;
            public long sleepProxyCount;
            public long idleScanCount;
        }

        public void ResetStats()
        {
            statStartTick = CurrentTick;
            stepCount = 0;
            stepNsTotal = 0;
            stepNsMax = 0;
            maintainCount = 0;
            maintainNsTotal = 0;
            maintainNsMax = 0;
            foundJobCount = 0;
            exhaustedStepCount = 0;
            wakeProxyCount = 0;
            sleepProxyCount = 0;
            idleScanCount = 0;
        }

        public OmniWorkstationStats GetStatsSnapshot()
        {
            return new OmniWorkstationStats
            {
                spanTicks = Mathf.Max(0, CurrentTick - statStartTick),
                stepCount = stepCount,
                stepAvgMs = stepCount > 0 ? stepNsTotal / (double)stepCount / 1_000_000.0 : 0.0,
                stepMaxMs = stepNsMax / 1_000_000.0,
                foundJobCount = foundJobCount,
                exhaustedStepCount = exhaustedStepCount,
                maintainCount = maintainCount,
                maintainAvgMs = maintainCount > 0 ? maintainNsTotal / (double)maintainCount / 1_000_000.0 : 0.0,
                maintainMaxMs = maintainNsMax / 1_000_000.0,
                wakeProxyCount = wakeProxyCount,
                sleepProxyCount = sleepProxyCount,
                idleScanCount = idleScanCount
            };
        }

        private void RecordStepSample(long ns)
        {
            stepCount++;
            stepNsTotal += ns;
            if (ns > stepNsMax) stepNsMax = ns;
        }

        /// <summary>生成一份可直接粘贴的多行统计文本,供状态窗口的"输出日志"按钮使用。</summary>
        public string BuildStatsLogText()
        {
            OmniWorkstationStats s = GetStatsSnapshot();
            double dispatchRate = s.spanTicks > 0 ? s.foundJobCount / (s.spanTicks / 60.0) : 0.0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[OmniWorkstation] proxy stats (tick=" + CurrentTick + ")");
            sb.AppendLine("searchState=" + searchState
                + " nextPumpTick=" + (nextPumpTick == int.MaxValue ? "sleep" : nextPumpTick.ToString())
                + " stations=" + stations.Count
                + " proxies=" + proxies.Count
                + " sleeping=" + (sleepingProxies == null ? 0 : sleepingProxies.Count)
                + " active=" + ActiveProxyCount
                + " configured=" + configuredProxyCount);
            sb.AppendLine("spanTicks=" + s.spanTicks
                + " stepCount=" + s.stepCount
                + " stepAvgMs=" + s.stepAvgMs.ToString("F3", CultureInfo.InvariantCulture)
                + " stepMaxMs=" + s.stepMaxMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("foundJobs=" + s.foundJobCount
                + " exhaustedSteps=" + s.exhaustedStepCount
                + " dispatchPerSec=" + dispatchRate.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("maintainCount=" + s.maintainCount
                + " maintainAvgMs=" + s.maintainAvgMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maintainMaxMs=" + s.maintainMaxMs.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("wakeProxy=" + s.wakeProxyCount
                + " sleepProxy=" + s.sleepProxyCount
                + " idleScan=" + s.idleScanCount);
            return sb.ToString();
        }

        private void RecordMaintainSample(long ns)
        {
            maintainCount++;
            maintainNsTotal += ns;
            if (ns > maintainNsMax) maintainNsMax = ns;
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
                PrepareProxy(pawn);
                ProxyRecord record = new ProxyRecord { pawn = pawn };
                proxies.Add(record);
                PutProxyToSleep(record);
            }
            // 读档恢复后按槽位制整体重排：旧存档中从 1 起算的编号会被纠正为 0..N-1。
            RenumberProxies();
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
            int removedInvalid = 0;
            for (int i = proxies.Count - 1; i >= 0; i--)
            {
                Pawn pawn = proxies[i].pawn;
                if (pawn != null && !pawn.Destroyed &&
                    ((pawn.Spawned && pawn.Map == map) || sleepingProxies.Contains(pawn))) continue;
                OmniWorkProxyUtility.Unassign(pawn);
                proxies.RemoveAt(i);
                removedInvalid++;
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

            // 代理池组成发生变化后按槽位制重排编号，保证名字中的编号始终从 0 连续。
            if (removedInvalid + created + removed > 0) RenumberProxies();
        }

        private Pawn CreateProxy()
        {
            Building_OmniWorkstation station = FirstOperationalStation();
            if (station == null || OmniWorkstationDefOf.FAOC_OmniWorkProxy == null) return null;

            try
            {
                Pawn pawn = PawnGenerator.GeneratePawn(OmniWorkstationDefOf.FAOC_OmniWorkProxy, Faction.OfPlayer);
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

            // 工作中的代理仍是合法 Pawn，但不进入殖民者、警报和普通 AI 使用的 MapPawns 列表。
            if (pawn.Spawned && pawn.Map == map)
                map.mapPawns.DeRegisterPawn(pawn);
        }

        /// <summary>
        /// 槽位制重排：代理名字中的编号恒等于它在代理池 proxies 中的下标，因此任何时刻
        /// 编号都从 0 开始且连续(0..N-1，N=当前在册代理数)。新建、回收或读档恢复后调用。
        /// 名字已是正确编号的代理直接跳过，不产生额外字符串分配。
        /// </summary>
        private void RenumberProxies()
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                Pawn pawn = proxies[i].pawn;
                if (pawn == null || pawn.Destroyed) continue;
                string expected = "OmniWorkstation_WorkerName".Translate(i);
                NameSingle current = pawn.Name as NameSingle;
                if (current == null || !current.Numerical || current.Name != expected)
                    pawn.Name = new NameSingle(expected, true);
            }
        }

        private Building_OmniWorkstation FirstOperationalStation()
        {
            for (int i = 0; i < stations.Count; i++)
                if (stations[i].Operational) return stations[i];
            return null;
        }

        private bool RefreshProxyState(ProxyRecord record, bool deferSleep)
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

            // Job 会被原版对象池复用，不能只靠引用变化判断任务是否已经结束：旧的
            // issuedJob 可能在结束后立刻被复用成 Wait，并再次成为 pawn.CurJob。
            // 无论引用是否相同，只要当前已无 Job 或进入原版等待 Job，就必须释放代理，
            // 否则 active 会永久占满代理池并让工作泵停在 NoIdleProxy 深睡状态。
            if (record.issuedJob != null && (pawn.CurJob == null || IsIdleJob(pawn.CurJob)))
            {
                record.issuedJob = null;
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

            if (deferSleep)
            {
                // 热续：泵步马上就要为此空闲代理派发新 Job，留在场上等待，
                // 跳过"入舱→出舱"往返与多余的 Sanitize 开销。
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
                return true;
            }
            PutProxyToSleep(record);
            return sleepingProxies.Contains(pawn);
        }

        internal static bool IsIdleJob(Job job)
        {
            return job?.def == null || BlacklistedJobDefs.Contains(job.def.defName);
        }

        /// <summary>按休眠舱的方式反生成并深保存代理；容器本身从不 Tick 内容物。</summary>
        private void PutProxyToSleep(ProxyRecord record)
        {
            Pawn pawn = record?.pawn;
            if (pawn == null || pawn.Destroyed) return;

            // 工作会话结束:入舱前统一恢复干净状态,取代原"每 250 tick 全员清洗"的周期任务。
            if (record.needsSanitize)
            {
                record.needsSanitize = false;
                OmniWorkProxyUtility.Sanitize(pawn);
            }

            // 原版无工作时通常会启动 Wait/GotoWander；入舱前明确结束，避免保存一个冻结 Job。
            if (pawn.CurJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            OmniWorkProxyUtility.SetActive(pawn, false);
            if (pawn.mindState != null) pawn.mindState.Active = false;
            OmniWorkProxyUtility.Unassign(pawn);
            record.issuedJob = null;
            record.trackedStartedJob = null;
            record.trackedStartedJobTick = -1;
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
            else
                sleepProxyCount++;
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
                wakeProxyCount++;
            }
            else
            {
                MoveProxyToStation(pawn, station);
            }
            return true;
        }

        private bool TryGetNextIdleProxy(out ProxyRecord result, bool deferSleep)
        {
            result = null;
            int count = proxies.Count;
            for (int checkedCount = 0; checkedCount < count; checkedCount++)
            {
                if (proxySearchCursor >= count) proxySearchCursor = 0;
                ProxyRecord candidate = proxies[proxySearchCursor++];
                idleScanCount++;
                if (!RefreshProxyState(candidate, deferSleep)) continue;
                result = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 把场上"空闲且非活跃"的代理统一送睡。无到期站的泵步与派发收尾时调用，
        /// 取代原先每 Tick 的顶部收容循环，避免空闲代理滞留在场上。
        /// </summary>
        private void ReclaimIdleProxies()
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.issuedJob == null && record.pawn != null && record.pawn.Spawned &&
                    !OmniWorkProxyUtility.IsActive(record.pawn))
                    PutProxyToSleep(record);
            }
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

        /// <summary>空闲探路采用 60→120 tick 的短退避；Designation 变化会立即唤醒。</summary>
        private static int ExhaustedBackoffTicks(int consecutiveFailures)
        {
            int exponent = Mathf.Min(consecutiveFailures - 1, 1);
            return Mathf.Min(EmptySearchBackoffBase << exponent, EmptySearchBackoffMax);
        }

        /// <summary>
        /// 唤醒一个代理并交给原版 Pawn_JobTracker。1 tick 的等待 Job 结束时，原版
        /// EndCurrentJob 会自然进入 TryFindAndStartJob，不在地图组件内主动搜索具体工作。
        /// </summary>
        private bool WakeProxyForVanillaSearch(ProxyRecord record, Building_OmniWorkstation station)
        {
            Pawn pawn = record.pawn;
            if (pawn == null || pawn.Destroyed || station == null || !station.Operational)
                return false;

            if (!WakeProxyAtStation(pawn, station))
                return false;
            record.station = station;
            OmniWorkProxyUtility.Assign(pawn, station);

            // 热续代理未经过入舱清洗,派发前补一次净化,保证新 Job 开始前状态干净。
            if (record.needsSanitize)
            {
                record.needsSanitize = false;
                OmniWorkProxyUtility.Sanitize(pawn);
            }

            ApplyStationWorkSettings(record, station);
            pawn.mindState.Active = true;
            OmniWorkProxyUtility.SetActive(pawn, true);
            Job probe = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 1);
            record.issuedJob = probe;
            pawn.jobs.StartJob(probe, JobCondition.InterruptForced, cancelBusyStances: false,
                addToJobsThisTick: false);
            if (pawn.CurJob == null)
            {
                record.issuedJob = null;
                OmniWorkProxyUtility.SetActive(pawn, false);
                return false;
            }

            record.issuedJob = pawn.CurJob;
            record.needsSanitize = true;
            return true;
        }

        /// <summary>
        /// 将工作站筛选直接投影到代理的原版优先级表。绕过 SetPriority 的禁用校验，
        /// 但仍让原版与第三方 WorkGiver 读取同一份 Pawn_WorkSettings 数据。
        /// </summary>
        private static void ApplyStationWorkSettings(ProxyRecord record, Building_OmniWorkstation station)
        {
            Pawn pawn = record.pawn;
            Pawn_WorkSettings settings = pawn.workSettings;
            if (settings == null) return;
            settings.EnableAndInitializeIfNotAlreadyInitialized();
            if (record.configuredStation == station &&
                record.configuredWorkFilterVersion == station.WorkFilterVersion) return;

            DefMap<WorkTypeDef, int> priorities = workPrioritiesRef(settings);
            priorities.SetAll(0);
            List<WorkTypeDef> workTypes = OmniWorkCatalog.WorkTypes;
            for (int i = 0; i < workTypes.Count; i++)
            {
                WorkTypeDef workType = workTypes[i];
                if (station.AllowsWorkType(workType)) priorities[workType] = 1;
            }
            settings.Notify_UseWorkPrioritiesChanged();
            record.configuredStation = station;
            record.configuredWorkFilterVersion = station.WorkFilterVersion;
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

        private static readonly HashSet<string> BlacklistedJobDefs = new HashSet<string>(StringComparer.Ordinal)
        {
            "Wait", "Wait_MaintainPosture", "Goto", "LayDown", "Ingest", "SocialRelax",
            "Lovin", "Meditate", "Flee", "ExitMapBest", "JoinCaravan"
        };

        private static void StopIssuedJob(ProxyRecord record)
        {
            OmniWorkProxyUtility.SetActive(record.pawn, false);
            if (record.pawn?.mindState != null) record.pawn.mindState.Active = false;
            if (record.pawn != null && record.pawn.CurJob != null)
                record.pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            record.issuedJob = null;
        }
    }

    /// <summary>
    /// 代理保存 999 级技能，但对外尊重当前游戏环境实际允许的技能上限。
    /// 原版或第三方 Mod 先计算出的结果即为当前有效上限，再由此处限制为至多 999。
    /// </summary>
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.GetLevel))]
    public static class Patch_OmniWorkProxy_SkillLevel
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(SkillRecord __instance, ref int __result)
        {
            if (OmniWorkProxyUtility.IsProxy(__instance.Pawn))
                __result = Mathf.Min(999, __result);
        }
    }

    /// <summary>
    /// SkillRecord 的禁用判定直接读取 Pawn 的底层禁用标签和工作类型，不会经过
    /// WorkTypeIsDisabled/WorkTagIsDisabled；代理必须在这一层同步放行所有技能。
    /// </summary>
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.TotallyDisabled), MethodType.Getter)]
    public static class Patch_OmniWorkProxy_EnableEverySkill
    {
        [HarmonyPrefix]
        public static bool Prefix(SkillRecord __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance.Pawn)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>永久禁用查询也必须与通用代理允许所有工作保持一致。</summary>
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.PermanentlyDisabled), MethodType.Getter)]
    public static class Patch_OmniWorkProxy_EnableEveryPermanentSkill
    {
        [HarmonyPrefix]
        public static bool Prefix(SkillRecord __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance.Pawn)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 代理只在绑定工作站的工作会话内放行该站选择的工作类型。工作优先级由真实
    /// Pawn_WorkSettings 控制，能力、ShouldSkip 与 WorkGiver 自身约束仍由原版处理。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.WorkTypeIsDisabled))]
    public static class Patch_OmniWorkProxy_EnableAssignedWorkType
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, WorkTypeDef w, ref bool __result)
        {
            if (w == null || !OmniWorkProxyUtility.TryGetStation(__instance,
                    out Building_OmniWorkstation station) || !station.AllowsWorkType(w))
                return true;
            __result = false;
            return false;
        }
    }

    /// <summary>绑定期间放行工作标签；具体工作类型已由工作站优先级表限定。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.WorkTagIsDisabled))]
    public static class Patch_OmniWorkProxy_EnableAssignedWorkTag
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, ref bool __result)
        {
            if (!OmniWorkProxyUtility.TryGetStation(__instance, out _)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 原版工作缓存不会收录没有 WorkTypeDef 的 WorkGiver。被动模式在缓存完成后把它们
    /// 追加为最低回退项，使实际选择和启动仍完整经过原版 JobGiver_Work。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_WorkSettings), "CacheWorkGiversInOrder")]
    public static class Patch_OmniWorkProxy_AppendUnclassifiedWorkGivers
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn ___pawn, List<WorkGiver> ___workGiversInOrderEmerg,
            List<WorkGiver> ___workGiversInOrderNormal)
        {
            if (!OmniWorkProxyUtility.TryGetStation(___pawn,
                    out Building_OmniWorkstation station) || !station.AllowUnclassifiedWork) return;

            List<WorkGiverDef> defs = OmniWorkCatalog.UnclassifiedWorkGivers;
            for (int i = 0; i < defs.Count; i++)
            {
                WorkGiverDef def = defs[i];
                try
                {
                    WorkGiver giver = def.Worker;
                    List<WorkGiver> target = def.emergency
                        ? ___workGiversInOrderEmerg
                        : ___workGiversInOrderNormal;
                    if (giver != null && !target.Contains(giver)) target.Add(giver);
                }
                catch (Exception error)
                {
                    Log.ErrorOnce("[OmniWorkstation] Failed to cache unclassified WorkGiver '" +
                                  def.defName + "': " + error, Gen.HashCombineInt(def.shortHash, 719031));
                }
            }
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

    /// <summary>
    /// 原版 EndCurrentJob 会同步寻找下一项工作。真实 Job 继续留场并更新记录；
    /// 只有原版最终落入等待状态时，才通知工作泵回收代理。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_DeactivateOnJobEnd
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn ___pawn, JobCondition condition)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return;
            bool wasActive = OmniWorkProxyUtility.IsActive(___pawn);
            Job current = ___pawn.CurJob;
            if (!MapComponent_OmniWorkstation.IsIdleJob(current))
            {
                OmniWorkProxyUtility.SetActive(___pawn, true);
                if (___pawn.Spawned)
                    ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>()
                        .NotifyProxyStartedJob(___pawn, current);
                return;
            }

            OmniWorkProxyUtility.SetActive(___pawn, false);
            if (wasActive && ___pawn.Spawned)
                ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>()
                    .NotifyProxyBecameIdle(___pawn, condition);
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
    /// 捕获原版或第三方在任意路径启动的真实 Job。与 EndCurrentJob 补丁配合且幂等，
    /// 可覆盖机会任务、finalizer 及其他 Mod 直接调用 StartJob 的情况。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_OmniWorkProxy_TrackVanillaJobStart
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn ___pawn)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn) || !___pawn.Spawned ||
                MapComponent_OmniWorkstation.IsIdleJob(___pawn.CurJob)) return;
            ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>()
                .NotifyProxyStartedJob(___pawn, ___pawn.CurJob);
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

    /// <summary>让使用 Region 级允许区判断的原版与 Mod 搜索器也遵守工作站覆盖范围。</summary>
    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbiddenEntirely))]
    public static class Patch_OmniWorkProxy_AllowedRegion
    {
        [HarmonyPrefix]
        public static bool Prefix(Region r, Pawn pawn, ref bool __result)
        {
            if (!OmniWorkProxyUtility.TryGetStation(pawn, out Building_OmniWorkstation station))
                return true;
            foreach (IntVec3 cell in r.Cells)
            {
                if (!station.Covers(cell)) continue;
                __result = false;
                return false;
            }
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// 全局候选搜索原本会在 allowed-area validator 之前逐个执行 Reachability。
    /// 代理从工作站中心出发，先把最大距离收紧到覆盖半径，可在寻路前排除范围外目标。
    /// </summary>
    [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThing_Global_Reachable))]
    public static class Patch_OmniWorkProxy_ClampGlobalReachableDistance
    {
        [HarmonyPrefix]
        public static void Prefix(TraverseParms traverseParams, ref float maxDistance)
        {
            if (OmniWorkProxyUtility.TryGetStation(traverseParams.pawn,
                    out Building_OmniWorkstation station))
                maxDistance = Mathf.Min(maxDistance, station.WorkRadius);
        }
    }

    /// <summary>原版非优先级 Thing 搜索启用整区剪枝，避免扫描完全位于覆盖范围外的 Region。</summary>
    [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable))]
    public static class Patch_OmniWorkProxy_EnableAllowedRegionPruning
    {
        [HarmonyPrefix]
        public static void Prefix(TraverseParms traverseParams, ref bool ignoreEntirelyForbiddenRegions)
        {
            if (OmniWorkProxyUtility.IsProxy(traverseParams.pawn))
                ignoreEntirelyForbiddenRegions = true;
        }
    }

    /// <summary>新增范围内 Designation 时立即唤醒探路代理，避免等待周期退避。</summary>
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
    public static class Patch_OmniWorkstation_WakeOnDesignation
    {
        [HarmonyPostfix]
        public static void Postfix(DesignationManager __instance, Designation newDes)
        {
            if (__instance?.map == null || newDes == null) return;
            IntVec3 cell = newDes.target.HasThing
                ? newDes.target.Thing.Position
                : newDes.target.Cell;
            if (cell.IsValid)
                __instance.map.GetComponent<MapComponent_OmniWorkstation>()
                    .NotifyDesignationAdded(cell);
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
