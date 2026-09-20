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
using Verse.AI.Group;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能工作站只保存玩家配置；地图组件管理代理生命周期，具体工作由原版思考树搜索。
    /// </summary>
    public sealed class Building_OmniWorkstation : Building
    {
        public const int MinWorkRadius = 1;
        public const int MaxWorkRadius = 256;

        /// <summary>
        /// 默认启用的工作类型：灭火、医生、基本、烹饪、酿酒、狩猎、建造、种植、采矿、割除、
        /// 锻造、缝制、制作、搬运、清洁、研究。原版没有独立的“酿酒”工作类型，部分 Mod
        /// （如 RimCuisine 2 的 RC2_Brewing）会自行新增，因此除固定 defName 外还对
        /// 名称包含 Brewing 的类型做宽松匹配，避免把默认值绑死在某个 Mod 上。
        /// </summary>
        private static readonly HashSet<string> DefaultEnabledWorkTypeDefNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Firefighter", "Doctor", "BasicWorker", "Cooking", "Hunting", "Construction",
                "Growing", "Mining", "PlantCutting", "Smithing", "Tailoring", "Crafting",
                "Hauling", "Cleaning", "Research", "Brewing", "RC2_Brewing"
            };

        private bool automationEnabled = true;
        private int workRadius = 30;
        private int legacyWorkRadiusIndex = 1;
        private List<string> disabledWorkTypeDefNames = new List<string>();
        private bool allowUnclassifiedWork = true;
        // 旧存档没有该字段，载入后需要把默认过滤器补齐，否则老工作站仍会保持“全部启用”。
        private bool workFilterInitialized;
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
                // 工作站**不需要供电**：Def 里 basePowerConsumption 为 0。保留 CompPowerTrader 只为
                // 维持"电网节点"能力（transmitsPower），所以这里刻意不检查 PowerOn —— 未接电网、
                // 电网断电、或根本没有电力组件时，工作站都视为可用。CompFlickable 保留给玩家手动停用。
                if (!Spawned || !automationEnabled || this.IsBurning() || this.IsBrokenDown()) return false;
                CompFlickable flickable = GetComp<CompFlickable>();
                return flickable == null || flickable.SwitchIsOn;
            }
        }

        public bool Covers(IntVec3 cell)
        {
            return Spawned && cell.InBounds(Map) && Position.InHorDistOf(cell, WorkRadius);
        }

        /// <summary>
        /// 跨图范围判定：**同坐标投影**——以工作站坐标为圆心，用同一坐标系在任意图上求半径。
        /// 工作站在本图时行为与原 Covers 完全一致；跨图时以目标图自身的边界为准
        /// （多层楼层的图与地面图同尺寸同坐标系，因此投影天然成立）。
        /// 入口授权的"覆盖判定"也复用此方法。
        /// </summary>
        public bool CoversOn(Map pawnMap, IntVec3 cell)
        {
            if (!Spawned || pawnMap == null) return false;
            if (!cell.InBounds(pawnMap)) return false;
            return Position.InHorDistOf(cell, WorkRadius);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // 新放置的工作站立刻套用默认过滤器；读档走 ExposeData 的 PostLoadInit 分支。
            if (!respawningAfterLoad && !workFilterInitialized) ApplyDefaultWorkFilter();
            map.GetComponent<MapComponent_OmniWorkstation>().Register(this);
            // 工作站集合变化会影响"哪些入口被覆盖"，立即失效入口授权缓存。
            OmniWorkProxyEntryAuthorization.Invalidate(map);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            if (map != null)
                map.GetComponent<MapComponent_OmniWorkstation>().Deregister(this);
            base.DeSpawn(mode);
            if (map != null) OmniWorkProxyEntryAuthorization.Invalidate(map);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref automationEnabled, "automationEnabled", true);
            Scribe_Values.Look(ref workRadius, "workRadius", -1);
            Scribe_Values.Look(ref legacyWorkRadiusIndex, "workRadiusIndex", 1);
            Scribe_Collections.Look(ref disabledWorkTypeDefNames, "disabledWorkTypeDefNames", LookMode.Value);
            Scribe_Values.Look(ref allowUnclassifiedWork, "allowUnclassifiedWork", true);
            Scribe_Values.Look(ref workFilterInitialized, "workFilterInitialized", false);
            if (disabledWorkTypeDefNames == null) disabledWorkTypeDefNames = new List<string>();
            disabledWorkTypeSet = null;
            if (workRadius < MinWorkRadius)
            {
                int[] legacyRadii = { 15, 30, 60, 100 };
                workRadius = legacyRadii[Mathf.Clamp(legacyWorkRadiusIndex, 0, legacyRadii.Length - 1)];
            }
            workRadius = Mathf.Clamp(workRadius, MinWorkRadius, MaxWorkRadius);
            // 旧存档里的工作站没有初始化标记，读档后补上默认过滤器。
            if (Scribe.mode == LoadSaveMode.PostLoadInit && !workFilterInitialized)
                ApplyDefaultWorkFilter();
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

            workFilterInitialized = true;
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
            workFilterInitialized = true;
            SyncDisabledWorkTypeList();
            workFilterVersion++;
            Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
        }

        /// <summary>该工作类型是否属于默认启用集合。</summary>
        public static bool IsDefaultEnabledWorkType(WorkTypeDef workType)
        {
            if (workType == null) return false;
            string defName = workType.defName;
            if (defName.NullOrEmpty()) return false;
            if (DefaultEnabledWorkTypeDefNames.Contains(defName)) return true;
            return defName.IndexOf("Brewing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>把过滤器恢复为默认集合：只有默认启用集合中的工作类型可用，其余全部关闭。</summary>
        public void ApplyDefaultWorkFilter()
        {
            EnsureWorkTypeSet();
            disabledWorkTypeSet.Clear();
            List<WorkTypeDef> workTypes = OmniWorkCatalog.WorkTypes;
            for (int i = 0; i < workTypes.Count; i++)
            {
                WorkTypeDef workType = workTypes[i];
                if (!IsDefaultEnabledWorkType(workType)) disabledWorkTypeSet.Add(workType.defName);
            }
            allowUnclassifiedWork = true;
            workFilterInitialized = true;
            SyncDisabledWorkTypeList();
            workFilterVersion++;
        }

        /// <summary>恢复默认过滤器并通知地图组件重建工作站的搜索状态。</summary>
        public void ResetWorkFilterToDefault()
        {
            ApplyDefaultWorkFilter();
            Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
        }

        /// <summary>导出当前过滤器，供剪贴板跨工作站复制。</summary>
        public OmniWorkFilterSnapshot ExportWorkFilter()
        {
            EnsureWorkTypeSet();
            return new OmniWorkFilterSnapshot(new List<string>(disabledWorkTypeDefNames), allowUnclassifiedWork);
        }

        /// <summary>用剪贴板内容整批替换当前过滤器，并通知地图组件刷新该工作站。</summary>
        public bool ImportWorkFilter(OmniWorkFilterSnapshot snapshot)
        {
            if (snapshot == null) return false;
            EnsureWorkTypeSet();
            disabledWorkTypeSet.Clear();
            List<string> defNames = snapshot.DisabledWorkTypeDefNames;
            for (int i = 0; i < defNames.Count; i++)
            {
                string defName = defNames[i];
                if (!defName.NullOrEmpty()) disabledWorkTypeSet.Add(defName);
            }
            allowUnclassifiedWork = snapshot.AllowUnclassifiedWork;
            workFilterInitialized = true;
            SyncDisabledWorkTypeList();
            workFilterVersion++;
            Map?.GetComponent<MapComponent_OmniWorkstation>().NotifyConfigurationChanged(this);
            return true;
        }

        public void CopyWorkFilterFrom(Building_OmniWorkstation source)
        {
            if (source == null || source == this) return;
            source.EnsureWorkTypeSet();
            disabledWorkTypeDefNames = new List<string>(source.disabledWorkTypeSet);
            disabledWorkTypeSet = new HashSet<string>(source.disabledWorkTypeSet, StringComparer.Ordinal);
            allowUnclassifiedWork = source.allowUnclassifiedWork;
            workFilterInitialized = true;
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

                yield return new Command_Action
                {
                    defaultLabel = "OmniWorkstation_RecreateProxies".Translate(),
                    defaultDesc = "OmniWorkstation_RecreateProxiesDesc".Translate(),
                    icon = TexButton.Reload,
                    // Gizmo 的 action 运行在 OnGUI 阶段，而销毁与生成 Pawn 都会改动地图上的集合，
                    // 因此这里只登记意图，实际动作交给下一 tick 执行。
                    action = () => manager.RequestRecreateAllProxies()
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

    /// <summary>工作站工作类型过滤器的不可变快照；供跨工作站复制粘贴使用。</summary>
    public sealed class OmniWorkFilterSnapshot
    {
        public readonly List<string> DisabledWorkTypeDefNames;
        public readonly bool AllowUnclassifiedWork;

        public OmniWorkFilterSnapshot(List<string> disabledWorkTypeDefNames, bool allowUnclassifiedWork)
        {
            DisabledWorkTypeDefNames = disabledWorkTypeDefNames ?? new List<string>();
            AllowUnclassifiedWork = allowUnclassifiedWork;
        }
    }

    /// <summary>工作类型过滤器的临时剪贴板，让玩家把一台工作站的配置粘贴到其他工作站；不参与存档。</summary>
    public static class OmniWorkFilterClipboard
    {
        private static OmniWorkFilterSnapshot snapshot;

        public static bool HasData => snapshot != null;

        public static void CopyFrom(Building_OmniWorkstation station)
        {
            if (station == null || station.Destroyed) return;
            snapshot = station.ExportWorkFilter();
        }

        public static bool TryPasteTo(Building_OmniWorkstation station)
        {
            if (snapshot == null || station == null || station.Destroyed) return false;
            return station.ImportWorkFilter(snapshot);
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
        // 行高与复选框尺寸（24）一致、行距略大 1 像素，让列表比原先 32/34 更紧凑。
        private const float RowHeight = 24f;
        private const float RowSpacing = 25f;

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

            // 按钮分两行排布：第一行是整站开关，第二行是跨工作站复制/粘贴。
            float gap = 6f;
            float buttonWidth = (inRect.width - gap * 2f) / 3f;
            float buttonHeight = 30f;
            float firstRowY = 124f;
            float secondRowY = firstRowY + buttonHeight + 4f;

            if (Widgets.ButtonText(new Rect(0f, firstRowY, buttonWidth, buttonHeight), "OmniWorkstation_EnableAll".Translate()))
                station.SetAllWorkTypesEnabled(true);
            if (Widgets.ButtonText(new Rect(buttonWidth + gap, firstRowY, buttonWidth, buttonHeight), "OmniWorkstation_DisableAll".Translate()))
                station.SetAllWorkTypesEnabled(false);

            Rect resetRect = new Rect((buttonWidth + gap) * 2f, firstRowY, buttonWidth, buttonHeight);
            if (Widgets.ButtonText(resetRect, "OmniWorkstation_ResetFilter".Translate()))
                station.ResetWorkFilterToDefault();
            TooltipHandler.TipRegion(resetRect, "OmniWorkstation_ResetFilterDesc".Translate());

            Rect copyRect = new Rect(0f, secondRowY, buttonWidth, buttonHeight);
            if (Widgets.ButtonText(copyRect, "OmniWorkstation_CopyFilter".Translate()))
            {
                OmniWorkFilterClipboard.CopyFrom(station);
                Messages.Message("OmniWorkstation_FilterCopied".Translate(), MessageTypeDefOf.SilentInput, false);
            }
            TooltipHandler.TipRegion(copyRect, "OmniWorkstation_CopyFilterDesc".Translate());

            Rect pasteRect = new Rect(buttonWidth + gap, secondRowY, buttonWidth, buttonHeight);
            // 剪贴板为空时把「粘贴」按钮画成灰色，提示当前没有可粘贴的配置。
            Color previousColor = GUI.color;
            if (!OmniWorkFilterClipboard.HasData) GUI.color = Color.gray;
            if (Widgets.ButtonText(pasteRect, "OmniWorkstation_PasteFilter".Translate()))
            {
                if (OmniWorkFilterClipboard.TryPasteTo(station))
                    Messages.Message("OmniWorkstation_FilterPasted".Translate(), MessageTypeDefOf.SilentInput, false);
                else
                    Messages.Message("OmniWorkstation_FilterClipboardEmpty".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            GUI.color = previousColor;
            TooltipHandler.TipRegion(pasteRect, "OmniWorkstation_PasteFilterDesc".Translate());

            Rect applyAllRect = new Rect((buttonWidth + gap) * 2f, secondRowY, buttonWidth, buttonHeight);
            if (Widgets.ButtonText(applyAllRect, "OmniWorkstation_ApplyAllStations".Translate()))
                station.Map?.GetComponent<MapComponent_OmniWorkstation>().ApplyWorkFilterToAll(station);
            TooltipHandler.TipRegion(applyAllRect, "OmniWorkstation_ApplyAllStationsDesc".Translate());

            float listTop = secondRowY + buttonHeight + 10f;
            Rect outRect = new Rect(0f, listTop, inRect.width, inRect.height - (listTop + 44f));
            int visibleCount = CountVisibleRows();
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(outRect.height, visibleCount * RowSpacing));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            float y = 0f;
            DrawUnclassifiedRow(viewRect.width, ref y);
            List<WorkTypeDef> workTypes = OmniWorkCatalog.WorkTypes;
            for (int i = 0; i < workTypes.Count; i++)
            {
                WorkTypeDef workType = workTypes[i];
                if (!MatchesSearch(workType)) continue;
                Rect row = new Rect(0f, y, viewRect.width, RowHeight);
                if ((Mathf.RoundToInt(y / RowSpacing) & 1) == 1) Widgets.DrawLightHighlight(row);
                bool enabled = station.AllowsWorkType(workType);
                string label = OmniWorkCatalog.WorkTypeLabel(workType);
                Widgets.CheckboxLabeled(row, label, ref enabled);
                TooltipHandler.TipRegion(row, workType.defName + GetModSuffix(workType));
                if (enabled != station.AllowsWorkType(workType))
                    station.SetWorkTypeEnabled(workType, enabled);
                y += RowSpacing;
            }
            Widgets.EndScrollView();
        }

        private void DrawUnclassifiedRow(float width, ref float y)
        {
            if (!searchText.NullOrEmpty() &&
                !"OmniWorkstation_UnclassifiedWork".Translate().ToString()
                    .ToLowerInvariant().Contains(searchText.ToLowerInvariant())) return;

            Rect row = new Rect(0f, y, width, RowHeight);
            bool enabled = station.AllowUnclassifiedWork;
            Widgets.CheckboxLabeled(row, "OmniWorkstation_UnclassifiedWork".Translate(), ref enabled);
            TooltipHandler.TipRegion(row, "OmniWorkstation_UnclassifiedWorkDesc".Translate());
            if (enabled != station.AllowUnclassifiedWork)
                station.SetWorkTypeEnabled(null, enabled);
            y += RowSpacing;
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
        /// <summary>对应代理；逐代理操作（停止并回收 / 重建此代理）需要它。</summary>
        public Pawn pawn;

        public OmniWorkProxyStatus(string name, string work, Pawn pawn)
        {
            this.name = name;
            this.work = work;
            this.pawn = pawn;
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
            if (Widgets.ButtonText(new Rect(inRect.width - 408f, 4f, 132f, 24f),
                "OmniWorkstation_DiagnoseRegionMiss".Translate()))
                manager.ToggleDiagnoseRegionMiss();

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

            // ─── 跨图管理（R-7 的最小管理视图）──────────────────────────────
            // 驻外 = 归属本图池、但当前在别的图上工作的代理；无第三方换图 Mod 时恒为 0。
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            int abroadCount = 0;
            if (registry != null && manager.OwningMap != null)
            {
                List<Pawn> abroadList = registry.Scratch;
                registry.EnumerateForeign(manager.OwningMap, abroadList);
                abroadCount = abroadList.Count;
            }
            Widgets.Label(new Rect(0f, 168f, inRect.width, 24f),
                "OmniWorkstation_AbroadCount".Translate(abroadCount));
            if (registry != null)
            {
                string toggleKey = registry.GlobalWorkEnabled
                    ? "OmniWorkstation_GlobalWorkOn"
                    : "OmniWorkstation_GlobalWorkOff";
                if (Widgets.ButtonText(new Rect(inRect.width - 540f, 4f, 132f, 24f),
                        toggleKey.Translate()))
                {
                    registry.SetGlobalWorkEnabled(!registry.GlobalWorkEnabled);
                    Messages.Message("OmniWorkstation_GlobalWorkDesc".Translate(),
                        MessageTypeDefOf.TaskCompletion, false);
                }
                if (Widgets.ButtonText(new Rect(inRect.width - 540f, 32f, 132f, 24f),
                        "OmniWorkstation_VerifyMirrors".Translate()))
                    registry.VerifyAllMirrors();
            }

            // ─── 性能探针统计区(诊断用;ResetStats 清零后观察)────────────────
            // 数字一律在 C# 侧格式化为字符串,翻译 key 只使用纯 {N} 占位符,
            // 避免翻译管线不识别 {N:F1} 这类复合格式说明符。
            MapComponent_OmniWorkstation.OmniWorkstationStats stats = manager.GetStatsSnapshot();
            double dispatchRate = stats.spanTicks > 0
                ? stats.foundJobCount / (stats.spanTicks / 60.0)
                : 0.0;
            float statsY = 196f;
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
            statsY += statLineHeight;
            // 诊断区:区域遍历漏活探测(默认关闭)。probes 为"搜索无结果"次数,hits 为其中
            // "用代理网格重查能命中"的次数 —— hits/probes 即漏活比例。
            Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                "OmniWorkstation_StatsRegionMiss".Translate(manager.RegionMissProbeCount,
                    manager.RegionMissHitCount,
                    (manager.DiagnoseRegionMiss
                        ? "OmniWorkstation_DiagnoseRegionMissOn"
                        : "OmniWorkstation_DiagnoseRegionMissOff").Translate()));
            statsY += statLineHeight;

            // ─── 跨图准入（R-10 可视化）：可去地图与未被覆盖的入口 ─────────────
            if (registry != null && manager.OwningMap != null)
            {
                OmniWorkProxyEntryAuthorization.GetReachableMaps(manager.OwningMap, ReachableScratch);
                ReachableNameScratch.Clear();
                for (int i = 0; i < ReachableScratch.Count; i++)
                {
                    Map reachable = ReachableScratch[i];
                    ReachableNameScratch.Add(reachable.Parent?.LabelCap ?? ("#" + reachable.uniqueID));
                }
                string reachableText = ReachableNameScratch.Count == 0
                    ? "OmniWorkstation_ReachableMapsNone".Translate().ToString()
                    : string.Join("、", ReachableNameScratch);
                Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                    "OmniWorkstation_ReachableMaps".Translate(reachableText));
                statsY += statLineHeight;
                Widgets.Label(new Rect(0f, statsY, inRect.width, statLineHeight),
                    "OmniWorkstation_UncoveredPortals".Translate(
                        OmniWorkProxyEntryAuthorization.UncoveredPortals(manager.OwningMap)));
                statsY += statLineHeight;
            }
            statsY += 10f;

            // 池修复（R-9）：只登记请求，真正的销毁/生成交给下一 tick。
            if (registry != null)
            {
                if (Widgets.ButtonText(new Rect(inRect.width - 270f, 32f, 132f, 24f),
                        "OmniWorkstation_RepairPool".Translate()))
                    manager.RequestRepairPool();
            }

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
                float actionsX = viewRect.width - 230f;
                Widgets.Label(new Rect(160f, row.y + 3f, actionsX - 166f, 24f), activeRows[i].work);

                // 逐代理操作（R-8）：只登记请求 —— OnGUI 阶段改动地图上的 Pawn 集合会破坏
                // 原版迭代，因此实际销毁/生成一律交给下一 tick 执行。
                Pawn rowPawn = activeRows[i].pawn;
                if (rowPawn == null || rowPawn.Destroyed || registry == null) continue;
                if (Widgets.ButtonText(new Rect(actionsX, row.y + 2f, 110f, 26f),
                        "OmniWorkstation_ProxyReclaim".Translate()))
                    manager.RequestProxyReclaim(rowPawn);
                if (Widgets.ButtonText(new Rect(actionsX + 115f, row.y + 2f, 110f, 26f),
                        "OmniWorkstation_ProxyRecreate".Translate()))
                    manager.RequestProxyRecreate(rowPawn);
            }
            Widgets.EndScrollView();
        }

        // 复用列表：窗口每 30 帧重绘一次，避免反复分配（AGENTS.md 性能要求）。
        private static readonly List<Map> ReachableScratch = new List<Map>();
        private static readonly List<string> ReachableNameScratch = new List<string>();

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
        public static HediffDef FAOC_OmniWorkProxyHome;
        public static BackstoryDef FAOC_OmniWorkProxyChildhood;
        public static BackstoryDef FAOC_OmniWorkProxyAdulthood;
        public static PathGridDef FAOC_OmniWorkProxyPathGrid;

        static OmniWorkstationDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OmniWorkstationDefOf));
        }
    }

    /// <summary>
    /// 代理专用的寻路网格：全图通行成本恒为 10，无视地形（深水/浅水/山体/真空）、建筑、
    /// 天气与火焰成本。配合 Def 上的 fencePassable/flying，使代理可以直接穿过并停留在任意
    /// 格子。OmniWorkProxyNavigation 统一解释终点、接触与路径，不使用普通区域连通性。
    /// </summary>
    public class OmniWorkProxyPathGrid : PathGrid
    {
        public OmniWorkProxyPathGrid(Map map, PathGridDef def) : base(map, def)
        {
        }

        public override int CalculatedCostAt(IntVec3 c, bool perceivedStatic, IntVec3 prevCell,
            int? baseCostOverride = null)
        {
            return 10;
        }
    }

    /// <summary>
    /// 工作站专用的原版系统区域。它与建造屋顶区等区域一样由 AreaManager 保存，
    /// 但不可编辑、不可作为玩家活动区选择，因此不会占用 Area_Allowed 的数量上限。
    /// </summary>
    public sealed class Area_OmniWorkstation : Area
    {
        private int stationThingId = -1;

        public int StationThingId => stationThingId;
        public override string Label => "OmniWorkstation_InternalArea".Translate(stationThingId);
        public override Color Color => new Color(0.25f, 0.8f, 1f, 0.35f);
        public override int ListPriority => -10000;

        public Area_OmniWorkstation()
        {
        }

        public Area_OmniWorkstation(AreaManager areaManager, int stationThingId)
            : base(areaManager)
        {
            this.stationThingId = stationThingId;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref stationThingId, "stationThingId", -1);
        }

        public override string GetUniqueLoadID()
        {
            return "Area_" + ID + "_OmniWorkstation_" + stationThingId;
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
        private static readonly HashSet<Pawn> ManagedTransitions = new HashSet<Pawn>();

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
            ClearAreaRestriction(pawn);
        }

        public static void BeginManagedTransition(Pawn pawn)
        {
            if (pawn != null) ManagedTransitions.Add(pawn);
        }

        public static void EndManagedTransition(Pawn pawn)
        {
            if (pawn != null) ManagedTransitions.Remove(pawn);
        }

        public static bool IsManagedTransition(Pawn pawn)
        {
            return pawn != null && ManagedTransitions.Contains(pawn);
        }

        // allowedAreas 是 Pawn_PlayerSettings 的私有字段（Dictionary<Map, Area>）：跨图工作的代理
        // 可能在多张图上留下 Area_OmniWorkStation，回收时必须整表清理；只清"当前图"会留下残留，
        // 并且会一直持有 pocket map 的强引用。
        private static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Dictionary<Map, Area>> AllowedAreasRef =
            AccessTools.FieldRefAccess<Pawn_PlayerSettings, Dictionary<Map, Area>>("allowedAreas");
        private static readonly List<Map> AreaCleanupScratch = new List<Map>();

        public static void ClearAreaRestriction(Pawn pawn)
        {
            Pawn_PlayerSettings settings = pawn?.playerSettings;
            if (settings == null) return;

            Dictionary<Map, Area> allowedAreas;
            try
            {
                allowedAreas = AllowedAreasRef(settings);
            }
            catch (Exception)
            {
                // 原版字段改名时退化为只清当前图，保证不抛异常。
                if (settings.AreaRestrictionInPawnCurrentMap is Area_OmniWorkstation)
                    settings.AreaRestrictionInPawnCurrentMap = null;
                return;
            }
            if (allowedAreas == null || allowedAreas.Count == 0) return;

            // 先收集再删除：不能边遍历 Dictionary 边改集合。
            AreaCleanupScratch.Clear();
            foreach (KeyValuePair<Map, Area> pair in allowedAreas)
                if (pair.Value is Area_OmniWorkstation) AreaCleanupScratch.Add(pair.Key);
            for (int i = 0; i < AreaCleanupScratch.Count; i++)
                allowedAreas.Remove(AreaCleanupScratch[i]);
            AreaCleanupScratch.Clear();
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

            // 代理不穿任何服装、不持任何武器：生成、出舱、读档与入舱前都先剥离一次，
            // 保证第三方在生成或读档阶段补上的服装与装备不会跟随代理留在场上。
            ReleaseWornGear(pawn);

            // 检测层拦截：原版 JobGiver_OptimizeApparel 在扫描任何服装之前，先比较
            // Find.TickManager.TicksGame 与 pawn.mindState.nextApparelOptimizeTick，
            // 一旦尚未到期就直接返回 null。把它钉在 int.MaxValue，代理既不会产生
            // 着装优化 Job，也不会周期性去 listerThings 里枚举全图服装（原版每
            // 6000~9000 tick 才允许一次扫描，是明显的白名单外开销）。
            // 这是最早的一道拦截，早于一切候选集构建与评分。
            if (pawn.mindState != null) pawn.mindState.nextApparelOptimizeTick = int.MaxValue;

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

        /// <summary>
        /// 脱下代理身上的全部服装并卸下全部装备，让它们落回地面。
        /// 代理未上场（刚生成、无地图）时无处安放，只能直接销毁——这种状态下的服装与
        /// 装备都是生成阶段刚造出来的新物件，销毁不会丢玩家的东西。
        /// </summary>
        public static void ReleaseWornGear(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            bool canPlace = pawn.Spawned && pawn.MapHeld != null && pawn.Position.IsValid;
            IntVec3 pos = canPlace ? pawn.Position : IntVec3.Invalid;

            if (pawn.apparel != null && pawn.apparel.WornApparelCount > 0)
            {
                if (canPlace) pawn.apparel.DropAll(pos, false, true);
                else pawn.apparel.DestroyAll(DestroyMode.Vanish);
            }

            if (pawn.equipment != null && pawn.equipment.HasAnything())
            {
                if (canPlace) pawn.equipment.DropAllEquipment(pos, false);
                else pawn.equipment.DestroyAllEquipment(DestroyMode.Vanish);
            }
        }

        /// <summary>
        /// 入舱前释放代理身上的一切随身物品：手上搬运的工件、物品栏库存、服装与装备全部落回地面。
        /// 代理在休眠期间身上必须空无一物，否则工件会跟着它一起被深保存进休眠舱，
        /// 服装与武器也会被"顺手带走"而不再出现在地图上。
        /// </summary>
        public static void ReleaseAllHeldThings(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            bool canPlace = pawn.Spawned && pawn.MapHeld != null && pawn.Position.IsValid;
            IntVec3 pos = canPlace ? pawn.Position : IntVec3.Invalid;

            if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                if (canPlace) pawn.carryTracker.TryDropCarriedThing(pos, ThingPlaceMode.Near, out _);
                else pawn.carryTracker.innerContainer.ClearAndDestroyContents(DestroyMode.Vanish);
            }

            if (pawn.inventory != null && pawn.inventory.innerContainer.Count > 0)
            {
                if (canPlace) pawn.inventory.DropAllNearPawn(pos, false, false);
                else pawn.inventory.DestroyAll(DestroyMode.Vanish);
            }

            ReleaseWornGear(pawn);
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
        private const int IdleRetryInterval = 30;
        private const int IdleGraceTicks = 180;
        // 泵在"无到期站 / 无空闲代理"时也要周期性醒来：代理池被占满期间不会有任何事件
        // 唤醒泵，个别不推进的 Job 就足以让整张地图再也派发不出工作。
        private const int IdlePumpFallbackInterval = 60;
        // 移动阶段长时间没有推进才算导航停滞；不限制合法原地工作的持续时间。
        private const int StalledJobTimeoutTicks = 2500;
        // 原版没有提供“不校验禁用状态但更新工作表”的接口。缓存字段访问器，只在工作站
        // 或筛选版本变化时写入，避免在热路径反射，也避免 SetPriority 的第三方禁用校验。
        private static readonly AccessTools.FieldRef<Pawn_WorkSettings, DefMap<WorkTypeDef, int>>
            workPrioritiesRef = AccessTools.FieldRefAccess<Pawn_WorkSettings,
                DefMap<WorkTypeDef, int>>("priorities");

        internal readonly OmniWorkFailureCache WorkFailures = new OmniWorkFailureCache();

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
        // Gizmo 运行在 OnGUI 阶段，重建代理的重活必须挪到 tick 里执行，这里只保存意图。
        private bool rebuildProxiesRequested;
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

        // ─── 区域遍历漏活诊断(默认关闭)─────────────────────────────────────
        private bool diagnoseRegionMiss;
        private long regionMissProbeCount;
        private long regionMissHitCount;

        /// <summary>
        /// 开启后，每当 GenClosest.ClosestThingReachable 对代理返回 null，都会用代理专用网格
        /// 再做一次全局可达搜索；若重查能命中，说明该目标是被原版区域遍历（map.regionGrid）
        /// 挡掉的。默认关闭：重查要枚举 listerThings 的候选集，属于明显的额外开销。
        /// </summary>
        public bool DiagnoseRegionMiss => diagnoseRegionMiss;

        public long RegionMissProbeCount => regionMissProbeCount;
        public long RegionMissHitCount => regionMissHitCount;

        public void ToggleDiagnoseRegionMiss()
        {
            diagnoseRegionMiss = !diagnoseRegionMiss;
            regionMissProbeCount = 0;
            regionMissHitCount = 0;
        }

        /// <summary>由 GenClosest.ClosestThingReachable 的探测补丁回调。</summary>
        public void NotifyRegionMissProbe(bool found)
        {
            regionMissProbeCount++;
            if (found) regionMissHitCount++;
        }

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
            public int idleSinceTick = -1;
            public bool waitingForWork;
            // 停滞检测：Job 引用变化或位置移动都会刷新进度时间戳，用于识别"在跑但完全不推进"的 Job。
            public Job lastTrackedJob;
            public IntVec3 lastTrackedPosition = IntVec3.Invalid;
            public int lastProgressTick = -1;
            public bool lastTrackedMoving;
            public int lastTrackedStartTick = -1;
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
            public Pawn probePawn;
            public Area_OmniWorkstation workArea;
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
            // 读档兜底：Pawn_NeedsTracker.ExposeData 会直接从存档恢复 needs 列表并重新绑定
            // joy/mood 等字段，这条路径不经过被 Patch_OmniWorkProxy_RemoveAllNeeds 短路的
            // AddOrRemoveNeedsAsAppropriate，因此存档里带上需求的代理会跨存档延续下来。
            if (Scribe.mode == LoadSaveMode.PostLoadInit) SanitizeAllProxiesAfterLoad();
        }

        /// <summary>
        /// 读档完成后对地图上的全部代理无条件净化一次，使代理在任何来源（历史存档、异常路径
        /// 或第三方直写）带回的状态需求后都立刻回到零需求状态。代理池记录不参与序列化，
        /// 且保存时正停留在场上的代理不会进入休眠舱，因此这里同时覆盖三处来源。
        /// </summary>
        private void SanitizeAllProxiesAfterLoad()
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                Pawn pooled = proxies[i]?.pawn;
                if (pooled != null && !pooled.Destroyed) OmniWorkProxyUtility.Sanitize(pooled);
            }

            for (int i = 0; i < sleepingProxies.Count; i++)
            {
                Pawn sleeping = sleepingProxies[i];
                if (sleeping != null && !sleeping.Destroyed) OmniWorkProxyUtility.Sanitize(sleeping);
            }

            List<Pawn> spawned = map?.mapPawns?.AllPawns;
            if (spawned == null) return;
            for (int i = 0; i < spawned.Count; i++)
            {
                Pawn pawn = spawned[i];
                if (OmniWorkProxyUtility.IsProxy(pawn)) OmniWorkProxyUtility.Sanitize(pawn);
            }
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
            stationStates.TryGetValue(station, out StationRuntime removedRuntime);
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.station != station) continue;
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(record.pawn);
            }
            if (removedRuntime != null) RemoveWorkArea(removedRuntime.workArea);
            stationStates.Remove(station);
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
            RebuildWorkArea(runtime);
            runtime.probePending = false;
            runtime.probePawn = null;
            ResetStationSearchState(runtime, CurrentTick);
            // 半径 / 启用状态变化会改变"哪些入口被覆盖"，立即失效入口授权缓存。
            OmniWorkProxyEntryAuthorization.Invalidate(map);
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

        internal void WakePumpNow()
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
            if (pawn == null) return;
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.pawn != pawn) continue;
                Building_OmniWorkstation station = record.station;
                record.issuedJob = null;
                record.trackedStartedJob = null;
                record.trackedStartedJobTick = -1;
                record.waitingForWork = false;

                if (station == null || !station.Operational || station.Map != map)
                {
                    PutProxyToSleep(record);
                    WakePumpNow();
                    return;
                }

                MoveProxyToStation(pawn, station);
                StationRuntime runtime = GetOrCreateStationRuntime(station);
                if (runtime.probePawn != null && runtime.probePawn != pawn &&
                    OmniWorkProxyUtility.TryGetStation(runtime.probePawn, out Building_OmniWorkstation owner) &&
                    owner == station)
                {
                    PutProxyToSleep(record);
                    return;
                }

                runtime.probePawn = pawn;
                runtime.probePending = true;
                runtime.nextSearchTick = int.MaxValue;
                if (record.idleSinceTick < 0) record.idleSinceTick = CurrentTick;
                int elapsed = CurrentTick - record.idleSinceTick;
                if (elapsed >= IdleGraceTicks)
                {
                    runtime.probePawn = null;
                    runtime.probePending = false;
                    runtime.consecutiveFailures++;
                    runtime.nextSearchTick = CurrentTick +
                        ExhaustedBackoffTicks(runtime.consecutiveFailures);
                    exhaustedStepCount++;
                    searchState = OmniWorkSearchState.Backoff;
                    PutProxyToSleep(record);
                    WakePumpNow();
                    return;
                }

                int waitTicks = Mathf.Min(IdleRetryInterval, IdleGraceTicks - elapsed);
                StartSearchWait(record, station, waitTicks);
                searchState = OmniWorkSearchState.Continuing;
                return;
            }
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
                if (record.pawn != pawn) continue;
                if (record.station == null)
                {
                    // 绑定意外丢失:代理已经启动了真实 Job,却无法据此唤醒泵扩容。
                    // 这里只记录异常信号,不做兜底恢复——绑定缺失意味着已丢失权威归属。
                    Log.WarningOnce("[OmniWorkstation] NotifyProxyStartedJob lost station binding: " + pawn,
                        Gen.HashCombineInt(pawn.thingIDNumber, 51907231));
                    continue;
                }

                if (record.trackedStartedJob == job && record.trackedStartedJobTick == job.startTick)
                {
                    OmniWorkProxyUtility.SetActive(pawn, true);
                    return;
                }
                WorkFailures.Started(pawn, job);
                record.trackedStartedJob = job;
                record.trackedStartedJobTick = job.startTick;
                record.issuedJob = job;
                record.needsSanitize = true;
                record.waitingForWork = false;
                record.idleSinceTick = -1;
                OmniWorkProxyUtility.SetActive(pawn, true);
                StationRuntime runtime = GetOrCreateStationRuntime(record.station);
                runtime.probePending = false;
                if (runtime.probePawn == pawn) runtime.probePawn = null;
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
            EnsureWorkArea(runtime);
            return runtime;
        }

        private void EnsureWorkArea(StationRuntime runtime)
        {
            Building_OmniWorkstation station = runtime?.station;
            if (station == null || !station.Spawned || station.Map != map) return;
            if (runtime.workArea != null && runtime.workArea.areaManager == map.areaManager) return;

            List<Area> areas = map.areaManager.AllAreas;
            for (int i = 0; i < areas.Count; i++)
            {
                if (areas[i] is Area_OmniWorkstation existing &&
                    existing.StationThingId == station.thingIDNumber)
                {
                    runtime.workArea = existing;
                    return;
                }
            }

            runtime.workArea = new Area_OmniWorkstation(map.areaManager, station.thingIDNumber);
            areas.Add(runtime.workArea);
            RebuildWorkArea(runtime);
        }

        private void RebuildWorkArea(StationRuntime runtime)
        {
            EnsureWorkArea(runtime);
            Area_OmniWorkstation area = runtime?.workArea;
            Building_OmniWorkstation station = runtime?.station;
            if (area == null || station == null || !station.Spawned) return;

            // 配置修改属于低频操作；逐格比较只通知真正发生变化的格子，
            // 避免 Clear 后遗漏原版寻路区域缓存的移除通知。
            int cellCount = map.cellIndices.NumGridCells;
            for (int index = 0; index < cellCount; index++)
            {
                IntVec3 cell = map.cellIndices.IndexToCell(index);
                bool covered = station.Covers(cell);
                if (area[index] != covered) area[index] = covered;
            }
        }

        private void RemoveWorkArea(Area_OmniWorkstation area)
        {
            if (area == null) return;
            foreach (Pawn pawn in PawnsFinder.All_AliveOrDead)
                pawn?.playerSettings?.Notify_AreaRemoved(area);
            // 私有休眠池不属于 PawnsFinder，需单独清理其存档引用。
            for (int i = 0; i < proxies.Count; i++)
            {
                Pawn pawn = proxies[i].pawn;
                pawn?.playerSettings?.Notify_AreaRemoved(area);
            }
            map.areaManager.AllAreas.Remove(area);
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RecoverExistingThings();
        }

        public override void MapRemoved()
        {
            // 池随地图消失：归属本池的代理（含正在其它地图上工作的）必须一并强删，
            // 否则会留下既不受工作站控制、也无法被任何池回收的无主代理。
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry != null)
            {
                registry.NotifyHomeMapRemoved(map);
            }
            else
            {
                for (int i = 0; i < proxies.Count; i++)
                {
                    Pawn pawn = proxies[i].pawn;
                    OmniWorkProxyUtility.Unassign(pawn);
                    if (pawn != null && pawn.Spawned && pawn.Map == map)
                        pawn.Destroy(DestroyMode.Vanish);
                }
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

            if (rebuildProxiesRequested)
            {
                rebuildProxiesRequested = false;
                PerformRecreateAllProxies();
            }

            // 界面登记的延迟管理请求（R-8 / R-9）：与地图集合的改动时机一致。
            ProcessPendingManagementRequests();

            int tick = Find.TickManager.TicksGame;

            // 全局总闸：关闭时本池停止派发任何工作，并把场上代理收回休眠舱（不销毁，
            // 配置与代理数量保持不变）。仅在每个维护周期执行一次回收，避免每 tick 遍历。
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry != null && !registry.GlobalWorkEnabled)
            {
                if (tick % AssignmentInterval == map.uniqueID % AssignmentInterval)
                    ReclaimAllActive();
                nextPumpTick = int.MaxValue;
                return;
            }

            if (tick % AssignmentInterval == map.uniqueID % AssignmentInterval)
            {
                long maintStart = Stopwatch.GetTimestamp();
                WorkFailures.Prune();
                RemoveInvalidStations();
                EnsureProxyCount();
                MaintainAbroadProxies(tick);
                // 入口授权表：每个维护周期全量重算一次（入口数量少，成本可忽略）。
                OmniWorkProxyEntryAuthorization.Refresh(map);
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
                    // 所有站都在深睡时不能永久停摆：代理池被占满期间没有任何事件会唤醒泵，
                    // 一个停滞的 Job 就足以让整张地图再也派发不出工作。保留固定重试周期。
                    nextPumpTick = earliestTick == int.MaxValue
                        ? tick + IdlePumpFallbackInterval
                        : earliestTick;
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
                    // 代理池被占满属于常态，但深睡后只能等事件唤醒；同样保留固定重试周期，
                    // 任何原因导致的停摆最多持续一个周期，代理释放后泵会自行恢复派发。
                    stationRuntime.nextSearchTick = int.MaxValue;
                    nextPumpTick = tick + IdlePumpFallbackInterval;
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
                    if (stationRuntime.probePawn == record.pawn) stationRuntime.probePawn = null;
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
                Pawn pawn = record.pawn;
                if (!OmniWorkProxyUtility.IsActive(pawn)) continue;
                // 以 Pawn.CurJob 为显示来源：record.issuedJob 只是调度记录，对应的 Job
                // 归还对象池后可能已被复用成其它 Pawn 的任务。
                Job job = pawn.CurJob ?? record.issuedJob;
                if (job == null) continue;
                string name = pawn.Name?.ToStringShort ?? "Worker";
                output.Add(new OmniWorkProxyStatus(name, SafeJobReport(pawn, job), pawn));
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
            sb.AppendLine("regionMissDiag=" + diagnoseRegionMiss
                + " probes=" + regionMissProbeCount
                + " hits=" + regionMissHitCount);
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
            string fallback = job.def?.label ?? job.def?.defName ?? "-";
            // 只有该 Job 确实由这个 Pawn 执行时才交给原版生成报告：Job.GetReport 会经
            // Job.GetCachedDriver 创建并缓存 JobDriver。Job 一旦归还原版对象池，实例会被
            // 复用给其它 Pawn，此时原版用 Log.Error 报 “Tried to use the same driver for
            // 2 pawns”（是日志而非异常，下面的 catch 拦不住），所以在这里主动回避。
            if (pawn == null || pawn.CurJob != job) return fallback;
            try
            {
                string report = job.GetReport(pawn);
                if (!report.NullOrEmpty()) return report;
            }
            catch (Exception)
            {
                // 第三方 JobDriver 的报告生成失败时只降级显示 Def，不影响调度窗口。
            }
            return fallback;
        }

        private void RecoverExistingThings()
        {
            proxiesRecovered = true;
            stations.Clear();
            stationStates.Clear();
            proxies.Clear();

            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;

            List<Pawn> sleeping = sleepingProxies.InnerListForReading;
            for (int i = 0; i < sleeping.Count; i++)
            {
                Pawn pawn = sleeping[i];
                if (pawn == null || pawn.Destroyed || !OmniWorkProxyUtility.IsProxy(pawn)) continue;
                PrepareProxy(pawn);
                registry?.Register(pawn, map, -1);
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
            RemoveOrphanedWorkAreas();

            List<Thing> pawns = map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn);
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                if (!(pawns[i] is Pawn pawn) || !OmniWorkProxyUtility.IsProxy(pawn)) continue;
                // 索引里查不到时，先用代理身上的 Hediff 镜像把归属救回来
                // （索引损坏，或从旧版本存档升级上来的代理）。
                registry?.TryRestoreFromMirror(pawn);
                // 归属其它图的代理此刻是"驻外工作"，不能被本图收养走：否则两张图的池会互相抢夺，
                // 并且本图的休眠舱会把不属于本池的代理深保存进来。
                if (registry != null && registry.TryGetHome(pawn, out GameComponent_OmniWorkProxyRegistry.ProxyHomeRecord home)
                    && home.homeMap != null && home.homeMap != map)
                    continue;
                PrepareProxy(pawn);
                registry?.Register(pawn, map, -1);
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
                    if (station != null && stationStates.TryGetValue(station, out StationRuntime runtime))
                    {
                        RemoveWorkArea(runtime.workArea);
                        stationStates.Remove(station);
                    }
                    stations.RemoveAt(i);
                    continue;
                }

                StationRuntime validRuntime = GetOrCreateStationRuntime(station);
                if (validRuntime.probePending &&
                    (validRuntime.probePawn == null || validRuntime.probePawn.Destroyed ||
                     !OmniWorkProxyUtility.TryGetStation(validRuntime.probePawn,
                         out Building_OmniWorkstation probeStation) || probeStation != station))
                {
                    validRuntime.probePawn = null;
                    validRuntime.probePending = false;
                    validRuntime.nextSearchTick = CurrentTick;
                }
            }
            if (stationCursor >= stations.Count) stationCursor = 0;
        }

        private void RemoveOrphanedWorkAreas()
        {
            List<Area> areas = map.areaManager.AllAreas;
            for (int i = areas.Count - 1; i >= 0; i--)
            {
                if (!(areas[i] is Area_OmniWorkstation workArea)) continue;
                Building_OmniWorkstation owner = null;
                for (int j = 0; j < stations.Count; j++)
                {
                    if (stations[j].thingIDNumber != workArea.StationThingId) continue;
                    owner = stations[j];
                    break;
                }
                if (owner == null || GetOrCreateStationRuntime(owner).workArea != workArea)
                    RemoveWorkArea(workArea);
            }
        }

        internal void EnsureProxyCount()
        {
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            int removedInvalid = 0;
            for (int i = proxies.Count - 1; i >= 0; i--)
            {
                ProxyRecord record = proxies[i];
                Pawn pawn = record.pawn;

                // 记录失效：代理已销毁 → 注销归属并移出池。
                if (pawn == null || pawn.Destroyed)
                {
                    registry?.Unregister(pawn);
                    proxies.RemoveAt(i);
                    removedInvalid++;
                    continue;
                }

                if (registry != null)
                {
                    if (!registry.TryGetHome(pawn, out GameComponent_OmniWorkProxyRegistry.ProxyHomeRecord home))
                    {
                        registry.Register(pawn, map, record.station?.thingIDNumber ?? -1);
                    }
                    else if (home.homeMap != null && home.homeMap != map)
                    {
                        // 归属已被转到别的池：本图只移出视图，绝不触碰代理本体。
                        proxies.RemoveAt(i);
                        continue;
                    }
                }

                if (sleepingProxies.Contains(pawn)) continue;

                // 归属本池的代理即使此刻在别的图上（驻外工作）也必须保留在池内，
                // 由 MaintainAbroadProxies 负责把它带回来；这里绝不能像旧实现那样删除记录，
                // 否则代理会变成既不受工作站控制、也无法被任何池回收的孤儿。
                if (pawn.Spawned && pawn.Map != map)
                    registry?.NotifySpawned(pawn, pawn.Map);
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
                registry?.Unregister(record.pawn);
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

        // ── 供全局管理组件（GameComponent_OmniWorkProxyRegistry）调用的内部接口 ──────────

        /// <summary>本组件所属地图；registry 用来判断"代理当前图是否就是它的归属图"。</summary>
        internal Map OwningMap => map;

        /// <summary>驻外代理的回收超时（tick）。零第三方依赖的兜底回收周期。</summary>
        private const int AbroadReclaimTicks = 1500;

        private ProxyRecord FindProxyRecord(Pawn pawn)
        {
            for (int i = 0; i < proxies.Count; i++)
                if (proxies[i].pawn == pawn) return proxies[i];
            return null;
        }

        /// <summary>按记录停止代理当前工作（registry 的强删/回收使用）。</summary>
        internal void StopIssuedJobForRecord(Pawn pawn)
        {
            if (pawn == null) return;
            ProxyRecord record = FindProxyRecord(pawn);
            if (record != null)
            {
                StopIssuedJob(record);
                return;
            }
            OmniWorkProxyUtility.SetActive(pawn, false);
            if (pawn.mindState != null) pawn.mindState.Active = false;
            if (pawn.CurJob != null) EndCurrentJobForManagement(pawn);
        }

        /// <summary>强制入睡：不销毁，直接收回**本图**（归属图）的休眠舱。</summary>
        internal void ForceSleep(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            ProxyRecord record = FindProxyRecord(pawn);
            if (record == null)
            {
                record = new ProxyRecord { pawn = pawn };
                proxies.Add(record);
            }
            PutProxyToSleep(record);
        }

        /// <summary>
        /// 清空本池：休眠舱（销毁内容物）与在册记录一起清干净。"重建本池"用；
        /// 代理本体的销毁由调用方（registry.ForceDestroy）负责，这里只清本地视图与失败表。
        /// </summary>
        internal void ClearSleepingPool()
        {
            sleepingProxies.ClearAndDestroyContents();
            proxies.Clear();
            sleepProxyCount = 0;
            WorkFailures.ClearAll();
            proxySearchCursor = 0;
        }

        /// <summary>清掉休眠舱里已失效的条目（已销毁 / 已不是代理），保留正常代理（R-9 池修复用）。</summary>
        internal void ClearSleepingPoolOfInvalid()
        {
            List<Pawn> sleeping = sleepingProxies.InnerListForReading;
            for (int i = sleeping.Count - 1; i >= 0; i--)
            {
                Pawn pawn = sleeping[i];
                if (pawn != null && !pawn.Destroyed && OmniWorkProxyUtility.IsProxy(pawn)) continue;
                sleepingProxies.Remove(pawn);
                if (sleepProxyCount > 0) sleepProxyCount--;
            }
        }

        /// <summary>校验本池全部代理（含休眠舱内）的归属镜像，返回校验数量。</summary>
        internal int VerifyPoolMirrors()
        {
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry == null) return 0;
            List<Pawn> list = registry.Scratch;
            registry.EnumeratePool(map, list);
            for (int i = 0; i < list.Count; i++) registry.VerifyMirror(list[i]);
            return list.Count;
        }

        /// <summary>全局总闸关闭时：把本池所有代理（含驻外）收回本图休眠舱，不销毁。</summary>
        internal void ReclaimAllActive()
        {
            for (int i = proxies.Count - 1; i >= 0; i--)
            {
                ProxyRecord record = proxies[i];
                if (record.pawn == null || record.pawn.Destroyed) continue;
                if (sleepingProxies.Contains(record.pawn)) continue;
                StopIssuedJob(record);
                PutProxyToSleep(record);
            }
        }

        /// <summary>清理某代理在各图失败表中的条目（强删时调用）。</summary>
        internal void PurgeFailureFor(Pawn pawn)
        {
            WorkFailures.Forget(pawn);
        }

        // ── 界面登记的延迟管理请求（R-8 / R-9）────────────────────────────────
        // Gizmo 与状态窗口的按钮都运行在 OnGUI 阶段，而销毁/生成 Pawn 会改动地图上的集合，
        // 因此界面只登记意图，真正执行统一放在下一 tick。

        private readonly List<Pawn> pendingProxyReclaims = new List<Pawn>();
        private readonly List<Pawn> pendingProxyRecreates = new List<Pawn>();
        private bool pendingPoolRepair;

        /// <summary>登记"停止该代理并立即收回其归属池"（不销毁）。</summary>
        public void RequestProxyReclaim(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            if (!pendingProxyReclaims.Contains(pawn)) pendingProxyReclaims.Add(pawn);
        }

        /// <summary>登记"重建该代理"（销毁并让所属池补建）。</summary>
        public void RequestProxyRecreate(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            if (!pendingProxyRecreates.Contains(pawn)) pendingProxyRecreates.Add(pawn);
        }

        /// <summary>登记"修复本池"（清理失效项 + 按配置补齐数量）。</summary>
        public void RequestRepairPool()
        {
            pendingPoolRepair = true;
        }

        /// <summary>在 tick 中执行界面登记的管理请求：与地图集合的改动时机一致。</summary>
        private void ProcessPendingManagementRequests()
        {
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;

            if (pendingPoolRepair)
            {
                pendingPoolRepair = false;
                int created = registry == null ? 0 : registry.RepairPool(map);
                Messages.Message("OmniWorkstation_RepairPoolDone".Translate(created),
                    MessageTypeDefOf.TaskCompletion, false);
            }

            if (pendingProxyReclaims.Count > 0)
            {
                for (int i = 0; i < pendingProxyReclaims.Count; i++)
                {
                    Pawn pawn = pendingProxyReclaims[i];
                    if (pawn == null || pawn.Destroyed) continue;
                    if (registry != null && registry.ReclaimNow(pawn))
                        Messages.Message("OmniWorkstation_ProxyReclaimed".Translate(),
                            MessageTypeDefOf.TaskCompletion, false);
                }
                pendingProxyReclaims.Clear();
            }

            if (pendingProxyRecreates.Count > 0)
            {
                for (int i = 0; i < pendingProxyRecreates.Count; i++)
                {
                    Pawn pawn = pendingProxyRecreates[i];
                    if (pawn == null || pawn.Destroyed) continue;
                    if (registry != null && registry.RecreateSingle(pawn))
                        Messages.Message("OmniWorkstation_ProxyRecreated".Translate(),
                            MessageTypeDefOf.TaskCompletion, false);
                }
                pendingProxyRecreates.Clear();
            }
        }

        /// <summary>
        /// 驻外代理"空闲"时的回收超时（tick）：给它一段在目标图找工作的宽限期，
        /// 超时仍空闲即视为那边没有活可干，收回本池。
        /// </summary>
        private const int AbroadIdleReclaimTicks = 600;

        /// <summary>
        /// 驻外看护：归属本图、但当前在别的图上的代理，空闲超时或占用超时后强制收回本图休眠舱。
        ///
        /// **不做"走楼梯 / portal 回程 job"**：原版没有"单个 Pawn 走进 portal 即换图"的通用 API ——
        /// `EnterPortalUtility.JobOnPortal` 只构造 `HaulToPortal`，且 `HasJobOnPortal` 要求
        /// `leftToLoad` 非空（Source/RimWorld/EnterPortalUtility.cs:23-30）；玩家手动进入走的是
        /// `LordJob_LoadAndEnterPortal` 的 Lord 机制，而代理不在殖民者列表里。第三方 portal
        /// （MultiFloors 的 Stair、SimplePortal 等）各有自己的换图实现，逐个写适配器会违反
        /// "零 Mod 依赖"约束。因此回程统一走"传送回收"，正确性由本方法保证。
        ///
        /// 无第三方 Mod 时列表恒空，本方法不产生任何实际工作。
        /// </summary>
        private void MaintainAbroadProxies(int tick)
        {
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry == null) return;

            // 低频镜像校验（每 240 tick）：以权威表为准修正本池代理身上的归属镜像。
            // 只遍历本池，遵守池隔离；单次开销是每代理 O(hediffs) 的线性扫描。
            if (tick % 240 == 0)
            {
                List<Pawn> verifyList = registry.Scratch;
                registry.EnumeratePool(map, verifyList);
                for (int i = 0; i < verifyList.Count; i++)
                    registry.VerifyMirror(verifyList[i]);
            }

            List<Pawn> list = registry.Scratch;
            registry.EnumerateForeign(map, list);
            for (int i = 0; i < list.Count; i++)
            {
                Pawn pawn = list[i];
                if (pawn == null || pawn.Destroyed)
                {
                    registry.Unregister(pawn);
                    continue;
                }

                registry.TryGetHome(pawn, out GameComponent_OmniWorkProxyRegistry.ProxyHomeRecord home);
                int since = home?.abroadSinceTick ?? -1;
                if (since < 0) continue;

                // 正在执行真实工作：给更长宽限（那边确实可能有活），超时仍未结束才视为卡住并收回。
                bool working = pawn.Spawned && pawn.MapHeld != null && !IsIdleState(pawn);
                int timeout = working ? AbroadReclaimTicks : AbroadIdleReclaimTicks;
                if (tick - since < timeout) continue;

                // 超时仍未回到归属图：传送回收（零依赖兜底，任何 Mod 下都成立）。
                ReclaimAbroadProxy(pawn);
            }
        }

        /// <summary>把驻外代理强制收回**归属图**（本图）休眠舱。</summary>
        private void ReclaimAbroadProxy(Pawn pawn)
        {
            ProxyRecord record = FindProxyRecord(pawn);
            if (record == null)
            {
                record = new ProxyRecord { pawn = pawn };
                proxies.Add(record);
            }
            StopIssuedJob(record);
            PutProxyToSleep(record);
            WakePumpNow();
        }

        /// <summary>
        /// 登记"重建全部代理"的请求。Gizmo 的 action 运行在 OnGUI 阶段，而销毁与生成 Pawn
        /// 都会改动地图上的集合，因此这里只登记意图，实际动作交给下一 tick 执行。
        /// </summary>
        public void RequestRecreateAllProxies()
        {
            rebuildProxiesRequested = true;
            WakePumpNow();
        }

        /// <summary>
        /// 强制销毁本图全部工作代理并重新创建一批。
        ///
        /// 用于代理状态被第三方 Mod 污染、或代理身上出现无法自行恢复的异常时的兜底恢复。
        /// 销毁前会依次结束 Job、解除绑定并把随身物品放回地面，因此玩家物资不会随代理消失。
        /// 新代理沿用 EnsureProxyCount 的分批创建路径，不需要额外的重建状态。
        /// </summary>
        private void PerformRecreateAllProxies()
        {
            // 归属本图池的代理全部强删（包括此刻正在其它地图上工作的），再按分批路径重建。
            // registry 按 homeMap 过滤，因此其他池的代理一律不受影响（池隔离）。
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry != null)
            {
                int destroyedCount = registry.ForceRecreatePool(map);
                WorkFailures.ClearAll();
                proxySearchCursor = 0;
                searchState = OmniWorkSearchState.Waiting;
                nextPumpTick = CurrentTick;
                WakePumpNow();
                Messages.Message(
                    "OmniWorkstation_ProxiesRecreated".Translate(destroyedCount, proxies.Count),
                    MessageTypeDefOf.TaskCompletion, false);
                return;
            }

            int destroyed = 0;

            // 场上代理：先停 Job、解绑，再让携带物落地，最后销毁。
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                Pawn pawn = record.pawn;
                if (pawn == null) continue;
                StopIssuedJob(record);
                OmniWorkProxyUtility.Unassign(pawn);
                if (sleepingProxies.Contains(pawn)) sleepingProxies.Remove(pawn);
                OmniWorkProxyUtility.ReleaseAllHeldThings(pawn);
                if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
                destroyed++;
            }
            proxies.Clear();

            // 休眠舱兜底：正常路径下它与 proxies 一一对应，这里收掉任何未被收录的残留实例。
            while (sleepingProxies.Count > 0)
            {
                Pawn pawn = sleepingProxies.InnerListForReading[0];
                if (!sleepingProxies.Remove(pawn)) break;
                if (pawn == null) continue;
                OmniWorkProxyUtility.Unassign(pawn);
                if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
                destroyed++;
            }

            // 失败隔离表以 Pawn 为键，代理整体换代后整表作废，避免继续强引用已销毁的代理。
            WorkFailures.ClearAll();
            proxySearchCursor = 0;
            searchState = OmniWorkSearchState.Waiting;
            nextPumpTick = CurrentTick;
            // 立即补一批，其余沿用周期维护的分批创建，避免一次生成上百个代理造成卡顿。
            EnsureProxyCount();
            WakePumpNow();
            Messages.Message(
                "OmniWorkstation_ProxiesRecreated".Translate(destroyed, proxies.Count),
                MessageTypeDefOf.TaskCompletion, false);
        }

        private Pawn CreateProxy()
        {
            Building_OmniWorkstation station = FirstOperationalStation();
            if (station == null || OmniWorkstationDefOf.FAOC_OmniWorkProxy == null) return null;

            try
            {
                Pawn pawn = PawnGenerator.GeneratePawn(OmniWorkstationDefOf.FAOC_OmniWorkProxy, Faction.OfPlayer);
                PrepareProxy(pawn);
                // 归属登记：代理属于"创建它的这台地图的池"，与它以后被搬到哪张图无关。
                GameComponent_OmniWorkProxyRegistry.Instance?.Register(pawn, map, station.thingIDNumber);
                if (!sleepingProxies.TryAdd(pawn))
                {
                    GameComponent_OmniWorkProxyRegistry.Instance?.Unregister(pawn);
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
            // 跨图驻外时同样从"当前所在图"的列表里移出，否则会在那张图的面板上冒出来。
            if (pawn.Spawned && pawn.Map != null)
                pawn.Map.mapPawns.DeRegisterPawn(pawn);
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

            // 进度跟踪：Job 引用变化与位置移动都算推进。停滞判定依赖这个时间戳，
            // 因此必须在所有提前返回之前刷新。
            int now = CurrentTick;
            bool moving = pawn.pather != null && pawn.pather.Moving;
            int startTick = pawn.CurJob?.startTick ?? -1;
            if (record.lastProgressTick < 0 || pawn.CurJob != record.lastTrackedJob ||
                startTick != record.lastTrackedStartTick || moving != record.lastTrackedMoving ||
                pawn.Position != record.lastTrackedPosition)
            {
                record.lastTrackedJob = pawn.CurJob;
                record.lastTrackedPosition = pawn.Position;
                record.lastProgressTick = now;
                record.lastTrackedMoving = moving;
                record.lastTrackedStartTick = startTick;
            }

            // 代理不穿戴、不装备：维护扫描发现代理身上正跑着着装/装备 Job 时立即中止，
            // 否则它会被 JobDriver_Wear 的延迟 toil 钉在原地（该 toil 依赖代理自身 tick）。
            if (IsForbiddenGearJob(pawn.CurJob))
            {
                CancelForbiddenGearJob(pawn);
                return sleepingProxies.Contains(pawn);
            }

            if (record.issuedJob != null && (record.station == null || !record.station.Operational))
            {
                StopIssuedJob(record);
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
            }

            // 空闲宽限期中的 Wait 是调度状态而非待回收 Job；它结束后原版会自行
            // 进入思考树，因此维护扫描不能提前解除工作站与允许区绑定。
            if (record.waitingForWork && record.station != null &&
                pawn.CurJob != null && IsIdleJob(pawn.CurJob))
                return false;

            // 原版接力缓冲（真实工作成功结束后的 1 tick Wait_MaintainPosture）同属工作链
            // 中间态：维护扫描若在此解除绑定，代理会被送进休眠舱，原版便无法原地接续工作。
            if (IsVanillaRelayWait(record)) return false;

            // Job 会被原版对象池复用，不能只靠引用变化判断任务是否已经结束：旧的
            // issuedJob 可能在结束后立刻被复用成 Wait，并再次成为 pawn.CurJob。
            // 无论引用是否相同，只要当前已无 Job 或进入原版等待 Job，就必须释放代理，
            // 否则 active 会永久占满代理池并让生命周期调度停在 NoIdleProxy 深睡状态。
            if (record.issuedJob != null && (pawn.CurJob == null || IsIdleJob(pawn.CurJob)))
            {
                record.issuedJob = null;
                record.station = null;
                OmniWorkProxyUtility.Unassign(pawn);
            }

            if (record.issuedJob != null && pawn.CurJob != record.issuedJob)
            {
                // 原版可能插入机会任务或 finalizer；当前 Job 非空时可以视为同一工作链继续跟踪。
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

            if (record.issuedJob != null)
            {
                // 仅兜底第三方干预或异常读档造成的移动停滞；调度等待和原地工作不在此中断。
                if (!record.waitingForWork && IsStalledJob(record, pawn, now))
                {
                    Log.WarningOnce("[OmniWorkstation] released a stalled job: " + pawn,
                        Gen.HashCombineInt(pawn.thingIDNumber, 77120433));
                    WorkFailures.NavigationFailed(pawn, pawn.pather.Destination);
                    StopIssuedJob(record);
                    record.station = null;
                    OmniWorkProxyUtility.Unassign(pawn);
                    PutProxyToSleep(record);
                    WakePumpNow();
                    return sleepingProxies.Contains(pawn);
                }
                return false;
            }

            // 原 Job 结束时 JobTracker 可能立即启动一个 Wait/生活 Job，统一停止后再由调度器分配。
            if (pawn.CurJob != null)
                EndCurrentJobForManagement(pawn);

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

        /// <summary>
        /// 判定代理是否卡在一个完全不推进的 Job 上：Job 引用与位置在 StalledJobTimeoutTicks
        /// 内都没有变化，且它当前确实在跑一个非等待 Job。用于释放永久占用代理池的停滞
        /// Job —— 否则一旦全部代理都被占住，泵不会再收到任何事件，整张地图都会停止派发。
        /// 仅检查移动阶段，合法的长时间原地工作不属于导航停滞。
        /// </summary>
        private static bool IsStalledJob(ProxyRecord record, Pawn pawn, int now)
        {
            if (record.lastProgressTick < 0) return false;
            if (now - record.lastProgressTick <= StalledJobTimeoutTicks) return false;
            return pawn.CurJob != null && !IsIdleJob(pawn.CurJob) && pawn.pather != null && pawn.pather.Moving;
        }

        internal static bool IsIdleJob(Job job)
        {
            return job?.def == null || BlacklistedJobDefs.Contains(job.def.defName);
        }

        internal static bool IsIdleState(Pawn pawn)
        {
            return pawn == null || IsIdleJob(pawn.CurJob) || pawn.mindState?.IsIdle == true;
        }

        /// <summary>
        /// 原版 Pawn_JobTracker.EndCurrentJob 在真实工作成功结束且代理未在移动时，会插入一个
        /// 1 tick 的 Wait_MaintainPosture 作为接力缓冲：它下一 tick 结束时原版会照常调用
        /// TryFindAndStartJob，代理因此可以原地接续下一项工作。它是工作链的中间态而非空闲，
        /// 一旦按空闲回收，代理会被立刻拽回工作站，原版接力就此被打断。
        /// 调度器自派的探路等待与宽限等待 def 相同、1 tick 探路的时长也相同，只能靠
        /// waitingForWork 区分：真实工作开始时该标记已被清除，调度器派发时才会置位。
        /// </summary>
        private static bool IsVanillaRelayWait(ProxyRecord record)
        {
            if (record == null || record.waitingForWork) return false;
            Job job = record.pawn?.CurJob;
            return job != null && job.def == JobDefOf.Wait_MaintainPosture && job.expiryInterval <= 1;
        }

        /// <summary>
        /// 静态补丁入口：若代理正处在原版接力缓冲中，则把 issuedJob 同步为当前 Job 并返回 true。
        /// 真实 Job 在插入缓冲的这一 tick 已经归还原版对象池，该实例可能立刻被复用成其它 Pawn
        /// 的任务；继续持有旧引用会让状态窗口拿到别人的 Job 去调用原版 GetReport。
        /// </summary>
        internal bool TryTrackVanillaRelayWait(Pawn pawn)
        {
            if (pawn == null) return false;
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.pawn != pawn) continue;
                if (!IsVanillaRelayWait(record)) return false;
                record.issuedJob = pawn.CurJob;
                record.trackedStartedJob = null;
                record.trackedStartedJobTick = -1;
                return true;
            }
            return false;
        }

        /// <summary>按休眠舱的方式反生成并深保存代理；容器本身从不 Tick 内容物。</summary>
        private void PutProxyToSleep(ProxyRecord record)
        {
            Pawn pawn = record?.pawn;
            if (pawn == null || pawn.Destroyed) return;

            if (record.station != null && stationStates.TryGetValue(record.station, out StationRuntime runtime) &&
                runtime.probePawn == pawn)
            {
                runtime.probePawn = null;
                runtime.probePending = false;
            }

            // 工作会话结束:入舱前统一恢复干净状态,取代原"每 250 tick 全员清洗"的周期任务。
            if (record.needsSanitize)
            {
                record.needsSanitize = false;
                OmniWorkProxyUtility.Sanitize(pawn);
            }

            // 原版无工作时通常会启动 Wait/GotoWander；入舱前明确结束，避免保存一个冻结 Job。
            if (pawn.CurJob != null)
                EndCurrentJobForManagement(pawn);
            // 入舱前把手上的工件、物品栏库存和身上的服装装备全部释放到地面：
            // 代理休眠时身上必须空无一物，否则这些物品会被一起深保存进休眠舱。
            OmniWorkProxyUtility.ReleaseAllHeldThings(pawn);
            OmniWorkProxyUtility.SetActive(pawn, false);
            if (pawn.mindState != null) pawn.mindState.Active = false;
            OmniWorkProxyUtility.Unassign(pawn);
            record.issuedJob = null;
            record.trackedStartedJob = null;
            record.trackedStartedJobTick = -1;
            record.station = null;
            record.waitingForWork = false;
            record.idleSinceTick = -1;
            if (sleepingProxies.Contains(pawn)) return;

            // 任意图都可反生成：跨图回收是本设计的核心能力（驻外代理必须能被收回归属池）。
            // 曾在别的图上的代理 DeSpawn 后统一收入**归属图**（本图）的休眠舱。
            if (pawn.Spawned)
                pawn.DeSpawn(DestroyMode.Vanish);
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
            // 出舱位置恒为工作站建筑所在格;即使该格不可站立也不再退避到附近可站立格。
            IntVec3 cell = station.Position;
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
            StationRuntime runtime = GetOrCreateStationRuntime(station);
            if (pawn.playerSettings != null)
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = runtime.workArea;

            // 热续代理未经过入舱清洗,派发前补一次净化,保证新 Job 开始前状态干净。
            if (record.needsSanitize)
            {
                record.needsSanitize = false;
                OmniWorkProxyUtility.Sanitize(pawn);
            }

            ApplyStationWorkSettings(record, station);
            pawn.mindState.Active = true;
            OmniWorkProxyUtility.SetActive(pawn, true);
            record.idleSinceTick = -1;
            record.waitingForWork = true;
            runtime.probePawn = pawn;
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
        /// 在工作站中心启动一段等待；等待自然结束时原版会进入思考树选取下一项工作。
        /// 调度器不调用任何 WorkGiver，也不枚举具体工作目标。
        /// </summary>
        private void StartSearchWait(ProxyRecord record, Building_OmniWorkstation station, int waitTicks)
        {
            Pawn pawn = record?.pawn;
            if (pawn == null || pawn.Destroyed || !pawn.Spawned) return;
            MoveProxyToStation(pawn, station);
            // 与 WakeProxyForVanillaSearch 保持一致:探路期间必须持有有效的工作站绑定,
            // 否则 NotifyProxyStartedJob 会跳过该代理,TryGetStation 也会一并失效。
            record.station = station;
            OmniWorkProxyUtility.Assign(pawn, station);
            StationRuntime runtime = GetOrCreateStationRuntime(station);
            if (pawn.playerSettings != null)
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = runtime.workArea;
            if (pawn.CurJob != null) EndCurrentJobForManagement(pawn);

            pawn.mindState.Active = true;
            OmniWorkProxyUtility.SetActive(pawn, true);
            record.waitingForWork = true;
            Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, Mathf.Max(1, waitTicks));
            record.issuedJob = wait;
            pawn.jobs.StartJob(wait, JobCondition.InterruptForced, cancelBusyStances: false,
                addToJobsThisTick: false);
            record.issuedJob = pawn.CurJob;
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

        /// <summary>代理的等待位置恒为工作站建筑所在格;不做附近可站立格回退。</summary>
        private void MoveProxyToStation(Pawn pawn, Building_OmniWorkstation station)
        {
            IntVec3 cell = station.Position;
            if (pawn.Position == cell) return;
            pawn.Position = cell;
            pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
        }

        private static readonly HashSet<string> BlacklistedJobDefs = new HashSet<string>(StringComparer.Ordinal)
        {
            "Wait", "Wait_MaintainPosture", "Wait_Wander", "Goto", "GotoWander", "LayDown", "Ingest", "SocialRelax",
            "Lovin", "Meditate", "Flee", "ExitMapBest", "JoinCaravan"
        };

        /// <summary>
        /// 判定代理被禁止的 Job。方法名沿用历史命名（最初只涵盖着装/装备），现在同时涵盖娱乐：
        /// 1) 着装/装备：JobDriver_Wear 会停在延迟 toil 上等待 EquipDelay 走完，而延迟 toil 完全
        ///    依赖 Pawn.Tick → JobTrackerTick 推进；代理一旦在等待期间被判为空闲（Pawn.Tick 被
        ///    Patch_OmniWorkProxy_FreezeWhenInactive 跳过），进度条就永远停在原地，表现为
        ///    "站在服装旁、显示正在穿着、然后一直不动"。
        /// 2) 娱乐：代理不做任何娱乐行为，否则代理池会被娱乐占用。JobGiver 层的拦截
        ///    (Patch_OmniWorkProxy_NoJoyJob) 是第一道防线，这里按 JobDef.joyKind 语义兜底，
        ///    可覆盖绕过 JoyGiver 直接启动娱乐 Job 的第三方路径；判定不依赖 defName 名单，
        ///    因此对各 Mod 自定义的娱乐 JobDef 同样生效。
        /// </summary>
        internal static bool IsForbiddenGearJob(Job job)
        {
            JobDef def = job?.def;
            if (def == null) return false;
            if (def == JobDefOf.Wear || def == JobDefOf.Equip) return true;
            return def.joyKind != null;
        }

        private static void StopIssuedJob(ProxyRecord record)
        {
            if (record?.station?.Map != null)
            {
                MapComponent_OmniWorkstation manager =
                    record.station.Map.GetComponent<MapComponent_OmniWorkstation>();
                if (manager.stationStates.TryGetValue(record.station, out StationRuntime runtime) &&
                    runtime.probePawn == record.pawn)
                {
                    runtime.probePawn = null;
                    runtime.probePending = false;
                }
            }
            OmniWorkProxyUtility.SetActive(record.pawn, false);
            if (record.pawn?.mindState != null) record.pawn.mindState.Active = false;
            if (record.pawn != null && record.pawn.CurJob != null)
                EndCurrentJobForManagement(record.pawn);
            record.issuedJob = null;
            record.waitingForWork = false;
            record.idleSinceTick = -1;
        }

        private static void EndCurrentJobForManagement(Pawn pawn)
        {
            if (pawn?.jobs?.curJob == null) return;
            OmniWorkProxyUtility.BeginManagedTransition(pawn);
            try
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            }
            finally
            {
                OmniWorkProxyUtility.EndManagedTransition(pawn);
            }
        }

        /// <summary>
        /// 立即中止代理身上的着装/装备 Job，并把代理交还调度器重新指派。
        /// 结束 Job 走受管转换，避免 EndCurrentJob 补丁把这次取消误判为"代理自然空闲"
        /// 而把它送进宽限等待链；随后立即入舱，代理不会留在原地空转。
        /// </summary>
        public void CancelForbiddenGearJob(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            for (int i = 0; i < proxies.Count; i++)
            {
                ProxyRecord record = proxies[i];
                if (record.pawn != pawn) continue;
                EndCurrentJobForManagement(pawn);
                OmniWorkProxyUtility.SetActive(pawn, false);
                if (pawn.mindState != null) pawn.mindState.Active = false;
                PutProxyToSleep(record);
                WakePumpNow();
                return;
            }
            // 不在代理池中的代理（例如已解绑）只负责把 Job 收掉。
            EndCurrentJobForManagement(pawn);
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
    /// 代理的单趟搬运量上限。
    /// 1.6 的 Pawn_CarryTracker.MaxStackSpaceEver 对容量取 Min(td.stackLimit, 携带量 / 单件体积)，
    /// 因此即使代理的 CarryingCapacity 已被 FAOC_OmniWorkProxyBoost 提升到千万级，
    /// 单趟仍然只能搬一个堆叠（钢铁 75）。此处仅对代理跳过 stackLimit 封顶，
    /// 让容量回到 CarryingCapacity / VolumePerUnit。
    /// 实际取用量仍会与 job.count、源堆数量、目标剩余空间三者取小；
    /// 放下时原版 GenPlace 会按 stackLimit 自动拆分，不需要额外处理。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.MaxStackSpaceEver), new Type[] { typeof(ThingDef) })]
    public static class Patch_OmniWorkProxy_CarryStackLimit
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_CarryTracker __instance, ThingDef td, ref int __result)
        {
            if (td == null || __instance == null || !OmniWorkProxyUtility.IsProxy(__instance.pawn)) return;
            float volumePerUnit = td.VolumePerUnit;
            if (volumePerUnit <= 0f) return;
            int capacity = Mathf.RoundToInt(__instance.pawn.GetStatValue(StatDefOf.CarryingCapacity) / volumePerUnit);
            if (capacity > __result) __result = capacity;
        }
    }

    /// <summary>
    /// 代理的负重上限。
    /// 1.6 的 MassUtility.Capacity 恒为 BodySize × 35，既不读 CarryingCapacity 也不读任何 stat，
    /// 因此 FAOC_OmniWorkProxyBoost 里再高的 CarryingCapacity 也影响不到走 MassUtility 的路径
    /// （拾取到背包、装车、驮运、超重减速、商队与装备界面显示）。
    /// 这里让代理的负重直接跟随 CarryingCapacity，与上面的单趟搬运量补丁同一来源，
    /// 避免出现"能拿多少"和"能背多重"两套互不相干的数字。
    /// 非代理立即短路；MassUtility.Capacity 调用点较多（含 UI 绘制），代价仅一次引用比较。
    /// </summary>
    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    public static class Patch_OmniWorkProxy_MassCapacity
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn p, ref float __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(p)) return;
            __result = p.GetStatValue(StatDefOf.CarryingCapacity);
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

    /// <summary>
    /// 空闲代理完全跳过 Pawn Tick；被调度到工作后才恢复 JobDriver 等必要更新。
    /// 另外，只要代理手上还拿着非等待 Job，就一律放行 Tick —— JobDriver 的延迟 toil、
    /// 进度条与寻路全都靠 Pawn.Tick 推进。若调度状态短暂不同步导致"有真实 Job 却被
    /// 冻结"，Job 将永远无法结束，表现为代理攥着进度条站在原地一动不动。
    /// 驻外代理（当前不在归属图）同样一律放行：它需要跑完工作或回程 job。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), "Tick")]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_FreezeWhenInactive
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return true;
            if (OmniWorkProxyUtility.IsActive(__instance)) return true;
            // 驻外代理必须保持可 Tick，否则连回程 job 都无法推进。
            Map home = GameComponent_OmniWorkProxyRegistry.HomeMapOf(__instance);
            if (__instance.Spawned && home != null && __instance.Map != home) return true;
            // 兜底：非活跃但仍在跑真实 Job 属于状态不一致，放行 Tick 让它正常收尾。
            return __instance.CurJob != null &&
                   !MapComponent_OmniWorkstation.IsIdleJob(__instance.CurJob);
        }
    }

    /// <summary>
    /// 原版 EndCurrentJob 会同步寻找下一项工作。真实 Job 继续留场并更新记录；
    /// 原版最终落入等待状态时，通知生命周期调度器返回工作站并进入空闲宽限期。
    /// 唯一例外是原版自己的 1 tick 接力缓冲 Wait，它不是空闲，必须放行让代理原地接续。
    /// 所有管理动作都以**归属图**的组件为准：跨图工作时若用代理当前所在图的组件，
    /// 记录查不到，代理就会变成无主状态。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    [HarmonyPriority(Priority.First)]
    public static class Patch_OmniWorkProxy_DeactivateOnJobEnd
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn ___pawn, JobCondition condition)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn) ||
                OmniWorkProxyUtility.IsManagedTransition(___pawn)) return;
            if (!___pawn.Spawned) return;
            MapComponent_OmniWorkstation manager = GameComponent_OmniWorkProxyRegistry.ManagerOf(___pawn);
            if (manager == null) return;

            // 驻外：代理当前图不是归属图，一律交给驻外看护处理，
            // 不能在这里按"空闲"把它拽回，否则回程 job 会在半途被自己打断。
            if (manager.OwningMap != ___pawn.Map) return;

            // 原版工作成功后插入的 1 tick Wait_MaintainPosture 是接力缓冲：它下一 tick 结束时
            // 原版会自行 TryFindAndStartJob，代理原地接续下一项工作。若在此按空闲回收，代理
            // 会被立即拽回工作站并进入宽限等待，表现为"做完一件就回站、其余时间都在等"。
            if (manager.TryTrackVanillaRelayWait(___pawn)) return;

            Job current = ___pawn.CurJob;
            if (!MapComponent_OmniWorkstation.IsIdleState(___pawn))
            {
                OmniWorkProxyUtility.SetActive(___pawn, true);
                manager.NotifyProxyStartedJob(___pawn, current);
                return;
            }

            manager.NotifyProxyBecameIdle(___pawn, condition);
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
        public static void Postfix(Pawn ___pawn, Job newJob)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return;
            if (!___pawn.Spawned) return;
            MapComponent_OmniWorkstation manager = GameComponent_OmniWorkProxyRegistry.ManagerOf(___pawn)
                ?? ___pawn.Map.GetComponent<MapComponent_OmniWorkstation>();
            if (manager == null) return;
            // 代理不穿戴、不装备：这类 Job 一启动就取消，代理不会握着无法完成的
            // 穿戴进度条停在原地，而是立刻入舱等调度器重新指派正常工作。
            if (MapComponent_OmniWorkstation.IsForbiddenGearJob(newJob))
            {
                manager.CancelForbiddenGearJob(___pawn);
                return;
            }
            // 驻外工作时不能以"代理当前所在图"为准，否则记录会写进别的图、归属池看不到。
            if (manager.OwningMap != ___pawn.Map) return;
            if (MapComponent_OmniWorkstation.IsIdleState(___pawn)) return;
            manager.NotifyProxyStartedJob(___pawn, ___pawn.CurJob);
        }
    }

    /// <summary>
    /// 代理出现在（或被搬到）某张图：更新全局归属表的"驻外"状态。
    /// 所有标准换图路径（DeSpawn + GenSpawn.Spawn）都会经过这里，
    /// 因此第三方 Mod 把代理搬到哪张图都能被无差别观测到。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Patch_OmniWorkProxy_TrackMapChange
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, Map map)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return;
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry == null) return;
            registry.NotifySpawned(__instance, map);
            registry.VerifyMirror(__instance);
        }
    }

    /// <summary>
    /// 代理离开地图：区分"我方送入休眠舱"与"被第三方容器（车辆 / 门户 / 平台）接住"，
    /// 后者需要在超时后强制收回归属池。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    public static class Patch_OmniWorkProxy_TrackDeSpawn
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance)
        {
            if (!OmniWorkProxyUtility.IsProxy(__instance)) return;
            GameComponent_OmniWorkProxyRegistry registry = GameComponent_OmniWorkProxyRegistry.Instance;
            if (registry == null) return;
            registry.NotifyDespawned(__instance);
            // 校验回写：入舱 / 被容器接住是归属最容易与镜像不一致的时刻，以权威表为准修正。
            registry.VerifyMirror(__instance);
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

    /// <summary>
    /// 入口授权（R-10）：代理只在"归属池有权进入的图"上接受工作查询。
    ///
    /// MultiFloors 会先把 pawn 瞬移到候选层、再递归重跑同一个 TryIssueJobPackage，因此那一刻
    /// `pawn.Map` 就是候选图 —— 这里只需比较它是否等于归属图即可覆盖它，无需识别 Mod 身份。
    /// 只约束"去"，不约束"回"：回程由驻外看护的空闲/超时传送回收兜底，否则代理会被困在别的图。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    public static class Patch_OmniWorkProxy_EntryAuthorization
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref ThinkResult __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            Map queryMap = pawn.Map;
            if (queryMap == null) return true;

            Map homeMap = GameComponent_OmniWorkProxyRegistry.HomeMapOf(pawn);
            if (homeMap == null || homeMap == queryMap) return true;

            if (OmniWorkProxyEntryAuthorization.IsAuthorized(homeMap, queryMap)) return true;

            if (OmniWorkProxyEntryAuthorization.StrictMode)
            {
                __result = ThinkResult.NoJob;
                return false;
            }
            // 仅警告模式：放行但留日志，便于诊断"代理为何跑到了不该去的图"。
            OmniWorkProxyEntryAuthorization.NotifyUnauthorized(homeMap, queryMap, pawn);
            return true;
        }
    }

    /// <summary>范围以工作站为圆心，不随代理取料位置漂移。</summary>
    [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThing_Global_Reachable))]
    public static class Patch_OmniWorkProxy_GlobalSearchFilter
    {
        [HarmonyPrefix]
        public static void Prefix(TraverseParms traverseParams, ref Predicate<Thing> validator)
        {
            Pawn pawn = traverseParams.pawn;
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return;
            Predicate<Thing> original = validator;
            OmniWorkProxyUtility.TryGetStation(pawn, out Building_OmniWorkstation station);
            OmniWorkFailureCache failures = OmniWorkFailureCache.For(pawn);
            validator = thing => thing != null &&
                (station == null || station.CoversOn(pawn.Map, thing.PositionHeld)) &&
                (failures == null || failures.Allows(pawn, null, thing)) &&
                (original == null || original(thing));
        }
    }

    /// <summary>直接搜索全局候选，彻底绕开普通区域遍历及其提前退出条件。</summary>
    [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable))]
    public static class Patch_OmniWorkProxy_ForceGlobalSearch
    {
        [HarmonyPrefix]
        public static bool Prefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode,
            TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator,
            IEnumerable<Thing> customGlobalSearchSet, bool lookInHaulSources, ref Thing __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(traverseParams.pawn)) return true;
            __result = map == null || thingReq.IsUndefined && customGlobalSearchSet == null
                ? null : GenClosest.ClosestThing_Global_Reachable(root, map,
                customGlobalSearchSet ?? map.listerThings.ThingsMatching(thingReq), peMode,
                traverseParams, maxDistance, validator, canLookInHaulableSources: lookInHaulSources);
            return false;
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
    /// 代理出舱工作与等待期间必须保持 mindState.Active(否则 Pawn_JobTracker 不会为它进入原版
    /// 思考树),而 Active 的 setter 会经 MapPawns.UpdateRegistryForPawn 重新把代理登记进
    /// pawnsSpawned。登记后 mapPawns.FreeColonists 会包含代理，于是殖民者头像栏(ColonistBar
    /// 读的就是 FreeColonists)和工作设置栏(MainTabWindow_PawnTable.Pawns 读同一列表)都会把
    /// 代理当成殖民者列出。这里在原版任何注册路径(生成、SetFaction、Active 变化等)之后立刻
    /// 撤销登记，与唤醒路径上的 DeRegisterPawn 共同保证代理只存在于代理池中。
    /// </summary>
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.RegisterPawn))]
    public static class Patch_OmniWorkProxy_KeepOutOfPawnRegistry
    {
        [HarmonyPostfix]
        public static void Postfix(MapPawns __instance, Pawn p)
        {
            if (OmniWorkProxyUtility.IsProxy(p))
                __instance.DeRegisterPawn(p);
        }
    }

    /// <summary>
    /// 将携带 Pawn 的网格查询接入代理网格；导航执行和终点语义集中在 OmniWorkProxyNavigation。
    /// </summary>
    public static class Patch_OmniWorkProxy_PathGrid
    {
        /// <summary>
        /// Pawn.GetPathContext：代理统一取自定义网格。必须优先于原版 Flying 分支，否则会落到
        /// 原版飞行网格（它仍受地形与建筑约束）。
        /// </summary>
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetPathContext))]
        public static class Patch_Pawn_GetPathContext
        {
            [HarmonyPrefix]
            public static bool Prefix(Pawn __instance, Pathing pathing, ref PathingContext __result)
            {
                if (!OmniWorkProxyUtility.IsProxy(__instance) ||
                    OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid == null) return true;
                __result = pathing.Get(OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid);
                return false;
            }
        }

        /// <summary>Pathing.For(TraverseParms)：覆盖 Reachability/WorkGiver 等经 TraverseParms 取网格的调用点。</summary>
        [HarmonyPatch(typeof(Pathing), nameof(Pathing.For), new Type[] { typeof(TraverseParms) })]
        public static class Patch_Pathing_For_TraverseParms
        {
            [HarmonyPrefix]
            public static bool Prefix(Pathing __instance, TraverseParms parms, ref PathingContext __result)
            {
                Pawn pawn = parms.pawn;
                if (pawn == null || !OmniWorkProxyUtility.IsProxy(pawn) ||
                    OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid == null) return true;
                __result = __instance.Get(OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid);
                return false;
            }
        }

        /// <summary>不因建筑挡路触发破墙 Job。</summary>
        [HarmonyPatch(typeof(Pawn_PathFollower), "BuildingBlockingNextPathCell")]
        public static class Patch_BuildingBlockingNextPathCell
        {
            [HarmonyPostfix]
            public static void Postfix(ref Building __result, Pawn ___pawn)
            {
                if (OmniWorkProxyUtility.IsProxy(___pawn)) __result = null;
            }
        }

        /// <summary>不等门、不手动开门。</summary>
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.NextCellDoorToWaitForOrManuallyOpen))]
        public static class Patch_NextCellDoorToWaitForOrManuallyOpen
        {
            [HarmonyPostfix]
            public static void Postfix(ref Building_Door __result, Pawn ___pawn)
            {
                if (OmniWorkProxyUtility.IsProxy(___pawn)) __result = null;
            }
        }

        /// <summary>代理可占据任意格子（含墙内、山体、深水）。</summary>
        [HarmonyPatch(typeof(Pawn_PathFollower), "PawnCanOccupy")]
        public static class Patch_PawnCanOccupy
        {
            [HarmonyPostfix]
            public static void Postfix(IntVec3 c, ref bool __result, Pawn ___pawn)
            {
                if (OmniWorkProxyUtility.IsProxy(___pawn))
                    __result = OmniWorkProxyNavigation.Walkable(___pawn.Map, c);
            }
        }

        /// <summary>可达性与执行共享终点规则，不以普通 Region 判断代理的连通性。</summary>
        [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
            new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
        public static class Patch_Reachability_CanReach
        {
            [HarmonyPrefix]
            public static bool Prefix(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode,
                TraverseParms traverseParams, ref bool __result, Map ___map)
            {
                Pawn pawn = traverseParams.pawn;
                if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
                __result = OmniWorkProxyNavigation.TryFindEnd(pawn, ___map, start, dest, peMode, out _);
                return false;
            }
        }

        /// <summary>可站立判定放宽，避免代理落在网格允许但原版判定为不可站的格上。</summary>
        [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.StandableBy))]
        public static class Patch_GenGrid_StandableBy
        {
            [HarmonyPostfix]
            public static void Postfix(IntVec3 c, Map map, Pawn pawn, ref bool __result)
            {
                if (OmniWorkProxyUtility.IsProxy(pawn))
                    __result = OmniWorkProxyNavigation.Walkable(map, c);
            }
        }

        /// <summary>代理视为飞行，与所用网格的 flying 语义一致。</summary>
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.Flying), MethodType.Getter)]
        public static class Patch_Pawn_Flying
        {
            [HarmonyPostfix]
            public static void Postfix(Pawn __instance, ref bool __result)
            {
                if (OmniWorkProxyUtility.IsProxy(__instance)) __result = true;
            }
        }
    }

    /// <summary>
    /// 代理不需要任何需求，包括第三方 Mod 通过 NeedDef 追加的需求。
    /// 原版与第三方都只能经由 Pawn_NeedsTracker.AddOrRemoveNeedsAsAppropriate 增删需求，
    /// 这里对代理直接短路并清空 AllNeeds 与 MiscNeeds，使生成、出舱、读档以及第三方
    /// 主动调用后都保持零需求。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.AddOrRemoveNeedsAsAppropriate))]
    public static class Patch_OmniWorkProxy_RemoveAllNeeds
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_NeedsTracker __instance, Pawn ___pawn)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn)) return true;
            if (__instance.AllNeeds.Count > 0)
            {
                // 不调用 Mod Need 的回调，避免其在清理阶段重新注入状态。
                __instance.AllNeeds.Clear();
                __instance.MiscNeeds.Clear();
                __instance.BindDirectNeedFields();
            }
            return false;
        }
    }

    /// <summary>
    /// 代理不做任何娱乐行为。原版全部娱乐 Job 都由 JobGiver_GetJoy 及其子类
    /// (JobGiver_IdleJoy / JobGiver_GetJoyInBed) 产生，子类在自身条件判断之后都会回落到
    /// 基类的 TryGiveJob，因此只需在基类入口对代理返回 null，就能一次性封死原版与全部
    /// 第三方 JoyGiver（例如 ColonyFitness 训练站的娱乐 Job）。基类自身不校验
    /// needs.joy 是否存在（只依赖上游 ThinkNode_Priority_GetJoy 的 null 检查），
    /// 在这里拦截同时消除了"零需求代理被娱乐链解引用"的隐患。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_GetJoy), "TryGiveJob", new Type[] { typeof(Pawn) })]
    public static class Patch_OmniWorkProxy_NoJoyJob
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// 代理没有"出装"概念：生成阶段原本会依 PawnKindDef 造出的初始服装、库存与武器整段跳过，
    /// 代理因此不会带着殖民者的补给生成，也不会把库存里的服装顺手穿到自己身上。
    /// </summary>
    [HarmonyPatch(typeof(PawnGenerator), "GenerateGearFor")]
    public static class Patch_OmniWorkProxy_NoStartingGear
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn)
        {
            return !OmniWorkProxyUtility.IsProxy(pawn);
        }
    }

    /// <summary>
    /// 代理永远不穿服装：任何来源（原版 OptimizeApparel、第三方换装、玩家手动操作）的
    /// 穿戴请求都在此处被拒绝，服装留在原处，不会被代理"穿走"。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Wear))]
    public static class Patch_OmniWorkProxy_NoWearApparel
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_ApparelTracker __instance)
        {
            return !OmniWorkProxyUtility.IsProxy(__instance.pawn);
        }
    }

    /// <summary>代理不装备任何武器或工具：装备请求一律拒绝。</summary>
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment))]
    public static class Patch_OmniWorkProxy_NoEquipWeapon
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_EquipmentTracker __instance)
        {
            return !OmniWorkProxyUtility.IsProxy(__instance.pawn);
        }
    }

    /// <summary>
    /// 代理不参与着装优化：既不为了更好的服装去搬运衣服，也不会因"穿着不合着装政策"去脱衣服。
    /// 这是"服装被不需要穿着的代理穿走"的源头思考节点。
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob", new Type[] { typeof(Pawn) })]
    public static class Patch_OmniWorkProxy_NoApparelOptimizeJob
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>代理不主动拾取武器或工具来装备自己。</summary>
    [HarmonyPatch(typeof(JobGiver_PickUpOpportunisticWeapon), "TryGiveJob", new Type[] { typeof(Pawn) })]
    public static class Patch_OmniWorkProxy_NoOpportunisticWeaponJob
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// 代理的最大血量固定为 999999。
    /// 原版部位血量恒为 CeilToInt(部位 hitPoints × HealthScale)（BodyPartDef.GetMaxHealth），
    /// 而人类全身部位 hitPoints 之和为 100，因此把 HealthScale 定为 999999 / 100 即可让
    /// 满血总量落在 999999。非代理立即短路，代价仅一次引用比较。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.HealthScale), MethodType.Getter)]
    public static class Patch_OmniWorkProxy_MaxHitPoints
    {
        private const float TargetMaxHitPoints = 999999f;
        private const float HumanTotalBodyHitPoints = 100f;

        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, ref float __result)
        {
            if (OmniWorkProxyUtility.IsProxy(__instance))
                __result = TargetMaxHitPoints / HumanTotalBodyHitPoints;
        }
    }

    /// <summary>
    /// 代理的舒适温度范围固定为 -10000 ~ 10000。
    /// 原版 SafeTemperatureRange（决定低温症/中暑是否发展）只是在此基础上外扩 10 度，
    /// 因此这一处覆盖即可让代理在任何温度下都不产生温度相关伤害。
    /// </summary>
    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.ComfortableTemperatureRange),
        new Type[] { typeof(Pawn) })]
    public static class Patch_OmniWorkProxy_TemperatureRange
    {
        private const float MinComfortableTemperature = -10000f;
        private const float MaxComfortableTemperature = 10000f;

        [HarmonyPrefix]
        public static bool Prefix(Pawn p, ref FloatRange __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(p)) return true;
            __result = new FloatRange(MinComfortableTemperature, MaxComfortableTemperature);
            return false;
        }
    }

    /// <summary>
    /// 代理受攻击时不受任何伤害。原版 Thing.TakeDamage 先调用 PreApplyDamage，
    /// 一旦 absorbed 为 true 就直接返回，不再生成伤口、流血或部位损伤。
    /// 这是 Pawn 伤害管线唯一的入口，因此一处吸收即可覆盖原版与第三方的全部攻击来源。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.PreApplyDamage))]
    public static class Patch_OmniWorkProxy_AbsorbAllDamage
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn, out bool absorbed)
        {
            if (!OmniWorkProxyUtility.IsProxy(___pawn))
            {
                // 非代理交回原版；absorbed 由原版自行判定。
                absorbed = false;
                return true;
            }
            absorbed = true;
            return false;
        }
    }
}
