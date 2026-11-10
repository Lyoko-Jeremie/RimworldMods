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
    /// Good Climate 兼容：把「真空关阀」翻译到 AO 的透气面。
    ///
    /// 背景：
    /// - Good Climate 的强化换热开启时，一旦两侧房间任一侧 Vacuum &gt; 0，
    ///   它会调用 CompFlickable.DoFlick() 关掉原版阀门；
    /// - 但 AO 的 CompAirVent 并不读 CompFlickable，它只看自己的 speedLimit；
    /// - 所以 GC 关阀之后，AO 的透气面仍然开着，真空照样漏。
    ///
    /// 做法：
    /// - 每 60 tick（与 GC 自己的检查同频）扫一遍殖民者建筑里的 Vent；
    /// - GC 强化模式开启且任一侧处于真空时，把 AO 透气面限速压到 0；
    /// - 真空解除后恢复它原本的限速（这是旧版缺失的部分）。
    ///
    /// 全部走反射，不静态引用 AO 程序集；AO 版本变化时最多是「联动失效」，不会报错。
    /// </summary>
    public sealed class GoodClimateCompat : CompatModuleBase
    {
        internal const string GcMapComponentType = "GOS.GoodClimate.GC_MapComponent";
        internal const string GcThingCompType = "GOS.GoodClimate.GC_ThingComp";
        internal const string AoAirVentType = "AtmosphereOverhaul.Buildings.CompAirVent";

        private static readonly Dictionary<int, SavedLimit> SavedLimits = new Dictionary<int, SavedLimit>();

        private static FieldInfo activeField;
        private static MethodInfo setSpeedLimitMethod;
        private static PropertyInfo speedLimitProperty;
        private static Type airVentType;
        private static bool resolved;

        private struct SavedLimit
        {
            public Map Map;
            public float Limit;
        }

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
            get { return "AOPatch_Module_GoodClimateHint"; }
        }

        public override AoCapability Required
        {
            get { return AoCapability.None; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings == null || AOPatchMod.Settings.EnableGoodClimate;
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
            Type mapComponent = ReflectionCache.Type(GcMapComponentType);
            if (mapComponent == null)
            {
                Detail = "GC_MapComponent not found";
                return false;
            }

            ResolveAoSide();

            MethodInfo tick = mapComponent.GetMethod(
                "MapComponentTick",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);

            if (tick == null)
            {
                Detail = "GC_MapComponent.MapComponentTick not found";
                return false;
            }

            if (airVentType == null)
            {
                Detail = "AO CompAirVent not found";
                return false;
            }

            harmony.Patch(tick, postfix: new HarmonyMethod(typeof(GoodClimateCompat), nameof(MapComponentTickPostfix)));
            Detail = "vacuum link armed";
            return true;
        }

        private static void ResolveAoSide()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;

            Type thingComp = ReflectionCache.Type(GcThingCompType);
            if (thingComp != null)
            {
                activeField = ReflectionCache.Field(thingComp, "GoodClimate_IE_Active");
            }

            airVentType = ReflectionCache.Type(AoAirVentType);
            if (airVentType != null)
            {
                setSpeedLimitMethod = ReflectionCache.Method(airVentType, "SetSpeedLimit", 1);
                speedLimitProperty = airVentType.GetProperty(
                    "SpeedLimit",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }

        private static void MapComponentTickPostfix(MapComponent __instance)
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.EnableGcVacuumLink)
            {
                return;
            }

            Map map = __instance == null ? null : __instance.map;
            if (map == null || !map.IsHashIntervalTick(60))
            {
                return;
            }

            ResolveAoSide();
            if (activeField == null || setSpeedLimitMethod == null)
            {
                return;
            }

            HashSet<int> alive = new HashSet<int>();

            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Building building = buildings[i];
                if (building == null || building.def == null || !(building is Building_Vent))
                {
                    continue;
                }

                ThingComp airVent = FindComp(building, airVentType);
                if (airVent == null)
                {
                    continue;
                }

                alive.Add(building.thingIDNumber);

                if (!IsIntensive(building))
                {
                    Restore(building, airVent);
                    continue;
                }

                if (IsVacuum(building, map))
                {
                    Suppress(building, airVent);
                }
                else
                {
                    Restore(building, airVent);
                }
            }

            // 清理已经不存在（被拆/被毁/换地图）的建筑记录，避免字典无限增长
            List<int> stale = null;
            foreach (KeyValuePair<int, SavedLimit> pair in SavedLimits)
            {
                if (pair.Value.Map == map && !alive.Contains(pair.Key))
                {
                    if (stale == null)
                    {
                        stale = new List<int>();
                    }

                    stale.Add(pair.Key);
                }
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    SavedLimits.Remove(stale[i]);
                }
            }
        }

        private static bool IsIntensive(Building building)
        {
            if (activeField == null)
            {
                return false;
            }

            try
            {
                List<ThingComp> comps = building.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    ThingComp comp = comps[i];
                    if (comp != null && activeField.DeclaringType.IsInstanceOfType(comp))
                    {
                        object value = activeField.GetValue(comp);
                        return value is bool active && active;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return false;
        }

        private static bool IsVacuum(Building building, Map map)
        {
            try
            {
                IntVec3 facing = building.Rotation.FacingCell;
                Room front = (building.Position + facing).GetRoom(map);
                Room back = (building.Position - facing).GetRoom(map);
                return (front != null && front.Vacuum > 0f) || (back != null && back.Vacuum > 0f);
            }
            catch
            {
                return false;
            }
        }

        private static void Suppress(Building building, ThingComp airVent)
        {
            int id = building.thingIDNumber;
            if (SavedLimits.ContainsKey(id))
            {
                return;
            }

            float current = GetSpeed(airVent);
            SavedLimits[id] = new SavedLimit
            {
                Map = building.Map,
                Limit = current > 0f ? current : 0f,
            };

            if (current > 0f)
            {
                SetSpeed(airVent, 0f);
                PatchLog.Debug("真空联动：关闭 " + building.def.defName + " @ " + building.Position + "（原限速 " + current.ToString("F1") + "）");
            }
        }

        private static void Restore(Building building, ThingComp airVent)
        {
            SavedLimit saved;
            if (!SavedLimits.TryGetValue(building.thingIDNumber, out saved))
            {
                return;
            }

            SavedLimits.Remove(building.thingIDNumber);
            if (saved.Limit > 0f)
            {
                SetSpeed(airVent, saved.Limit);
                PatchLog.Debug("真空联动：恢复 " + building.def.defName + " @ " + building.Position + "（限速 " + saved.Limit.ToString("F1") + "）");
            }
        }

        private static float GetSpeed(ThingComp airVent)
        {
            if (speedLimitProperty == null)
            {
                return 0f;
            }

            try
            {
                object value = speedLimitProperty.GetValue(airVent, null);
                return value is float speed ? speed : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static void SetSpeed(ThingComp airVent, float speed)
        {
            try
            {
                setSpeedLimitMethod.Invoke(airVent, new object[] { speed });
            }
            catch (Exception exception)
            {
                PatchLog.WarnOnce("GcVacuumSetSpeed", "设置 AO 透气面限速失败：" + exception.Message);
            }
        }

        private static ThingComp FindComp(Building building, Type type)
        {
            if (type == null)
            {
                return null;
            }

            List<ThingComp> comps = building.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] != null && type.IsInstanceOfType(comps[i]))
                {
                    return comps[i];
                }
            }

            return null;
        }
    }
}
