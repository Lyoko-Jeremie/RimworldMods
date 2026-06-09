using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_CompBiosphere : CompProperties
    {
        public CompProperties_CompBiosphere()
        {
            this.compClass = typeof(CompBiosphere);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompBiosphereTex
    {
        public static readonly Texture2D IconBiosphereUI =
            ContentFinder<Texture2D>.Get("UI/Commands/IconBiosphereUI", true) ?? BaseContent.WhiteTex;
    }

    public enum PlantGrowthMode
    {
        Normal,     // 正常生长
        Forced,     // 强制生长 (至少100%)
        Stopped,    // 停止生长
        Cleared     // 清除并阻止
    }

    /// <summary>
    /// 生物圈控制组件
    /// </summary>
    public class CompBiosphere : ThingComp
    {
        public string areaName = null; // 选定的活动区名称
        public PlantGrowthMode growthMode = PlantGrowthMode.Normal;
        public bool controlTemperature = false;
        public float targetTemperature = 21f;
        public bool ensureNoVacuum = false;
        public bool ensureLight = false;
        public bool ensureSunlight = false;

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
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            CompBiosphereManager.Register(this);
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
            Scribe_Values.Look(ref growthMode, "growthMode", PlantGrowthMode.Normal);
            Scribe_Values.Look(ref controlTemperature, "controlTemperature", false);
            Scribe_Values.Look(ref targetTemperature, "targetTemperature", 21f);
            Scribe_Values.Look(ref ensureNoVacuum, "ensureNoVacuum", false);
            Scribe_Values.Look(ref ensureLight, "ensureLight", false);
            Scribe_Values.Look(ref ensureSunlight, "ensureSunlight", false);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action
            {
                defaultLabel = "OpenBiosphereUI".Translate(),
                defaultDesc = "OpenBiosphereUIDesc".Translate(),
                icon = CompBiosphereTex.IconBiosphereUI,
                action = () => Find.WindowStack.Add(new Dialog_CompBiosphere(this))
            };
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            ApplyEffects();
        }
        
        public override void CompTick()
        {
            base.CompTick();
            if (parent.IsHashIntervalTick(250)) // 每250tick执行一次
            {
                ApplyEffects();
            }
        }

        private void ApplyEffects()
        {
            Area area = SelectedArea;
            if (area == null) return;

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
                                // plant.RefreshRequired(); // 移除了不存在的方法
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
    }
}