using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_CompBiosphere : CompProperties
    {
        // 默认复用状态分配终端已有的“OmniBiosphere_Protect”hediff；XML 可直接传 HediffDef 或 defName。
        public bool applyFriendlyHediff = true;
        public bool removeFriendlyHediffWhenLeaving = true;
        public string friendlyHediffDefName = "OmniBiosphere_Protect";

        private HediffDef friendlyHediffDefToUseCached;

        public HediffDef FriendlyHediffDefToUse
        {
            get
            {
                if (friendlyHediffDefToUseCached == null && !friendlyHediffDefName.NullOrEmpty())
                {
                    friendlyHediffDefToUseCached = HediffDef.Named(friendlyHediffDefName);
                }
                return friendlyHediffDefToUseCached;
            }
        }

        public CompProperties_CompBiosphere()
        {
            this.compClass = typeof(CompBiosphere);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompBiosphereTex
    {
        public static readonly Texture2D IconSelectArea =
            ContentFinder<Texture2D>.Get("UI/Commands/CompBiosphere_SelectArea", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconGrowthMode =
            ContentFinder<Texture2D>.Get("UI/Commands/CompBiosphere_GrowthMode", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconTemperature =
            ContentFinder<Texture2D>.Get("UI/Commands/CompBiosphere_Temperature", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconTemperatureChange =
            ContentFinder<Texture2D>.Get("UI/Commands/CompBiosphere_IconTemperatureChange", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconLightingMode =
            ContentFinder<Texture2D>.Get("UI/Commands/CompBiosphere_LightingMode", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconNoVacuum =
            ContentFinder<Texture2D>.Get("UI/Commands/CompBiosphere_NoVacuum", false) ?? BaseContent.WhiteTex;
    }

    public enum PlantGrowthMode
    {
        Normal,     // 正常生长
        Forced,     // 强制生长 (至少100%)
        Stopped,    // 停止生长
        Cleared     // 清除并阻止
    }

    public enum LightingMode
    {
        None,       // 不改变
        Light,      // 灯光
        Sunlight    // 太阳灯
    }

    /// <summary>
    /// 生物圈控制组件
    /// </summary>
    public class CompBiosphere : ThingComp
    {
        public CompProperties_CompBiosphere Props => (CompProperties_CompBiosphere)props;
        public string areaName = null; // 选定的活动区名称
        
        private PlantGrowthMode _growthMode = PlantGrowthMode.Normal;
        public PlantGrowthMode growthMode
        {
            get => _growthMode;
            set
            {
                if (_growthMode != value)
                {
                    _growthMode = value;
                    CompBiosphereManager.NotifySettingsChanged(this);
                    ApplyEffects(); // 变更时立即生效一次
                }
            }
        }

        private bool _controlTemperature = false;
        public bool controlTemperature
        {
            get => _controlTemperature;
            set
            {
                if (_controlTemperature != value)
                {
                    _controlTemperature = value;
                    CompBiosphereManager.NotifySettingsChanged(this);
                    ApplyEffects(); // 变更时立即生效一次
                }
            }
        }

        private float _targetTemperature = 21f;
        public float targetTemperature
        {
            get => _targetTemperature;
            set
            {
                if (_targetTemperature != value)
                {
                    _targetTemperature = value;
                    CompBiosphereManager.NotifySettingsChanged(this);
                    ApplyEffects(); // 变更时立即生效一次
                }
            }
        }

        // private bool _ensureNoVacuum = false;
        // public bool ensureNoVacuum
        // {
        //     get => _ensureNoVacuum;
        //     set
        //     {
        //         if (_ensureNoVacuum != value)
        //         {
        //             _ensureNoVacuum = value;
        //             CompBiosphereManager.NotifySettingsChanged(this);
        //             ApplyEffects(); // 变更时立即生效一次
        //         }
        //     }
        // }

        private LightingMode _lightingMode = LightingMode.None;
        public LightingMode lightingMode
        {
            get => _lightingMode;
            set
            {
                if (_lightingMode != value)
                {
                    _lightingMode = value;
                    DirtyGlowInArea(parent.Map);
                    CompBiosphereManager.NotifySettingsChanged(this);
                    ApplyEffects(); // 变更时立即生效一次
                }
            }
        }

        private Area selectedArea;
        private bool areaFound = false;

        public Area SelectedArea
        {
            get
            {
                if (!areaFound || selectedArea == null || selectedArea.Map != parent.Map)
                {
                    RefreshArea();
                }
                return selectedArea;
            }
        }

        public void RefreshArea()
        {
            Area oldArea = selectedArea;
            areaFound = false;
            selectedArea = null;
            if (!string.IsNullOrEmpty(areaName) && parent.Map != null)
            {
                selectedArea = parent.Map.areaManager.GetLabeled(areaName);
                if (selectedArea != null)
                {
                    areaFound = true;
                }
            }
            if (oldArea != selectedArea)
            {
                CompBiosphereManager.UpdateAreaMapping(this, oldArea, selectedArea);
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad && string.IsNullOrEmpty(areaName) && parent.Map != null)
            {
                areaName = parent.Map.areaManager.Home.Label;
            }
            CompBiosphereManager.Register(this);
            RefreshArea();
            ApplyEffects(); // 生成时执行一次全量同步
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            CompBiosphereManager.Deregister(this, previousMap);
            base.PostDestroy(mode, previousMap);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref areaName, "areaName");
            Scribe_Values.Look(ref _growthMode, "growthMode", PlantGrowthMode.Normal);
            Scribe_Values.Look(ref _controlTemperature, "controlTemperature", false);
            Scribe_Values.Look(ref _targetTemperature, "targetTemperature", 21f);
            // Scribe_Values.Look(ref _ensureNoVacuum, "ensureNoVacuum", false);
            Scribe_Values.Look(ref _lightingMode, "lightingMode", LightingMode.None);
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            SelectedArea?.MarkForDraw();

            var comps = CompBiosphereManager.GetCompsForMap(parent.Map);
            if (comps != null)
            {
                foreach (var comp in comps)
                {
                    TargetHighlighter.Highlight(comp.parent);
                }
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // 1. 选择区域
            yield return new Command_Action
            {
                defaultLabel = (string.IsNullOrEmpty(areaName) ? (string)"Biosphere_SelectArea".Translate() : areaName),
                defaultDesc = "Biosphere_SelectAreaDesc".Translate(),
                icon = CompBiosphereTex.IconSelectArea,
                action = () =>
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    options.Add(new FloatMenuOption("None".Translate(), () =>
                    {
                        DirtyGlowInArea(parent.Map); // Dirty old area
                        areaName = null;
                        RefreshArea();
                        DirtyGlowInArea(parent.Map); // Dirty new area (none)
                    }));
                    foreach (Area area in parent.Map.areaManager.AllAreas)
                    {
                        options.Add(new FloatMenuOption(area.Label, () =>
                        {
                            DirtyGlowInArea(parent.Map); // Dirty old area
                            areaName = area.Label;
                            RefreshArea();
                            DirtyGlowInArea(parent.Map); // Dirty new area
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            // 2. 选择生长模式
            yield return new Command_Action
            {
                defaultLabel = ("Biosphere_GrowthMode_" + growthMode.ToString()).Translate(),
                defaultDesc = "Biosphere_GrowthModeDesc".Translate(),
                icon = CompBiosphereTex.IconGrowthMode,
                action = () =>
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (PlantGrowthMode mode in Enum.GetValues(typeof(PlantGrowthMode)))
                    {
                        options.Add(new FloatMenuOption(("Biosphere_GrowthMode_" + mode.ToString()).Translate(), () =>
                        {
                            growthMode = mode;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            // 3. 是否启用+温度设置
            yield return new Command_Toggle
            {
                defaultLabel = "Biosphere_ControlTemperature".Translate(),
                defaultDesc = "Biosphere_ControlTemperatureDesc".Translate(),
                icon = CompBiosphereTex.IconTemperature,
                isActive = () => controlTemperature,
                toggleAction = () => controlTemperature = !controlTemperature
            };
            
            if (controlTemperature)
            {
                yield return new Command_Action
                {
                    defaultLabel = targetTemperature.ToStringTemperature(),
                    defaultDesc = "Biosphere_SetTemperatureDesc".Translate(),
                    icon = CompBiosphereTex.IconTemperatureChange,
                    action = () => Find.WindowStack.Add(new Dialog_CompBiosphere_Temperature(this))
                };
            }
            
            // 4. 光照模式
            yield return new Command_Action
            {
                defaultLabel = ("Biosphere_LightingMode_" + lightingMode.ToString()).Translate(),
                defaultDesc = "Biosphere_LightingModeDesc".Translate(),
                icon = CompBiosphereTex.IconLightingMode,
                action = () =>
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (LightingMode mode in Enum.GetValues(typeof(LightingMode)))
                    {
                        options.Add(new FloatMenuOption(("Biosphere_LightingMode_" + mode.ToString()).Translate(), () =>
                        {
                            lightingMode = mode;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
            
            // // 5. 排除真空
            // yield return new Command_Toggle
            // {
            //     defaultLabel = "Biosphere_EnsureNoVacuum".Translate(),
            //     defaultDesc = "Biosphere_EnsureNoVacuumDesc".Translate(),
            //     icon = CompBiosphereTex.IconNoVacuum,
            //     isActive = () => ensureNoVacuum,
            //     toggleAction = () => ensureNoVacuum = !ensureNoVacuum
            // };
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            // 移除原本每250tick的 ApplyEffects() 全量遍历，改为事件驱动触发。
        }
        
        private float cachedSkyGlow = -1f;
        private const int SkyGlowDirtyTickInterval = 30; // 降低到30tick，约0.5秒
        private const float SkyGlowDirtyThreshold = 0.005f;

        public override void CompTick()
        {
            base.CompTick();
            // 移除原本每250tick的 ApplyEffects() 全量遍历，改为事件驱动触发。
            
            if (lightingMode != LightingMode.None && parent.IsHashIntervalTick(SkyGlowDirtyTickInterval))
            {
                float curSkyGlow = parent.Map.skyManager.CurSkyGlow;
                if (Mathf.Abs(cachedSkyGlow - curSkyGlow) > SkyGlowDirtyThreshold)
                {
                    cachedSkyGlow = curSkyGlow;
                    DirtyGlowInArea(parent.Map);
                }
            }
        }

        private void ApplyEffects()
        {
            Area area = SelectedArea;
            if (area == null || area.ActiveCells.Count() == 0) return;

            Map map = parent.Map;
            foreach (IntVec3 cell in area.ActiveCells)
            {
                // 1. 植物生长控制
                ApplyPlantGrowth(cell, map);

                // 2. 温度控制
                if (controlTemperature)
                {
                    GenTemperature.PushHeat(cell, map, 0.01f); // 象征性的推一点热量，为了让原版系统注意到
                    // 实际强制设置温度将通过 Patch 实现以保证稳定性
                }
                
                // 3. 这里的 确保无真空/照明/阳光 将通过 Patch 实现
            }
        }

        private void ApplyPlantGrowth(IntVec3 cell, Map map)
        {
            if (growthMode == PlantGrowthMode.Normal) return;

            List<Thing> things = cell.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                if (things[i] is Plant plant)
                {
                    switch (growthMode)
                    {
                        case PlantGrowthMode.Forced:
                            if (plant.Growth < 1f)
                            {
                                plant.Growth = 1f;
                                plant.Map.mapDrawer.MapMeshDirty(plant.Position, MapMeshFlagDefOf.Things);
                            }
                            break;
                        case PlantGrowthMode.Stopped:
                            // 停止生长将通过 Patch Plant.Tick 实现
                            break;
                        case PlantGrowthMode.Cleared:
                            plant.Destroy();
                            break;
                    }
                }
            }
        }

        public void DirtyGlowInArea(Map map)
        {
            if (map?.glowGrid == null) return;
            Area area = SelectedArea;
            if (area == null) return;

            foreach (IntVec3 c in area.ActiveCells)
            {
                map.glowGrid.DirtyCell(c);
            }
        }

        private bool isSyncing = false;
        public void SyncSettingsFrom(CompBiosphere other)
        {
            if (isSyncing) return;
            isSyncing = true;
            try
            {
                this.growthMode = other.growthMode;
                this.controlTemperature = other.controlTemperature;
                this.targetTemperature = other.targetTemperature;
                // this.ensureNoVacuum = other.ensureNoVacuum;
                this.lightingMode = other.lightingMode;
                // 注意：属性 Setter 内部已经调用了 ApplyEffects()
            }
            finally
            {
                isSyncing = false;
            }
        }
    }
}