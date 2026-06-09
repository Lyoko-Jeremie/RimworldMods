using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    public enum OmniForceFieldDomeGlowMode
    {
        None,
        Lamp,
        SunLamp
    }

    public enum OmniForceFieldDomeGrowthMode
    {
        Normal,
        Forced,
        Stopped,
        Cleared
    }

    [StaticConstructorOnStartup]
    public static class OmniForceFieldDomeTex
    {
        public static readonly Texture2D IconGlowMode =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniForceFieldDome_GlowMode", false)
            ?? TexCommand.DesirePower
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconGrowthMode =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniForceFieldDome_GrowthMode", false)
            ?? ContentFinder<Texture2D>.Get("UI/Designators/Harvest", false)
            ?? BaseContent.WhiteTex;
    }

    /// <summary>
    /// 穹顶配置。继承矩形护盾配置，是为了直接复用宽高设置、绘制、投射物拦截和
    /// OmniInterceptorTracker 的保护区域缓存。
    /// </summary>
    public class CompProperties_OmniForceFieldDome : CompProperties_CompOmniRectangleProjectileInterceptor
    {
        // 能量屋顶：真实写入 RoofGrid，让游戏把穹顶格视为有屋顶；
        // 透光和防塌方行为在下方 Harmony 补丁中修正。
        public bool applyRoof = false;
        public RoofDef roofDef;
        public bool supportRoof = true;
        public float roofSupportRadius = -1f;
        public bool allowPlantsUnderRoof = true;
        public bool allowSkyLightThroughRoof = true;

        // 通过给穹顶外圈格赋予自定义 RegionType，把穹顶内外拆成不同 Room。
        public bool formRoom = true;

        // 温度/真空通过查询补丁处理，避免把室外大房间本身整体改温。
        public bool maintainTemperature = true;
        public float targetTemperature = 21f;

        // 周期性环境清理项。它们只作用于穹顶缓存格，不扫描整张地图。
        public bool blockVacuum = true;
        public bool clearGas = true;
        public bool clearPollution = true;
        public bool cleanFilth = true;
        public bool extinguishFire = true;

        // 默认复用状态分配终端已有的“OmniForceFieldDome_Protect”hediff；XML 可直接传 HediffDef 或 defName。
        public bool applyFriendlyHediff = true;
        public HediffDef friendlyHediffDef;
        public string friendlyHediffDefName = "OmniForceFieldDome_Protect";
        public bool removeFriendlyHediffWhenLeaving = true;

        // 大区域穹顶可能覆盖很多格，维护逻辑按间隔执行以控制开销。
        public int environmentTickInterval = 250;
        public int pawnTickInterval = 60;

        public CompProperties_OmniForceFieldDome()
        {
            compClass = typeof(CompOmniForceFieldDome);
            isStatic = true;
        }

        public RoofDef RoofDefToUse => roofDef ?? RoofDefOf.RoofConstructed;

        /// <summary>
        /// 延迟解析 hediff，避免 DefDatabase 尚未完全加载时过早取值。
        /// </summary>
        public HediffDef FriendlyHediffDefToUse
        {
            get
            {
                if (friendlyHediffDef != null)
                {
                    return friendlyHediffDef;
                }
                if (!friendlyHediffDefName.NullOrEmpty())
                {
                    friendlyHediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(friendlyHediffDefName);
                }
                return friendlyHediffDef;
            }
        }
    }

    /// <summary>
    /// 一种力场穹顶建筑，用于在荒野或太空环境下建立一个方形安全区域。
    /// 投射物、爆炸、轨道攻击、空投和敌方寻路/攻击目标保护复用 OmniProjectileInterceptor 体系。
    /// 环境层效果由 OmniForceFieldDomeNetworkManager 按合并后的穹顶网络处理。
    /// </summary>
    public class CompOmniForceFieldDome : CompOmniRectangleProjectileInterceptor
    {
        public new CompProperties_OmniForceFieldDome Props => (CompProperties_OmniForceFieldDome)props;

        // 当前穹顶实际覆盖矩形。网络合并和环境维护都以这个矩形为准。
        public CellRect DomeRect => OccupiedRect;

        // 玩家通过 Gizmo 改宽高、或开关护盾时，矩形缓存需要失效。
        private float cachedNetworkWidth = -1f;
        private float cachedNetworkHeight = -1f;
        private bool cachedNetworkActive;
        private OmniForceFieldDomeGlowMode cachedGlowMode = OmniForceFieldDomeGlowMode.None;
        private OmniForceFieldDomeGlowMode glowMode = OmniForceFieldDomeGlowMode.None;
        private OmniForceFieldDomeGrowthMode growthMode = OmniForceFieldDomeGrowthMode.Normal;

        public OmniForceFieldDomeGlowMode GlowMode => glowMode;
        public OmniForceFieldDomeGrowthMode GrowthMode => growthMode;

        public float RoofSupportRadius
        {
            get
            {
                if (Props.roofSupportRadius > 0f)
                {
                    return Props.roofSupportRadius;
                }
                return Mathf.Sqrt(Width * Width + Height * Height);
            }
        }

        public bool SupportsRoofAt(IntVec3 c)
        {
            return Props.supportRoof
                   && Active
                   && parent.Spawned
                   && parent.Map != null
                   && c.InBounds(parent.Map)
                   && c.InHorDistOf(parent.Position, RoofSupportRadius);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map.GetComponent<OmniForceFieldDomeNetworkManager>()?.Register(this);
            DirtyGlowInDomeArea(parent.Map);
            RememberNetworkState();
        }

        public override void PostMapInit()
        {
            base.PostMapInit();
            parent.Map?.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
            DirtyGlowInDomeArea(parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            DirtyGlowInDomeArea(map);
            map.GetComponent<OmniForceFieldDomeNetworkManager>()?.Deregister(this);
            base.PostDeSpawn(map, mode);
        }

        public override void SetRadius(float newRadius)
        {
            DirtyGlowInDomeArea(parent.Map);
            base.SetRadius(newRadius);
            parent.Map?.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
            DirtyGlowInDomeArea(parent.Map);
            RememberNetworkState();
        }

        public new void SetSize(float width, float height)
        {
            DirtyGlowInDomeArea(parent.Map);
            base.SetSize(width, height);
            parent.Map?.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
            DirtyGlowInDomeArea(parent.Map);
            RememberNetworkState();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref glowMode, "omniForceFieldDomeGlowMode", OmniForceFieldDomeGlowMode.None);
            Scribe_Values.Look(ref growthMode, "omniForceFieldDomeGrowthMode", OmniForceFieldDomeGrowthMode.Normal);
        }

        public override void CompTick()
        {
            base.CompTick();
            DirtyNetworkIfStateChanged();
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            DirtyNetworkIfStateChanged();
        }

        public override string CompInspectStringExtra()
        {
            string str = base.CompInspectStringExtra();
            if (parent.Spawned)
            {
                OmniForceFieldDomeNetwork network;
                if (parent.Map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetNetworkAt(parent.Position, out network))
                {
                    if (!str.NullOrEmpty())
                    {
                        str += "\n";
                    }
                    str += "OmniForceFieldDome_NetworkCells".Translate(network.CellCount, network.Domes.Count);
                }
            }
            return str;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            Command_Action buildRoof = new Command_Action
            {
                defaultLabel = TranslateOrFallback("OmniForceFieldDome_BuildRoof", "Build dome roof"),
                defaultDesc = TranslateOrFallback("OmniForceFieldDome_BuildRoofDesc",
                    "Build the selected roof type across this dome generator's covered area."),
                icon = CompAutoRooferTex.IconBuildRoof,
                action = ShowRoofBuildMenu
            };

            if (!AnySelectedRoofDomeActive())
            {
                buildRoof.Disable(TranslateOrFallback("OmniForceFieldDome_Disabled", "The dome is disabled."));
            }

            yield return buildRoof;

            yield return new Command_Action
            {
                defaultLabel = string.Format(
                    TranslateOrFallback("OmniForceFieldDome_GlowMode", "Dome light: {0}"),
                    GlowModeLabel(glowMode)),
                defaultDesc = TranslateOrFallback("OmniForceFieldDome_GlowModeDesc",
                    "Choose whether this dome generator lights its covered area like a lamp, like a sun lamp, or stays dark."),
                icon = OmniForceFieldDomeTex.IconGlowMode,
                action = ShowGlowModeMenu
            };

            yield return new Command_Action
            {
                defaultLabel = string.Format(
                    TranslateOrFallback("OmniForceFieldDome_GrowthMode", "Plant growth: {0}"),
                    GrowthModeLabel(growthMode)),
                defaultDesc = TranslateOrFallback("OmniForceFieldDome_GrowthModeDesc",
                    "Choose whether plants in the dome area grow normally, are forced to maturity, stop growing, or are cleared."),
                icon = OmniForceFieldDomeTex.IconGrowthMode,
                action = ShowGrowthModeMenu
            };
        }

        private void ShowGlowModeMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            AddGlowModeOption(options, OmniForceFieldDomeGlowMode.None);
            AddGlowModeOption(options, OmniForceFieldDomeGlowMode.Lamp);
            AddGlowModeOption(options, OmniForceFieldDomeGlowMode.SunLamp);

            Find.WindowStack.Add(new FloatMenu(options,
                TranslateOrFallback("OmniForceFieldDome_SelectGlowMode", "Select dome light mode")));
        }

        private void ShowGrowthModeMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            AddGrowthModeOption(options, OmniForceFieldDomeGrowthMode.Normal);
            AddGrowthModeOption(options, OmniForceFieldDomeGrowthMode.Forced);
            AddGrowthModeOption(options, OmniForceFieldDomeGrowthMode.Stopped);
            AddGrowthModeOption(options, OmniForceFieldDomeGrowthMode.Cleared);

            Find.WindowStack.Add(new FloatMenu(options,
                TranslateOrFallback("OmniForceFieldDome_SelectGrowthMode", "Select plant growth mode")));
        }

        private void AddGrowthModeOption(List<FloatMenuOption> options, OmniForceFieldDomeGrowthMode mode)
        {
            OmniForceFieldDomeGrowthMode selectedMode = mode;
            string label = GrowthModeLabel(selectedMode);
            if (growthMode == selectedMode)
            {
                label += " " + TranslateOrFallback("OmniForceFieldDome_CurrentMode", "(current)");
            }

            options.Add(new FloatMenuOption(label, () => SetGrowthModeForSelectedDomeAreas(selectedMode)));
        }

        private void SetGrowthModeForSelectedDomeAreas(OmniForceFieldDomeGrowthMode mode)
        {
            List<CompOmniForceFieldDome> domes = SelectedDomeComps();
            for (int i = 0; i < domes.Count; i++)
            {
                domes[i].SetGrowthMode(mode);
            }
        }

        private void SetGrowthMode(OmniForceFieldDomeGrowthMode mode)
        {
            if (growthMode == mode)
            {
                return;
            }

            growthMode = mode;
        }

        private string GrowthModeLabel(OmniForceFieldDomeGrowthMode mode)
        {
            return TranslateOrFallback("Biosphere_GrowthMode_" + mode.ToString(), mode.ToString());
        }

        private void AddGlowModeOption(List<FloatMenuOption> options, OmniForceFieldDomeGlowMode mode)
        {
            OmniForceFieldDomeGlowMode selectedMode = mode;
            string label = GlowModeLabel(selectedMode);
            if (glowMode == selectedMode)
            {
                label += " " + TranslateOrFallback("OmniForceFieldDome_CurrentMode", "(current)");
            }
            options.Add(new FloatMenuOption(label, () => SetGlowModeForSelectedDomeAreas(selectedMode)));
        }

        private void SetGlowModeForSelectedDomeAreas(OmniForceFieldDomeGlowMode mode)
        {
            List<CompOmniForceFieldDome> domes = SelectedDomeComps();
            for (int i = 0; i < domes.Count; i++)
            {
                domes[i].SetGlowMode(mode);
            }
        }

        private void SetGlowMode(OmniForceFieldDomeGlowMode mode)
        {
            if (glowMode == mode)
            {
                return;
            }

            DirtyGlowInDomeArea(parent.Map);
            glowMode = mode;
            cachedGlowMode = glowMode;
            DirtyGlowInDomeArea(parent.Map);
        }

        private static string GlowModeLabel(OmniForceFieldDomeGlowMode mode)
        {
            switch (mode)
            {
                case OmniForceFieldDomeGlowMode.Lamp:
                    return TranslateOrFallback("OmniForceFieldDome_GlowModeLamp", "Lamp mode");
                case OmniForceFieldDomeGlowMode.SunLamp:
                    return TranslateOrFallback("OmniForceFieldDome_GlowModeSunLamp", "Sun lamp mode");
                default:
                    return TranslateOrFallback("OmniForceFieldDome_GlowModeNone", "No light");
            }
        }

        private void ShowRoofBuildMenu()
        {
            List<RoofDef> roofDefs = DefDatabase<RoofDef>.AllDefsListForReading
                .Where(IsSelectableRoofDef)
                .OrderBy(def => def == Props.RoofDefToUse ? 0 : 1)
                .ThenBy(def => RoofLabel(def))
                .ToList();

            if (!roofDefs.Contains(Props.RoofDefToUse))
            {
                roofDefs.Insert(0, Props.RoofDefToUse);
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            options.Add(new FloatMenuOption(
                TranslateOrFallback("OmniForceFieldDome_RemoveRoof", "Remove roof"),
                RemoveRoofInSelectedDomeAreas));

            for (int i = 0; i < roofDefs.Count; i++)
            {
                RoofDef roof = roofDefs[i];
                string label = RoofMenuLabel(roof);
                options.Add(new FloatMenuOption(label, () => BuildRoofInSelectedDomeAreas(roof)));
            }

            Find.WindowStack.Add(new FloatMenu(options,
                TranslateOrFallback("OmniForceFieldDome_SelectRoof", "Select roof type")));
        }

        private bool IsSelectableRoofDef(RoofDef roof)
        {
            return roof != null && (!roof.isNatural || roof == Props.RoofDefToUse);
        }

        private void BuildRoofInSelectedDomeAreas(RoofDef roof)
        {
            if (roof == null)
            {
                return;
            }

            int builtCount = 0;
            int replacedCount = 0;
            int skippedNaturalCount = 0;

            List<CompOmniForceFieldDome> domes = SelectedActiveRoofDomes();
            for (int i = 0; i < domes.Count; i++)
            {
                RoofBuildResult result = domes[i].BuildRoofInDomeArea(roof);
                builtCount += result.builtCount;
                replacedCount += result.replacedCount;
                skippedNaturalCount += result.skippedNaturalCount;
            }

            if (domes.Count == 0)
            {
                return;
            }

            Messages.Message(string.Format(
                    TranslateOrFallback("OmniForceFieldDome_BuildRoofComplete",
                        "Dome roofing complete: built {0}, replaced {1}, skipped natural roofs {2}. Roof: {3}."),
                    builtCount,
                    replacedCount,
                    skippedNaturalCount,
                    RoofLabel(roof)),
                parent,
                MessageTypeDefOf.PositiveEvent);
        }

        private RoofBuildResult BuildRoofInDomeArea(RoofDef roof)
        {
            Map map = parent.Map;
            if (map == null || roof == null)
            {
                return default(RoofBuildResult);
            }

            RoofBuildResult result = new RoofBuildResult();
            CellRect rect = DomeRect;

            foreach (IntVec3 c in rect.Cells)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }

                RoofDef currentRoof = map.roofGrid.RoofAt(c);
                if (currentRoof == roof)
                {
                    continue;
                }

                if (currentRoof != null && currentRoof.isNatural)
                {
                    result.skippedNaturalCount++;
                    continue;
                }

                map.roofGrid.SetRoof(c, roof);
                if (currentRoof == null)
                {
                    result.builtCount++;
                }
                else
                {
                    result.replacedCount++;
                }
            }

            return result;
        }

        private void RemoveRoofInSelectedDomeAreas()
        {
            int removedCount = 0;

            List<CompOmniForceFieldDome> domes = SelectedActiveRoofDomes();
            for (int i = 0; i < domes.Count; i++)
            {
                removedCount += domes[i].RemoveRoofInDomeArea();
            }

            if (domes.Count == 0)
            {
                return;
            }

            Messages.Message(string.Format(
                    TranslateOrFallback("OmniForceFieldDome_RemoveRoofComplete",
                        "Dome roof removal complete: removed {0}."),
                    removedCount),
                parent,
                MessageTypeDefOf.PositiveEvent);
        }

        private int RemoveRoofInDomeArea()
        {
            Map map = parent.Map;
            if (map == null)
            {
                return 0;
            }

            int removedCount = 0;
            CellRect rect = DomeRect;

            foreach (IntVec3 c in rect.Cells)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }

                RoofDef currentRoof = map.roofGrid.RoofAt(c);
                if (currentRoof == null)
                {
                    continue;
                }

                map.roofGrid.SetRoof(c, null);
                removedCount++;
            }

            return removedCount;
        }

        private bool AnySelectedRoofDomeActive()
        {
            return SelectedActiveRoofDomes().Count > 0;
        }

        private List<CompOmniForceFieldDome> SelectedActiveRoofDomes()
        {
            return SelectedDomeComps(true);
        }

        private List<CompOmniForceFieldDome> SelectedDomeComps(bool activeOnly = false)
        {
            List<CompOmniForceFieldDome> domes = new List<CompOmniForceFieldDome>();

            foreach (object obj in Find.Selector.SelectedObjects)
            {
                ThingWithComps thing = obj as ThingWithComps;
                CompOmniForceFieldDome dome = thing?.GetComp<CompOmniForceFieldDome>();
                if (dome != null && (!activeOnly || dome.Active) && !domes.Contains(dome))
                {
                    domes.Add(dome);
                }
            }

            if (domes.Count == 0 && (!activeOnly || Active))
            {
                domes.Add(this);
            }

            return domes;
        }

        private static string RoofMenuLabel(RoofDef roof)
        {
            string label = RoofLabel(roof);
            string modName = roof.modContentPack?.Name;
            if (modName.NullOrEmpty())
            {
                return label;
            }
            return label + " (" + modName + ")";
        }

        private static string RoofLabel(RoofDef roof)
        {
            if (roof == null)
            {
                return TranslateOrFallback("OmniForceFieldDome_UnknownRoof", "Unknown");
            }
            string label = roof.LabelCap;
            return label.NullOrEmpty() ? roof.defName : label;
        }

        private static string TranslateOrFallback(string key, string fallback)
        {
            return key.CanTranslate() ? (string)key.Translate() : fallback;
        }

        private struct RoofBuildResult
        {
            public int builtCount;
            public int replacedCount;
            public int skippedNaturalCount;
        }

        private void DirtyNetworkIfStateChanged()
        {
            if (!parent.Spawned || parent.Map == null)
            {
                return;
            }

            if (cachedNetworkActive != Active
                || !Mathf.Approximately(cachedNetworkWidth, Width)
                || !Mathf.Approximately(cachedNetworkHeight, Height)
                || cachedGlowMode != glowMode)
            {
                DirtyGlowInDomeArea(parent.Map);
                // 尺寸和启用状态会改变网络连通性、屋顶范围和保护格缓存。
                parent.Map.GetComponent<OmniForceFieldDomeNetworkManager>()?.DirtyCache();
                RememberNetworkState();
            }
        }

        private void RememberNetworkState()
        {
            cachedNetworkActive = Active;
            cachedNetworkWidth = Width;
            cachedNetworkHeight = Height;
            cachedGlowMode = glowMode;
        }

        private void DirtyGlowInDomeArea(Map map)
        {
            if (map?.glowGrid == null)
            {
                return;
            }

            CellRect rect = DomeRect;
            foreach (IntVec3 c in rect.Cells)
            {
                if (c.InBounds(map))
                {
                    map.glowGrid.DirtyCell(c);
                }
            }
        }
    }

    public sealed class OmniForceFieldDomeNetwork
    {
        // 一个 network 表示所有相邻或重叠穹顶合并后的连续区域。
        public readonly List<CompOmniForceFieldDome> Domes = new List<CompOmniForceFieldDome>();
        public readonly List<IntVec3> Cells = new List<IntVec3>();

        public int CellCount => Cells.Count;

        public CompOmniForceFieldDome PrimaryDome
        {
            get
            {
                // 多个穹顶配置不一致时，用第一个仍激活的穹顶作为网络配置来源。
                for (int i = 0; i < Domes.Count; i++)
                {
                    if (Domes[i]?.Active == true)
                    {
                        return Domes[i];
                    }
                }
                return Domes.Count > 0 ? Domes[0] : null;
            }
        }

        public CompProperties_OmniForceFieldDome PrimaryProps => PrimaryDome?.Props;
    }

    /// <summary>
    /// 负责合并所有相邻或重叠的力场穹顶，并对合并后的区域执行环境维护。
    /// </summary>
    public class OmniForceFieldDomeNetworkManager : MapComponent
    {
        // 178 = Normal(2) | 自定义高位标志。它是 passable，但不会被原版
        // ShouldBeInTheSameRoom 视为 Normal/Fence 的精确值，穹顶补丁会单独决定房间合并。
        public const RegionType DomeRoomRegionType = (RegionType)178;

        private readonly List<CompOmniForceFieldDome> domes = new List<CompOmniForceFieldDome>();
        private readonly List<OmniForceFieldDomeNetwork> networks = new List<OmniForceFieldDomeNetwork>();

        // 只移除本 manager 曾经授予的 hediff，避免误删玩家通过其他系统添加的同类状态。
        private readonly HashSet<Pawn> pawnsGrantedHediff = new HashSet<Pawn>();
        private readonly List<Pawn> tmpPawnsToRemove = new List<Pawn>();

        // 每个地图格到 network 的 O(1) 查询缓存；攻击、温度、真空等补丁会频繁查询它。
        private OmniForceFieldDomeNetwork[] networkCache;
        private bool[] roomCellCache;
        private bool cacheDirty = true;
        private bool regionsDirty = true;
        private const int SkyGlowDirtyTickInterval = 30;
        private const float SkyGlowDirtyThreshold = 1f / 255f;
        private float cachedSkyGlow = -1f;

        public OmniForceFieldDomeNetworkManager(Map map) : base(map)
        {
            networkCache = new OmniForceFieldDomeNetwork[map.cellIndices.NumGridCells];
            roomCellCache = new bool[map.cellIndices.NumGridCells];
        }

        public void Register(CompOmniForceFieldDome dome)
        {
            if (dome == null || domes.Contains(dome))
            {
                return;
            }
            domes.Add(dome);
            DirtyCache();
        }

        public void Deregister(CompOmniForceFieldDome dome)
        {
            if (domes.Remove(dome))
            {
                DirtyCache();
            }
        }

        public void DirtyCache()
        {
            // 延迟到下一次查询或 tick 重建，避免同一帧多次尺寸/开关变化反复重算。
            cacheDirty = true;
            regionsDirty = true;
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (cacheDirty)
            {
                RebuildNetworks();
            }
            if (regionsDirty)
            {
                RebuildRegionsAndRooms();
            }
            if (networks.Count == 0)
            {
                return;
            }

            if (map.IsHashIntervalTick(SkyGlowDirtyTickInterval))
            {
                DirtySkyLightDomeGlowIfNeeded();
            }
            if (map.IsHashIntervalTick(GetEnvironmentTickInterval()))
            {
                MaintainEnvironment();
            }
            if (map.IsHashIntervalTick(GetPawnTickInterval()))
            {
                MaintainPawnEffects();
            }
        }

        public bool IsDomeCell(IntVec3 c)
        {
            OmniForceFieldDomeNetwork network;
            return TryGetNetworkAt(c, out network);
        }

        public bool IsDomeRoomCell(IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            if (cacheDirty)
            {
                RebuildNetworks();
            }
            return roomCellCache[map.cellIndices.CellToIndex(c)];
        }

        public bool TryGetNetworkAt(IntVec3 c, out OmniForceFieldDomeNetwork network)
        {
            network = null;
            if (!c.InBounds(map))
            {
                return false;
            }
            if (cacheDirty)
            {
                RebuildNetworks();
            }
            network = networkCache[map.cellIndices.CellToIndex(c)];
            return network != null;
        }

        public bool TryGetPropsAt(IntVec3 c, out CompProperties_OmniForceFieldDome props)
        {
            props = null;
            OmniForceFieldDomeNetwork network;
            if (!TryGetNetworkAt(c, out network))
            {
                return false;
            }
            props = network.PrimaryProps;
            return props != null;
        }

        public bool TryGetDomeGlowAt(IntVec3 c, out float glow)
        {
            glow = 0f;
            OmniForceFieldDomeNetwork network;
            if (!TryGetNetworkAt(c, out network))
            {
                return false;
            }

            for (int i = 0; i < network.Domes.Count; i++)
            {
                CompOmniForceFieldDome dome = network.Domes[i];
                if (dome == null || !dome.Active || !dome.DomeRect.Contains(c))
                {
                    continue;
                }

                switch (dome.GlowMode)
                {
                    case OmniForceFieldDomeGlowMode.SunLamp:
                        glow = Mathf.Max(glow, 1f);
                        break;
                    case OmniForceFieldDomeGlowMode.Lamp:
                        glow = Mathf.Max(glow, 0.5f);
                        break;
                }
            }

            return glow > 0f;
        }

        public bool IsProtectedFrom(Thing searcher, IntVec3 c)
        {
            OmniForceFieldDomeNetwork network;
            if (!TryGetNetworkAt(c, out network))
            {
                return false;
            }
            CompOmniForceFieldDome dome = network.PrimaryDome;
            // 复用 CompOmniProjectileInterceptor 的敌我判定：敌方/无主来源被保护区域拒绝。
            return dome != null && dome.IsEnemy(searcher);
        }

        public bool IsRoofSupportedByDome(IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }

            for (int i = 0; i < domes.Count; i++)
            {
                CompOmniForceFieldDome dome = domes[i];
                if (dome != null && dome.SupportsRoofAt(c))
                {
                    return true;
                }
            }

            return false;
        }

        public bool AllowsPlantUnderRoof(IntVec3 c, ThingDef plantDef)
        {
            if (plantDef?.plant == null || !c.InBounds(map) || !map.roofGrid.Roofed(c))
            {
                return false;
            }

            if (plantDef.plant.diesToLight || plantDef.plant.cavePlant)
            {
                return false;
            }

            CompProperties_OmniForceFieldDome props;
            return TryGetPropsAt(c, out props) && props.allowPlantsUnderRoof && AllowsSkyLightThroughRoof(c);
        }

        public bool AllowsSkyLightThroughRoof(IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }

            RoofDef roof = map.roofGrid.RoofAt(c);
            if (roof == null || roof.isNatural || roof.isThickRoof)
            {
                return false;
            }

            CompProperties_OmniForceFieldDome props;
            return TryGetPropsAt(c, out props) && props.allowSkyLightThroughRoof;
        }

        private void RebuildNetworks()
        {
            if (networkCache == null || networkCache.Length != map.cellIndices.NumGridCells)
            {
                networkCache = new OmniForceFieldDomeNetwork[map.cellIndices.NumGridCells];
            }
            if (roomCellCache == null || roomCellCache.Length != map.cellIndices.NumGridCells)
            {
                roomCellCache = new bool[map.cellIndices.NumGridCells];
            }
            Array.Clear(networkCache, 0, networkCache.Length);
            Array.Clear(roomCellCache, 0, roomCellCache.Length);
            networks.Clear();

            bool[] visited = new bool[domes.Count];
            for (int i = 0; i < domes.Count; i++)
            {
                CompOmniForceFieldDome start = domes[i];
                if (visited[i] || start == null || !start.Active || !start.parent.Spawned || start.parent.Map != map)
                {
                    continue;
                }

                // BFS 合并相邻/重叠矩形。这里按穹顶数量做图搜索，而不是按格洪泛，
                // 对少量大穹顶更便宜，也能保留每个网络的穹顶列表。
                OmniForceFieldDomeNetwork network = new OmniForceFieldDomeNetwork();
                Queue<int> queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int currentIndex = queue.Dequeue();
                    CompOmniForceFieldDome current = domes[currentIndex];
                    if (current == null || !current.Active)
                    {
                        continue;
                    }

                    network.Domes.Add(current);
                    CellRect currentRect = current.DomeRect;

                    for (int j = 0; j < domes.Count; j++)
                    {
                        if (visited[j])
                        {
                            continue;
                        }
                        CompOmniForceFieldDome other = domes[j];
                        if (other == null || !other.Active || !other.parent.Spawned || other.parent.Map != map)
                        {
                            continue;
                        }
                        if (RectsTouchOrOverlap(currentRect, other.DomeRect))
                        {
                            visited[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }

                CacheNetworkCells(network);
                networks.Add(network);
            }

            cacheDirty = false;
        }

        private void RebuildRegionsAndRooms()
        {
            // 穹顶虚拟房间格不是 Thing，不会触发原版区域 dirty 通知；必须主动重建 Room/Region。
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            map.reachability.ClearCache();
            regionsDirty = false;
        }

        private void CacheNetworkCells(OmniForceFieldDomeNetwork network)
        {
            // 将合并后的所有矩形投影到格缓存。重叠格只记录一次。
            HashSet<IntVec3> seenCells = new HashSet<IntVec3>();
            for (int i = 0; i < network.Domes.Count; i++)
            {
                CellRect rect = network.Domes[i].DomeRect;
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    for (int z = rect.minZ; z <= rect.maxZ; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (!c.InBounds(map) || !seenCells.Add(c))
                        {
                            continue;
                        }
                        network.Cells.Add(c);
                        networkCache[map.cellIndices.CellToIndex(c)] = network;
                    }
                }
            }

            MarkRoomCells(network);
        }

        private void MarkRoomCells(OmniForceFieldDomeNetwork network)
        {
            CompProperties_OmniForceFieldDome props = network.PrimaryProps;
            if (props == null || !props.formRoom)
            {
                return;
            }

            for (int i = 0; i < network.Cells.Count; i++)
            {
                roomCellCache[map.cellIndices.CellToIndex(network.Cells[i])] = true;
            }
        }

        private static bool RectsTouchOrOverlap(CellRect a, CellRect b)
        {
            // +1 使边贴边的穹顶也合并为一个连续穹顶房间。
            return a.minX <= b.maxX + 1
                   && a.maxX + 1 >= b.minX
                   && a.minZ <= b.maxZ + 1
                   && a.maxZ + 1 >= b.minZ;
        }

        private void DirtySkyLightDomeGlowIfNeeded()
        {
            if (map?.glowGrid == null || map.skyManager == null)
            {
                return;
            }

            float skyGlow = map.skyManager.CurSkyGlow;
            if (cachedSkyGlow >= 0f && Mathf.Abs(cachedSkyGlow - skyGlow) < SkyGlowDirtyThreshold)
            {
                return;
            }

            cachedSkyGlow = skyGlow;
            for (int i = 0; i < networks.Count; i++)
            {
                OmniForceFieldDomeNetwork network = networks[i];
                for (int j = 0; j < network.Cells.Count; j++)
                {
                    IntVec3 c = network.Cells[j];
                    if (AllowsSkyLightThroughRoof(c))
                    {
                        map.glowGrid.DirtyCell(c);
                    }
                }
            }
        }

        private int GetEnvironmentTickInterval()
        {
            int interval = 250;
            for (int i = 0; i < networks.Count; i++)
            {
                CompProperties_OmniForceFieldDome props = networks[i].PrimaryProps;
                if (props != null)
                {
                    interval = Mathf.Max(30, Mathf.Min(interval, props.environmentTickInterval));
                }
            }
            return interval;
        }

        private int GetPawnTickInterval()
        {
            int interval = 60;
            for (int i = 0; i < networks.Count; i++)
            {
                CompProperties_OmniForceFieldDome props = networks[i].PrimaryProps;
                if (props != null)
                {
                    interval = Mathf.Max(10, Mathf.Min(interval, props.pawnTickInterval));
                }
            }
            return interval;
        }

        private void MaintainEnvironment()
        {
            for (int i = 0; i < networks.Count; i++)
            {
                OmniForceFieldDomeNetwork network = networks[i];
                CompProperties_OmniForceFieldDome props = network.PrimaryProps;
                if (props == null)
                {
                    continue;
                }

                for (int j = 0; j < network.Cells.Count; j++)
                {
                    IntVec3 c = network.Cells[j];

                    if (props.clearGas && map.gasGrid != null && map.gasGrid.AnyGasAt(c))
                    {
                        // ClearCellUnsafe 不触发气体扩散，适合穹顶这种“直接净化”效果。
                        map.gasGrid.ClearCellUnsafe(c);
                    }

                    if (props.clearPollution && ModsConfig.BiotechActive && map.pollutionGrid != null && map.pollutionGrid.IsPolluted(c))
                    {
                        map.pollutionGrid.SetPolluted(c, false, true);
                    }

                    if (props.cleanFilth)
                    {
                        FilthMaker.RemoveAllFilth(c, map);
                    }

                    if (props.extinguishFire)
                    {
                        ExtinguishFiresAt(c);
                    }

                    // 植物生长控制
                    ApplyPlantGrowth(c, network);
                }
            }
        }

        private void ApplyPlantGrowth(IntVec3 cell, OmniForceFieldDomeNetwork network)
        {
            // 找到主穹顶组件来获取生长模式
            CompOmniForceFieldDome primaryDome = network.Domes.FirstOrDefault();
            if (primaryDome == null || primaryDome.GrowthMode == OmniForceFieldDomeGrowthMode.Normal)
            {
                return;
            }

            OmniForceFieldDomeGrowthMode growthMode = primaryDome.GrowthMode;
            List<Thing> things = cell.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                if (things[i] is Plant plant)
                {
                    switch (growthMode)
                    {
                        case OmniForceFieldDomeGrowthMode.Forced:
                            if (plant.Growth < 1f)
                            {
                                plant.Growth = 1f;
                            }
                            break;
                        case OmniForceFieldDomeGrowthMode.Cleared:
                            plant.Destroy();
                            break;
                    }
                }
            }
        }

        private void ExtinguishFiresAt(IntVec3 c)
        {
            List<Thing> things = c.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                if (things[i] is Fire)
                {
                    things[i].Destroy();
                }
            }
        }

        private void MaintainPawnEffects()
        {
            tmpPawnsToRemove.Clear();

            // 先处理离开穹顶或离图的 Pawn，再给当前穹顶内友方补状态。
            foreach (Pawn pawn in pawnsGrantedHediff)
            {
                if (pawn == null || !pawn.Spawned || pawn.Map != map || !IsDomeCell(pawn.Position))
                {
                    tmpPawnsToRemove.Add(pawn);
                }
            }

            for (int i = 0; i < tmpPawnsToRemove.Count; i++)
            {
                Pawn pawn = tmpPawnsToRemove[i];
                pawnsGrantedHediff.Remove(pawn);
                RemoveDomeHediffIfNeeded(pawn);
            }

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.health == null || !IsFriendlyPawn(pawn))
                {
                    continue;
                }

                OmniForceFieldDomeNetwork network;
                if (!TryGetNetworkAt(pawn.Position, out network))
                {
                    continue;
                }

                CompProperties_OmniForceFieldDome props = network.PrimaryProps;
                HediffDef hediffDef = props?.FriendlyHediffDefToUse;
                if (props == null || !props.applyFriendlyHediff || hediffDef == null)
                {
                    continue;
                }

                if (pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef) == null)
                {
                    pawn.health.AddHediff(hediffDef);
                }
                pawnsGrantedHediff.Add(pawn);
            }
        }

        private void RemoveDomeHediffIfNeeded(Pawn pawn)
        {
            if (pawn == null || pawn.health == null)
            {
                return;
            }

            for (int i = 0; i < domes.Count; i++)
            {
                CompProperties_OmniForceFieldDome props = domes[i]?.Props;
                HediffDef hediffDef = props?.FriendlyHediffDefToUse;
                if (props == null || !props.removeFriendlyHediffWhenLeaving || hediffDef == null)
                {
                    continue;
                }

                // 只尝试移除本穹顶配置对应的 hediff；若多个穹顶使用不同 hediff，都能各自清理。
                Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                if (hediff != null)
                {
                    pawn.health.RemoveHediff(hediff);
                }
            }
        }

        private static bool IsFriendlyPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            return pawn.Faction == Faction.OfPlayer || pawn.HostFaction == Faction.OfPlayer || pawn.IsPrisonerOfColony;
        }
    }

    [HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
    public static class Patch_OmniForceFieldDome_RegionType
    {
        public static void Postfix(IntVec3 c, Map map, ref RegionType __result)
        {
            if (__result != RegionType.Normal || map == null || !c.InBounds(map))
            {
                return;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager != null && manager.IsDomeRoomCell(c))
            {
                __result = OmniForceFieldDomeNetworkManager.DomeRoomRegionType;
            }
        }
    }

    [HarmonyPatch(typeof(RegionAndRoomUpdater), "ShouldBeInTheSameRoom")]
    public static class Patch_OmniForceFieldDome_ShouldBeInTheSameRoom
    {
        public static bool Prefix(District a, District b, ref bool __result)
        {
            bool isDomeA = a.RegionType == OmniForceFieldDomeNetworkManager.DomeRoomRegionType;
            bool isDomeB = b.RegionType == OmniForceFieldDomeNetworkManager.DomeRoomRegionType;

            if (isDomeA && isDomeB)
            {
                __result = true;
                return false;
            }

            if (isDomeA || isDomeB)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.TryGetTemperatureForCell))]
    public static class Patch_OmniForceFieldDome_Temperature
    {
        public static void Postfix(IntVec3 c, Map map, ref float tempResult, ref bool __result)
        {
            if (map == null || !c.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(c, out props)
                && props.maintainTemperature)
            {
                // 直接覆盖单格温度查询结果，让穹顶像独立恒温房间，但不污染 Room.Temperature。
                tempResult = props.targetTemperature;
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(VacuumUtility), nameof(VacuumUtility.GetVacuum))]
    public static class Patch_OmniForceFieldDome_Vacuum
    {
        public static void Postfix(IntVec3 cell, Map map, ref float __result)
        {
            if (map == null || !cell.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(cell, out props)
                && props.blockVacuum)
            {
                // 真空伤害、真空顾虑等逻辑最终都会查询 GetVacuum，这里统一归零。
                __result = 0f;
            }
        }
    }

    // Dome roofs no longer alter global ground glow; plant growth has a narrow patch below.
    public static class OmniForceFieldDomeGlobalGroundGlowNotPatched
    {
        private static readonly AccessTools.FieldRef<GlowGrid, Map> MapField =
            AccessTools.FieldRefAccess<GlowGrid, Map>("map");

        private static void DisabledPostfix(GlowGrid __instance, IntVec3 c, bool ignoreSky, ref float __result)
        {
            if (ignoreSky)
            {
                return;
            }

            Map map = MapField(__instance);
            if (map == null || !c.InBounds(map))
            {
                return;
            }

            CompProperties_OmniForceFieldDome props;
            if (map.GetComponent<OmniForceFieldDomeNetworkManager>().TryGetPropsAt(c, out props)
                && props.allowPlantsUnderRoof
                && map.roofGrid.Roofed(c))
            {
                __result = Mathf.Max(__result, map.skyManager.CurSkyGlow);
                // RoofGrid 有屋顶会让原版不再取天空光；这里把天空光补回来，实现“透光屋顶”。
                __result = Mathf.Max(__result, map.skyManager.CurSkyGlow);
            }
        }
    }

    public static class OmniForceFieldDomeSkyLightUtility
    {
        private static readonly AccessTools.FieldRef<GlowGrid, Map> MapField =
            AccessTools.FieldRefAccess<GlowGrid, Map>("map");

        public static bool AllowsSkyLightThroughRoof(IntVec3 c, Map map)
        {
            if (map == null || !c.InBounds(map))
            {
                return false;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            return manager != null && manager.AllowsSkyLightThroughRoof(c);
        }

        public static bool AllowsSkyLightThroughRoof(int index, Map map)
        {
            if (map == null || index < 0 || index >= map.cellIndices.NumGridCells)
            {
                return false;
            }

            return AllowsSkyLightThroughRoof(map.cellIndices.IndexToCell(index), map);
        }

        public static Map GetMap(GlowGrid glowGrid)
        {
            return glowGrid == null ? null : MapField(glowGrid);
        }

        public static bool TryGetDomeGlow(IntVec3 c, Map map, out float glow)
        {
            glow = 0f;
            if (map == null || !c.InBounds(map))
            {
                return false;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            return manager != null && manager.TryGetDomeGlowAt(c, out glow);
        }

        public static bool TryGetDomeGlow(int index, Map map, out float glow)
        {
            glow = 0f;
            if (map == null || index < 0 || index >= map.cellIndices.NumGridCells)
            {
                return false;
            }

            return TryGetDomeGlow(map.cellIndices.IndexToCell(index), map, out glow);
        }

        public static Color32 WithDomeGlow(Color32 color, float glow)
        {
            byte light = (byte)Mathf.Clamp(Mathf.RoundToInt(glow * 255f), 0, 255);
            if (light <= 0)
            {
                return color;
            }

            color.r = Math.Max(color.r, light);
            color.g = Math.Max(color.g, light);
            color.b = Math.Max(color.b, light);
            color.a = Math.Max(color.a, light);
            return color;
        }

        public static Color32 WithSkyGlow(Color32 color, Map map)
        {
            if (map == null)
            {
                return color;
            }

            byte sky = (byte)Mathf.Clamp(Mathf.RoundToInt(map.skyManager.CurSkyGlow * 255f), 0, 255);
            if (sky <= 0)
            {
                return color;
            }

            color.r = Math.Max(color.r, sky);
            color.g = Math.Max(color.g, sky);
            color.b = Math.Max(color.b, sky);
            color.a = Math.Max(color.a, sky);
            return color;
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt))]
    public static class Patch_OmniForceFieldDome_GroundGlowAt
    {
        public static void Postfix(GlowGrid __instance, IntVec3 c, bool ignoreSky, ref float __result)
        {
            Map map = OmniForceFieldDomeSkyLightUtility.GetMap(__instance);
            float domeGlow;
            if (OmniForceFieldDomeSkyLightUtility.TryGetDomeGlow(c, map, out domeGlow))
            {
                __result = Mathf.Max(__result, domeGlow);
            }

            if (ignoreSky)
            {
                return;
            }

            if (OmniForceFieldDomeSkyLightUtility.AllowsSkyLightThroughRoof(c, map))
            {
                __result = Mathf.Max(__result, map.skyManager.CurSkyGlow);
            }
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.VisualGlowAt), new[] { typeof(IntVec3) })]
    public static class Patch_OmniForceFieldDome_VisualGlowAtCell
    {
        public static void Postfix(GlowGrid __instance, IntVec3 c, ref Color32 __result)
        {
            Map map = OmniForceFieldDomeSkyLightUtility.GetMap(__instance);
            float domeGlow;
            if (OmniForceFieldDomeSkyLightUtility.TryGetDomeGlow(c, map, out domeGlow))
            {
                __result = OmniForceFieldDomeSkyLightUtility.WithDomeGlow(__result, domeGlow);
            }

            if (OmniForceFieldDomeSkyLightUtility.AllowsSkyLightThroughRoof(c, map))
            {
                __result = OmniForceFieldDomeSkyLightUtility.WithSkyGlow(__result, map);
            }
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.VisualGlowAt), new[] { typeof(int) })]
    public static class Patch_OmniForceFieldDome_VisualGlowAtIndex
    {
        public static void Postfix(GlowGrid __instance, int index, ref Color32 __result)
        {
            Map map = OmniForceFieldDomeSkyLightUtility.GetMap(__instance);
            float domeGlow;
            if (OmniForceFieldDomeSkyLightUtility.TryGetDomeGlow(index, map, out domeGlow))
            {
                __result = OmniForceFieldDomeSkyLightUtility.WithDomeGlow(__result, domeGlow);
            }

            if (OmniForceFieldDomeSkyLightUtility.AllowsSkyLightThroughRoof(index, map))
            {
                __result = OmniForceFieldDomeSkyLightUtility.WithSkyGlow(__result, map);
            }
        }
    }

    [HarmonyPatch(typeof(SectionLayer_IndoorMask), "HideCommon")]
    public static class Patch_OmniForceFieldDome_IndoorMask
    {
        public static void Postfix(Map map, IntVec3 c, ref bool __result)
        {
            if (__result && OmniForceFieldDomeSkyLightUtility.AllowsSkyLightThroughRoof(c, map))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(CompPowerPlantSolar), "RoofedPowerOutputFactor", MethodType.Getter)]
    public static class Patch_OmniForceFieldDome_SolarRoofedFactor
    {
        public static void Postfix(CompPowerPlantSolar __instance, ref float __result)
        {
            if (__instance?.parent?.Spawned != true || __instance.parent.Map == null)
            {
                return;
            }

            int cellCount = 0;
            int blockedRoofCount = 0;
            Map map = __instance.parent.Map;
            foreach (IntVec3 c in __instance.parent.OccupiedRect())
            {
                cellCount++;
                if (map.roofGrid.Roofed(c) && !OmniForceFieldDomeSkyLightUtility.AllowsSkyLightThroughRoof(c, map))
                {
                    blockedRoofCount++;
                }
            }

            if (cellCount > 0)
            {
                __result = Mathf.Max(__result, (float)(cellCount - blockedRoofCount) / cellCount);
            }
        }
    }

    [HarmonyPatch(typeof(PlaceWorker_NotUnderRoof), nameof(PlaceWorker_NotUnderRoof.AllowsPlacing))]
    public static class Patch_OmniForceFieldDome_NotUnderRoofPlacement
    {
        public static void Postfix(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, ref AcceptanceReport __result)
        {
            if (__result.Accepted || checkingDef == null || map == null)
            {
                return;
            }

            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, checkingDef.Size))
            {
                if (map.roofGrid.Roofed(c) && !OmniForceFieldDomeSkyLightUtility.AllowsSkyLightThroughRoof(c, map))
                {
                    return;
                }
            }

            __result = true;
        }
    }

    public static class OmniForceFieldDomePlantRoofUtility
    {
        private static readonly FieldInfo WantedPlantDefField =
            AccessTools.Field(typeof(WorkGiver_Grower), "wantedPlantDef");
        private static readonly MethodInfo RoofedMethod =
            AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Roofed), new[] { typeof(IntVec3), typeof(Map) });
        private static readonly MethodInfo RoofedForPlantRoofBlockMethod =
            AccessTools.Method(typeof(OmniForceFieldDomePlantRoofUtility), nameof(RoofedForPlantRoofBlock));
        private static readonly MethodInfo ThingToInstallGetter =
            AccessTools.PropertyGetter(typeof(Designator_Install), "ThingToInstall");

        public static bool AllowsPlantUnderDomeRoof(IntVec3 c, Map map, ThingDef plantDef)
        {
            if (map == null || plantDef?.plant == null || !c.InBounds(map))
            {
                return false;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            return manager != null && manager.AllowsPlantUnderRoof(c, plantDef);
        }

        public static bool RoofedForPlantRoofBlock(IntVec3 c, Map map, ThingDef plantDef)
        {
            return map != null && map.roofGrid.Roofed(c) && !AllowsPlantUnderDomeRoof(c, map, plantDef);
        }

        public static void DisableRoofInterferenceIfAllowed(ThingDef plantDef, IntVec3 c, Map map,
            ref PlantRoofInterferenceState state)
        {
            if (!AllowsPlantUnderDomeRoof(c, map, plantDef) || plantDef?.plant == null || !plantDef.plant.interferesWithRoof)
            {
                return;
            }

            state.plant = plantDef.plant;
            state.originalInterferesWithRoof = plantDef.plant.interferesWithRoof;
            state.changed = true;
            plantDef.plant.interferesWithRoof = false;
        }

        public static Exception RestoreRoofInterference(Exception exception, PlantRoofInterferenceState state)
        {
            if (state.changed && state.plant != null)
            {
                state.plant.interferesWithRoof = state.originalInterferesWithRoof;
            }
            return exception;
        }

        public static Thing GetThingToInstall(Designator_Install designator)
        {
            return ThingToInstallGetter?.Invoke(designator, null) as Thing;
        }

        public static IEnumerable<CodeInstruction> ReplaceSecondRoofedCallWithPlantAwareCheck(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();
            int roofedCallCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                if (code.Calls(RoofedMethod))
                {
                    roofedCallCount++;
                    if (roofedCallCount == 2)
                    {
                        yield return new CodeInstruction(OpCodes.Ldsfld, WantedPlantDefField);
                        code = new CodeInstruction(OpCodes.Call, RoofedForPlantRoofBlockMethod);
                    }
                }

                yield return code;
            }
        }

        public struct PlantRoofInterferenceState
        {
            public PlantProperties plant;
            public bool originalInterferesWithRoof;
            public bool changed;
        }
    }

    [HarmonyPatch(typeof(Plant), "GrowthRateFactor_Light", MethodType.Getter)]
    public static class Patch_OmniForceFieldDome_PlantLightUnderRoof
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            if (__instance?.Spawned != true || __instance.Map == null)
            {
                return;
            }

            if (OmniForceFieldDomePlantRoofUtility.AllowsPlantUnderDomeRoof(__instance.Position, __instance.Map, __instance.def))
            {
                float skyLightFactor = PlantUtility.GrowthRateFactorFor_Light(__instance.def, __instance.Map.skyManager.CurSkyGlow);
                __result = Mathf.Max(__result, skyLightFactor);
            }
        }
    }

    [HarmonyPatch(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell))]
    public static class Patch_OmniForceFieldDome_GrowerSowRoofCheck
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return OmniForceFieldDomePlantRoofUtility.ReplaceSecondRoofedCallWithPlantAwareCheck(instructions);
        }
    }

    [HarmonyPatch(typeof(CompPlantable), nameof(CompPlantable.CanPlantAt))]
    public static class Patch_OmniForceFieldDome_CompPlantableRoofCheck
    {
        public static void Prefix(CompPlantable __instance, IntVec3 cell, Map map,
            ref OmniForceFieldDomePlantRoofUtility.PlantRoofInterferenceState __state)
        {
            OmniForceFieldDomePlantRoofUtility.DisableRoofInterferenceIfAllowed(__instance.Props.plantDefToSpawn, cell, map, ref __state);
        }

        public static Exception Finalizer(Exception __exception,
            OmniForceFieldDomePlantRoofUtility.PlantRoofInterferenceState __state)
        {
            return OmniForceFieldDomePlantRoofUtility.RestoreRoofInterference(__exception, __state);
        }
    }

    [HarmonyPatch(typeof(Designator_Replant), nameof(Designator_Replant.CanDesignateCell))]
    public static class Patch_OmniForceFieldDome_DesignatorReplantRoofCheck
    {
        public static void Prefix(Designator_Replant __instance, IntVec3 c,
            ref OmniForceFieldDomePlantRoofUtility.PlantRoofInterferenceState __state)
        {
            Plant plant = OmniForceFieldDomePlantRoofUtility.GetThingToInstall(__instance) as Plant;
            OmniForceFieldDomePlantRoofUtility.DisableRoofInterferenceIfAllowed(plant?.def, c, __instance.Map, ref __state);
        }

        public static Exception Finalizer(Exception __exception,
            OmniForceFieldDomePlantRoofUtility.PlantRoofInterferenceState __state)
        {
            return OmniForceFieldDomePlantRoofUtility.RestoreRoofInterference(__exception, __state);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Replant), nameof(WorkGiver_Replant.JobOnThing))]
    public static class Patch_OmniForceFieldDome_WorkGiverReplantRoofCheck
    {
        public static void Prefix(Thing t, ref OmniForceFieldDomePlantRoofUtility.PlantRoofInterferenceState __state)
        {
            Blueprint_Install blueprint = t as Blueprint_Install;
            ThingDef plantDef = blueprint?.def.entityDefToBuild as ThingDef;
            if (plantDef?.plant == null)
            {
                return;
            }

            OmniForceFieldDomePlantRoofUtility.DisableRoofInterferenceIfAllowed(plantDef, t.Position, t.Map, ref __state);
        }

        public static Exception Finalizer(Exception __exception,
            OmniForceFieldDomePlantRoofUtility.PlantRoofInterferenceState __state)
        {
            return OmniForceFieldDomePlantRoofUtility.RestoreRoofInterference(__exception, __state);
        }
    }

    [HarmonyPatch(typeof(RoofCollapseUtility), nameof(RoofCollapseUtility.WithinRangeOfRoofHolder))]
    public static class Patch_OmniForceFieldDome_RoofSupportRange
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result || map == null || !c.InBounds(map))
            {
                return;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager != null && manager.IsRoofSupportedByDome(c))
            {
                __result = true;
                // 能量屋顶不依赖墙柱支撑，避免原版屋顶塌方扫描把它标记为坍塌。
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(RoofCollapseUtility), nameof(RoofCollapseUtility.ConnectedToRoofHolder))]
    public static class Patch_OmniForceFieldDome_RoofConnected
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result || map == null || !c.InBounds(map))
            {
                return;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager != null && manager.IsRoofSupportedByDome(c))
            {
                __result = true;
                // 建造/维护屋顶时还会检查“是否连接到支撑物”，这里同样放行穹顶格。
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.CanHitTargetFrom))]
    public static class Patch_OmniForceFieldDome_VerbCanHitTargetFrom
    {
        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || __instance?.caster == null)
            {
                return;
            }

            Map map = __instance.caster.Map;
            if (map == null)
            {
                return;
            }

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager == null)
            {
                return;
            }

            Thing caster = __instance.caster;
            if (targ.HasThing && targ.Thing != null && manager.IsProtectedFrom(caster, targ.Thing.Position))
            {
                // 覆盖非投射物 Verb（例如 beam/特殊武器）的 CanHitTargetFrom 结果。
                __result = false;
                return;
            }

            if (!targ.HasThing && manager.IsProtectedFrom(caster, targ.Cell))
            {
                __result = false;
                return;
            }

            if (manager.IsProtectedFrom(caster, root))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Plant), "Tick")]
    public static class Patch_OmniForceFieldDome_PlantGrowthStopped
    {
        public static bool Prefix(Plant __instance)
        {
            if (!__instance.Spawned) return true;
            Map map = __instance.Map;
            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager != null && manager.TryGetNetworkAt(__instance.Position, out var network))
            {
                CompOmniForceFieldDome primaryDome = network.Domes.FirstOrDefault();
                if (primaryDome != null && primaryDome.GrowthMode == OmniForceFieldDomeGrowthMode.Stopped)
                {
                    return false; // 停止生长
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(WildPlantSpawner), "CheckCellForWildPlant")]
    public static class Patch_OmniForceFieldDome_WildPlantSpawner_CheckCellForWildPlant
    {
        public static bool Prefix(WildPlantSpawner __instance, IntVec3 c, ref bool __result)
        {
            Map map = (Map)AccessTools.Field(typeof(WildPlantSpawner), "map").GetValue(__instance);
            if (map == null) return true;

            OmniForceFieldDomeNetworkManager manager = map.GetComponent<OmniForceFieldDomeNetworkManager>();
            if (manager != null && manager.TryGetNetworkAt(c, out var network))
            {
                CompOmniForceFieldDome primaryDome = network.Domes.FirstOrDefault();
                if (primaryDome != null && primaryDome.GrowthMode == OmniForceFieldDomeGrowthMode.Cleared)
                {
                    __result = false;
                    return false;
                }
            }

            return true;
        }
    }
}
