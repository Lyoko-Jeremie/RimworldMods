using System;
using System.Collections.Generic;
using System.Reflection;
using Jeremie.AtmosphereOverhaulPatch.Api;
using Jeremie.AtmosphereOverhaulPatch.Util;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 超维科技兼容模块。
    ///
    /// 目标：把「生物圈」和「穹顶发生器」从「只改读取值」升级为「真正写 AO 物理场」。
    ///
    /// 现状问题：
    /// - 生物圈用 GenTemperature.GetTemperatureForCell 的 Postfix 覆盖读数，
    ///   用 Room.OpenRoofCount 伪装屋顶；AO 下读数被覆盖，但 AO 自己算的温度、
    ///   气压、氧气、湿度完全不变，动物/植物/腐烂/火都按真实场走。
    /// - 穹顶发生器用 TryGetTemperatureForCell + VacuumUtility.GetVacuum 的 Postfix，
    ///   同样只是「看起来稳定」。
    ///
    /// 这里不 patch 目标 mod 的任何方法，而是注册一个 MapComponent，周期性扫描
    /// 两类建筑，把它们转成 IForceFieldRegion 交给宿主的 ForceFieldStabilizer，
    /// 用 AtmosphereApi.AddDeltas 逐格（或批量）把物理场拉回目标值。
    /// 目标 mod 不存在时整个组件空转，不产生任何日志噪音。
    /// </summary>
    public sealed class OmniCrafterCompat : CompatModuleBase
    {
        internal const string BiosphereTypeName = "FullyAutomaticOmniCrafter.CompBiosphere";
        internal const string DomeTypeName = "FullyAutomaticOmniCrafter.CompOmniForceFieldDome";

        public override string PackageId
        {
            get { return "Jeremie.Fully.Automatic.OmniCrafter"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_Omni"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_OmniHint"; }
        }

        /// <summary>需要 AO 的批量增量 API；没有就跳过，避免只改一半。</summary>
        public override AoCapability Required
        {
            get { return AoCapability.Deltas; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings != null && AOPatchMod.Settings.EnableOmniCrafter;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableOmniCrafter = value;
            }
        }

        protected override bool TryApply(HarmonyLib.Harmony harmony)
        {
            Type biosphere = ReflectionCache.Type(BiosphereTypeName);
            Type dome = ReflectionCache.Type(DomeTypeName);

            if (biosphere == null && dome == null)
            {
                PatchLog.Warn("未找到生物圈或穹顶类型，超维科技兼容跳过。");
                return false;
            }

            OmniEnvironmentBridge.Configure(biosphere, dome);
            OmniEnvironmentBridge.Enabled = true;

            PatchLog.Info(
                "超维科技强制稳定已启用：生物圈=" + (biosphere != null ? "是" : "否")
                + "，穹顶=" + (dome != null ? "是" : "否"));
            return true;
        }

        protected override void OnRevert()
        {
            OmniEnvironmentBridge.Enabled = false;
            OmniEnvironmentBridge.ClearAll();
        }
    }

    /// <summary>
    /// 每张地图一个的扫描器：把生物圈/穹顶建筑转成强制稳定区。
    /// </summary>
    public class OmniEnvironmentBridge : MapComponent
    {
        /// <summary>整体开关，由 OmniCrafterCompat.Apply/Revert 控制。</summary>
        public static bool Enabled;

        /// <summary>扫描间隔（tick）。与 AO 的模拟间隔对齐由 ForceFieldStabilizer 负责。</summary>
        private const int ScanInterval = 30;

        private static Type biosphereType;
        private static Type domeType;

        private readonly Dictionary<ThingComp, IForceFieldRegion> regions =
            new Dictionary<ThingComp, IForceFieldRegion>();

        public OmniEnvironmentBridge(Map map) : base(map)
        {
        }

        public static void Configure(Type biosphere, Type dome)
        {
            biosphereType = biosphere;
            domeType = dome;
        }

        /// <summary>禁用或重载时清空所有地图的注册。</summary>
        public static void ClearAll()
        {
            if (Find.Maps == null)
            {
                return;
            }

            for (int i = 0; i < Find.Maps.Count; i++)
            {
                Map map = Find.Maps[i];
                OmniEnvironmentBridge bridge = map.GetComponent<OmniEnvironmentBridge>();
                if (bridge != null)
                {
                    bridge.Clear();
                }
            }
        }

        public override void MapComponentTick()
        {
            if (!Enabled)
            {
                if (regions.Count > 0)
                {
                    Clear();
                }

                return;
            }

            if (biosphereType == null && domeType == null)
            {
                return;
            }

            if (!map.IsHashIntervalTick(ScanInterval))
            {
                return;
            }

            Sync();
        }

        private void Sync()
        {
            ForceFieldStabilizer stabilizer = map.GetComponent<ForceFieldStabilizer>();
            if (stabilizer == null)
            {
                return;
            }

            List<ThingComp> seen = new List<ThingComp>();

            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Building building = buildings[i];
                if (building == null || building.Destroyed)
                {
                    continue;
                }

                List<ThingComp> comps = building.AllComps;
                if (comps == null)
                {
                    continue;
                }

                for (int j = 0; j < comps.Count; j++)
                {
                    ThingComp comp = comps[j];
                    if (comp == null)
                    {
                        continue;
                    }

                    Type type = comp.GetType();
                    IForceFieldRegion region;

                    if (regions.TryGetValue(comp, out region))
                    {
                        seen.Add(comp);
                        continue;
                    }

                    if (biosphereType != null && biosphereType.IsAssignableFrom(type))
                    {
                        region = new BiosphereRegion(comp);
                    }
                    else if (domeType != null && domeType.IsAssignableFrom(type))
                    {
                        region = new DomeRegion(comp);
                    }
                    else
                    {
                        continue;
                    }

                    regions[comp] = region;
                    seen.Add(comp);
                    stabilizer.Register(region);
                }
            }

            // 清理已经消失的建筑
            if (regions.Count != seen.Count)
            {
                List<ThingComp> stale = null;
                foreach (KeyValuePair<ThingComp, IForceFieldRegion> pair in regions)
                {
                    if (!seen.Contains(pair.Key))
                    {
                        if (stale == null)
                        {
                            stale = new List<ThingComp>();
                        }

                        stale.Add(pair.Key);
                    }
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        ThingComp comp = stale[i];
                        stabilizer.Unregister(regions[comp]);
                        regions.Remove(comp);
                    }
                }
            }
        }

        private void Clear()
        {
            ForceFieldStabilizer stabilizer = map.GetComponent<ForceFieldStabilizer>();
            if (stabilizer != null)
            {
                foreach (KeyValuePair<ThingComp, IForceFieldRegion> pair in regions)
                {
                    stabilizer.Unregister(pair.Value);
                }
            }

            regions.Clear();
        }
    }

    /// <summary>把 CompBiosphere 映射成强制稳定区。</summary>
    internal sealed class BiosphereRegion : IForceFieldRegion
    {
        private const int CacheTicks = 60;

        private readonly ThingComp comp;
        private readonly PropertyInfo controlProperty;
        private readonly PropertyInfo targetProperty;
        private readonly PropertyInfo areaProperty;
        private readonly PropertyInfo noVacuumProperty;
        private readonly FieldInfo noVacuumField;

        private List<IntVec3> cachedCells;
        private int cachedAtTick = -9999;

        public BiosphereRegion(ThingComp comp)
        {
            this.comp = comp;
            Type type = comp.GetType();
            controlProperty = ReflectionCache.Property(type, "controlTemperature");
            targetProperty = ReflectionCache.Property(type, "targetTemperature");
            areaProperty = ReflectionCache.Property(type, "Area");
            noVacuumProperty = ReflectionCache.Property(type, "ensureNoVacuum");
            noVacuumField = noVacuumProperty == null ? ReflectionCache.Field(type, "_ensureNoVacuum") : null;
        }

        public string RegionName
        {
            get { return "生物圈#" + ThingId; }
        }

        private int ThingId
        {
            get { return comp.parent != null ? comp.parent.thingIDNumber : 0; }
        }

        public bool Active
        {
            get
            {
                if (comp.parent == null || !comp.parent.Spawned)
                {
                    return false;
                }

                // 只有开启「控制温度」才接管；关闭时把环境还给 AO。
                if (controlProperty != null)
                {
                    object value = controlProperty.GetValue(comp, null);
                    if (value is bool && !(bool)value)
                    {
                        return false;
                    }
                }

                return GetArea() != null;
            }
        }

        public List<IntVec3> Cells
        {
            get
            {
                if (comp.parent == null || comp.parent.Map == null)
                {
                    return null;
                }

                int now = GenTicks.TicksGame;
                if (cachedCells != null && now - cachedAtTick < CacheTicks)
                {
                    return cachedCells;
                }

                Area area = GetArea();
                if (area == null)
                {
                    cachedCells = null;
                    cachedAtTick = now;
                    return null;
                }

                List<IntVec3> cells = new List<IntVec3>();
                foreach (IntVec3 cell in area.ActiveCells)
                {
                    cells.Add(cell);
                }

                cachedCells = cells;
                cachedAtTick = now;
                return cells;
            }
        }

        /// <summary>生物圈形状不规则，逐格轮转更稳，不走批量矩形。</summary>
        public CellRect? BulkRect
        {
            get { return null; }
        }

        public float TargetTemperature
        {
            get
            {
                if (targetProperty != null)
                {
                    object value = targetProperty.GetValue(comp, null);
                    if (value is float)
                    {
                        return (float)value;
                    }
                }

                return 21f;
            }
        }

        public bool StabilizePressure
        {
            get { return ReadBool(noVacuumProperty, noVacuumField); }
        }

        public bool StabilizeHumidity
        {
            get { return false; }
        }

        public bool KillWind
        {
            get { return true; }
        }

        private Area GetArea()
        {
            if (areaProperty == null)
            {
                return null;
            }

            return areaProperty.GetValue(comp, null) as Area;
        }

        private bool ReadBool(PropertyInfo property, FieldInfo field)
        {
            if (property != null)
            {
                object value = property.GetValue(comp, null);
                if (value is bool)
                {
                    return (bool)value;
                }
            }

            if (field != null)
            {
                object value = field.GetValue(comp);
                if (value is bool)
                {
                    return (bool)value;
                }
            }

            return false;
        }
    }

    /// <summary>把 CompOmniForceFieldDome 映射成强制稳定区。</summary>
    internal sealed class DomeRegion : IForceFieldRegion
    {
        private const int CacheTicks = 60;

        private readonly ThingComp comp;
        private readonly PropertyInfo rectProperty;
        private readonly MethodInfo isDomeCellMethod;
        private readonly PropertyInfo maintainProperty;
        private readonly PropertyInfo targetProperty;
        private readonly PropertyInfo blockVacuumProperty;

        private List<IntVec3> cachedCells;
        private int cachedAtTick = -9999;

        public DomeRegion(ThingComp comp)
        {
            this.comp = comp;
            Type type = comp.GetType();
            rectProperty = ReflectionCache.Property(type, "DomeRect");
            isDomeCellMethod = ReflectionCache.Method(type, "IsDomeCell", 1);

            Type propsType = comp.props != null ? comp.props.GetType() : null;
            if (propsType != null)
            {
                maintainProperty = ReflectionCache.Property(propsType, "maintainTemperature");
                targetProperty = ReflectionCache.Property(propsType, "targetTemperature");
                blockVacuumProperty = ReflectionCache.Property(propsType, "blockVacuum");
            }
        }

        public string RegionName
        {
            get { return "穹顶#" + (comp.parent != null ? comp.parent.thingIDNumber : 0); }
        }

        public bool Active
        {
            get
            {
                if (comp.parent == null || !comp.parent.Spawned)
                {
                    return false;
                }

                return ReadBool(maintainProperty) || ReadBool(blockVacuumProperty);
            }
        }

        public List<IntVec3> Cells
        {
            get
            {
                int now = GenTicks.TicksGame;
                if (cachedCells != null && now - cachedAtTick < CacheTicks)
                {
                    return cachedCells;
                }

                CellRect rect = Rect;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    cachedCells = null;
                    cachedAtTick = now;
                    return null;
                }

                // 没有 IsDomeCell 就退化为整个矩形；有就逐格过滤，避免把圆外也稳定掉。
                if (isDomeCellMethod == null)
                {
                    List<IntVec3> all = new List<IntVec3>();
                    foreach (IntVec3 cell in rect.Cells)
                    {
                        all.Add(cell);
                    }

                    cachedCells = all;
                }
                else
                {
                    List<IntVec3> filtered = new List<IntVec3>();
                    foreach (IntVec3 cell in rect.Cells)
                    {
                        object inside = isDomeCellMethod.Invoke(comp, new object[] { cell });
                        if (inside is bool && (bool)inside)
                        {
                            filtered.Add(cell);
                        }
                    }

                    cachedCells = filtered;
                }

                cachedAtTick = now;
                return cachedCells;
            }
        }

        public CellRect? BulkRect
        {
            get
            {
                // 只有「整个矩形都是穹顶」时才能批量写；有 IsDomeCell 就逐格。
                if (isDomeCellMethod != null)
                {
                    return null;
                }

                CellRect rect = Rect;
                return rect.Width > 0 && rect.Height > 0 ? (CellRect?)rect : null;
            }
        }

        public float TargetTemperature
        {
            get { return ReadFloat(targetProperty, 21f); }
        }

        public bool StabilizePressure
        {
            get { return ReadBool(blockVacuumProperty); }
        }

        public bool StabilizeHumidity
        {
            get { return false; }
        }

        public bool KillWind
        {
            get { return true; }
        }

        private CellRect Rect
        {
            get
            {
                if (rectProperty != null)
                {
                    object value = rectProperty.GetValue(comp, null);
                    if (value is CellRect)
                    {
                        return (CellRect)value;
                    }
                }

                return new CellRect();
            }
        }

        private bool ReadBool(PropertyInfo property)
        {
            if (property == null)
            {
                return false;
            }

            object value = property.GetValue(comp.props, null);
            return value is bool && (bool)value;
        }

        private float ReadFloat(PropertyInfo property, float fallback)
        {
            if (property == null)
            {
                return fallback;
            }

            object value = property.GetValue(comp.props, null);
            return value is float ? (float)value : fallback;
        }
    }
}
