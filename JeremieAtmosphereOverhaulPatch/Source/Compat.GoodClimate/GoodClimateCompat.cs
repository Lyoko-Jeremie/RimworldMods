using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Api;
using Jeremie.AtmosphereOverhaulPatch.Util;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// Good Climate（GOS.GoodClimate）兼容。
    ///
    /// 问题：Good Climate 的「强力换热」在检测到任一房间 Room.Vacuum &gt; 0 时，
    /// 会 DoFlick 关闭原版 CompFlickable，向玩家发出「气压骤降」信件。
    /// 但 Atmosphere Overhaul 的透气面只认自己的 CompAirVent.speedLimit，
    /// 对 CompFlickable 的信号仅做 MarkDirty，不会把透气口关上，
    /// 于是「已关阀」的通风口在 AO 气压场里仍然漏气。
    ///
    /// 做法：在 GC_MapComponent.MapComponentTick 之后补一刀——
    /// 当 GC 判定真空并关阀时，把同一建筑上的 AO CompAirVent 限速设为 0。
    ///
    /// 为什么挂在 MapComponentTick 而不是 Harmony 补 GC_ThingComp：
    /// GC 的真空判定全部集中在 MapComponent 的 60 tick 循环里，挂这里只需一次补丁、
    /// 不必触碰 GC 的私有状态，也不会影响它自己的 UI 逻辑。
    /// </summary>
    public sealed class GoodClimateCompat : CompatModuleBase
    {
        private const string GcMapComponentType = "GOS.GoodClimate.GC_MapComponent";
        private const string GcThingCompType = "GOS.GoodClimate.GC_ThingComp";
        private const string AoAirVentType = "AtmosphereOverhaul.Buildings.CompAirVent";

        public override string PackageId
        {
            get { return "GOS.GoodClimate"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_GoodClimate"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_GoodClimateDesc"; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings != null && AOPatchMod.Settings.EnableGoodClimate;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableGoodClimate = value;
            }
        }

        protected override bool TryApply(Harmony harmony)
        {
            if (AOPatchMod.Settings != null && !AOPatchMod.Settings.EnableGcVacuumLink)
            {
                Detail = "vacuum-link-off";
                return false;
            }

            Type mapComponentType = ReflectionCache.Type(GcMapComponentType);
            if (mapComponentType == null)
            {
                Detail = "GC_MapComponent missing";
                return false;
            }

            MethodInfo tick = ReflectionCache.Method(mapComponentType, "MapComponentTick", 0);
            if (tick == null)
            {
                Detail = "MapComponentTick missing";
                return false;
            }

            MethodInfo postfix = typeof(GoodClimateCompat).GetMethod(
                nameof(Postfix_MapComponentTick),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (postfix == null)
            {
                Detail = "postfix missing";
                return false;
            }

            harmony.Patch(tick, postfix: new HarmonyMethod(postfix));
            Detail = "patched " + mapComponentType.Name + ".MapComponentTick";
            return true;
        }

        /// <summary>
        /// GC 的 MapComponentTick 每 tick 被调用，内部自己用 IsHashIntervalTick(60) 限流。
        /// 这里保持同样的节流，避免额外的逐 tick 开销。
        /// </summary>
        private static void Postfix_MapComponentTick(MapComponent __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                Map map = __instance.map;
                if (map == null || !map.IsHashIntervalTick(60))
                {
                    return;
                }

                AOPatchSettings settings = AOPatchMod.Settings;
                if (settings == null || !settings.MasterEnabled || !settings.EnableGoodClimate || !settings.EnableGcVacuumLink)
                {
                    return;
                }

                Type gcCompType = ReflectionCache.Type(GcThingCompType);
                Type aoVentType = ReflectionCache.Type(AoAirVentType);
                if (gcCompType == null || aoVentType == null)
                {
                    return;
                }

                FieldInfo activeField = ReflectionCache.Field(gcCompType, "GoodClimate_IE_Active");
                MethodInfo setSpeedLimit = ReflectionCache.Method(aoVentType, "SetSpeedLimit", 1);
                PropertyInfo speedLimitProperty = ReflectionCache.Property(aoVentType, "SpeedLimit");
                if (activeField == null || setSpeedLimit == null)
                {
                    return;
                }

                List<Building> buildings = map.listerBuildings.allBuildingsColonist;
                for (int i = 0; i < buildings.Count; i++)
                {
                    Building building = buildings[i];
                    if (building == null || !(building is Building_Vent))
                    {
                        continue;
                    }

                    object gcComp = FindComp(building, gcCompType);
                    if (gcComp == null)
                    {
                        continue;
                    }

                    if (!(ReflectionCache.GetFieldValue(activeField, gcComp) is bool active) || !active)
                    {
                        continue;
                    }

                    Room roomA = (building.Position + building.Rotation.FacingCell).GetRoom(map);
                    Room roomB = (building.Position - building.Rotation.FacingCell).GetRoom(map);
                    if (roomA == null || roomB == null)
                    {
                        continue;
                    }

                    if (roomA.Vacuum <= 0f && roomB.Vacuum <= 0f)
                    {
                        continue;
                    }

                    object aoVent = FindComp(building, aoVentType);
                    if (aoVent == null)
                    {
                        continue;
                    }

                    // 已经是 0 就不重复触发 MarkDirty（会重建地图障碍掩码）
                    if (speedLimitProperty != null)
                    {
                        try
                        {
                            object current = speedLimitProperty.GetValue(aoVent, null);
                            if (current is float speed && speed <= 0.001f)
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            // 读不到就当作需要关，交给 SetSpeedLimit 内部去判重
                        }
                    }

                    try
                    {
                        setSpeedLimit.Invoke(aoVent, new object[] { 0f });
                        PatchLog.Debug("真空联动：已把 " + building.LabelShortCap + " 的 AO 透气面限速设为 0");
                    }
                    catch (Exception exception)
                    {
                        PatchLog.Warn("关闭 AO 透气面失败：" + exception.GetType().Name + ": " + exception.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                PatchLog.Warn("Good Climate 真空联动执行异常：" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static ThingComp FindComp(Building building, Type compType)
        {
            List<ThingComp> comps = building.AllComps;
            if (comps == null)
            {
                return null;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp != null && comp.GetType() == compType)
                {
                    return comp;
                }
            }

            return null;
        }
    }
}
