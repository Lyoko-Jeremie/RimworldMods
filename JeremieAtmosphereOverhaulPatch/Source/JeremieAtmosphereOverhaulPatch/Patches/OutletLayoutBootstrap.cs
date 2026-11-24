using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Comps;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Patches
{
    /// <summary>
    /// 把 CompProperties_TempOutputShape 的形状翻译成 AO 的 XML 出风口布局（AO_OutletLayout）。
    ///
    /// AO 的 RunHeater/RunCooler 已经处理了「多格出风口、效率、热侧倍率、除湿」，
    /// 挂上布局后不再需要我们在 FacilityPatchUtility.RunTempControl 上打 Prefix 自己算速率。
    /// 布局按建筑朝北坐标书写，AO 会按 Rotation 旋转。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OutletLayoutBootstrap
    {
        private const string LayoutTypeName = "AtmosphereOverhaul.Buildings.AO_OutletLayout";

        private static bool _applied;
        private static bool _attempted;

        static OutletLayoutBootstrap()
        {
            TryApplyAll();
        }

        /// <summary>
        /// 是否已至少给一个 def 挂上 AO_OutletLayout。挂上后温控改走 AO 原生实现，
        /// TempOutputShapePatch 不再需要安装。
        /// </summary>
        public static bool Applied
        {
            get { return _applied; }
        }

        /// <summary>幂等：把每个挂了 CompProperties_TempOutputShape 的 def 翻译成 AO_OutletLayout。</summary>
        public static bool TryApplyAll()
        {
            if (_attempted)
            {
                return _applied;
            }
            _attempted = true;

            Type layoutType = AccessTools.TypeByName(LayoutTypeName);
            if (layoutType == null)
            {
                return false;
            }

            FieldInfo heatField = AccessTools.Field(layoutType, "heatCells");
            FieldInfo coldField = AccessTools.Field(layoutType, "coldCells");
            FieldInfo hotField = AccessTools.Field(layoutType, "hotCells");
            FieldInfo factorField = AccessTools.Field(layoutType, "hotSideFactor");
            if (heatField == null || coldField == null || hotField == null || factorField == null)
            {
                return false;
            }

            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef def = defs[i];
                CompProperties_TempOutputShape props = FindShapeProps(def);
                if (props == null)
                {
                    continue;
                }
                if (HasLayout(def, layoutType))
                {
                    // 手工 XML 已经配了布局，尊重它。
                    _applied = true;
                    continue;
                }

                object layout;
                try
                {
                    layout = Activator.CreateInstance(layoutType);
                }
                catch (Exception)
                {
                    continue;
                }
                if (layout == null)
                {
                    continue;
                }

                List<IntVec3> heat = null;
                List<IntVec3> cold = null;
                List<IntVec3> hot = null;
                switch (props.shape)
                {
                    case TempOutputShape.TwoCellHeater:
                        heat = new List<IntVec3> { IntVec3.North, IntVec3.North + IntVec3.East };
                        break;
                    case TempOutputShape.TwoCellCooler:
                        cold = new List<IntVec3> { IntVec3.South, IntVec3.South + IntVec3.East };
                        hot = new List<IntVec3> { IntVec3.North, IntVec3.North + IntVec3.East };
                        break;
                    case TempOutputShape.WallOffset:
                        // 挂壁加热器：AO 的 RunHeater 只在有 heatCells 时用它，
                        // 否则回退到 parent.Position，会把热量写进墙里。
                        heat = new List<IntVec3> { props.wallOffsetNorth };
                        break;
                    default:
                        // Single：加热器默认写自身格；制冷机冷南热北各一格。
                        cold = new List<IntVec3> { IntVec3.South };
                        hot = new List<IntVec3> { IntVec3.North };
                        break;
                }

                heatField.SetValue(layout, heat);
                coldField.SetValue(layout, cold);
                hotField.SetValue(layout, hot);
                factorField.SetValue(layout, props.hotReturnFactor);

                if (def.modExtensions == null)
                {
                    def.modExtensions = new List<DefModExtension>();
                }
                def.modExtensions.Add((DefModExtension)layout);
                _applied = true;
            }

            if (_applied)
            {
                Log.Message("[AOPatch] 温控出风口形状已写入 AO_OutletLayout，改走 AO 原生实现。");
            }
            return _applied;
        }

        private static CompProperties_TempOutputShape FindShapeProps(ThingDef def)
        {
            List<CompProperties> comps = def.comps;
            if (comps == null)
            {
                return null;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                CompProperties_TempOutputShape props = comps[i] as CompProperties_TempOutputShape;
                if (props != null)
                {
                    return props;
                }
            }
            return null;
        }

        private static bool HasLayout(ThingDef def, Type layoutType)
        {
            List<DefModExtension> extensions = def.modExtensions;
            if (extensions == null)
            {
                return false;
            }
            for (int i = 0; i < extensions.Count; i++)
            {
                DefModExtension extension = extensions[i];
                if (extension != null && extension.GetType() == layoutType)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
