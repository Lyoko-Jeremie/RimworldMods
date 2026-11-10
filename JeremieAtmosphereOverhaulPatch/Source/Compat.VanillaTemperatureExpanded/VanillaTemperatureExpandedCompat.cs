using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Api;
using Jeremie.AtmosphereOverhaulPatch.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// Vanilla Temperature Expanded（VTE）兼容。
    ///
    /// 问题背景：
    /// VTE 的温控建筑都继承原版 Building_TempControl，因此 AO 的 FacilityCompInjection
    /// 会给它们自动注入 CompTempControlAtmosphere，并由 TempControlSubclassPatches
    /// 接管 TickRare。但 AO 的接管假设了「一台机器只加热或只制冷」：
    /// - AO 用 energyPerSecond 的符号判断冷热；
    /// - AO 的制冷分支固定「South 冷侧 / North 热侧」；
    /// 而 VTE 的 AcUnit 是「按目标温度双向工作」，出风方向由
    /// TemperatureOutputPositionModExtension.offsetNorth 决定，还可能带管网效率。
    ///
    /// 做法：
    /// 1. 给 AO 的 CompTempControlAtmosphere.Run 打 Prefix：目标建筑是 VTE 温控类时返回 false，
    ///    阻止 AO 双写；
    /// 2. 给 VTE 的四个 TickRare 打 Prefix（优先级高于 AO），按 VTE 原语义重算，
    ///    但把结果写成 AO 的 AddTemperatureDelta，保留 ResourceOn 与管网效率。
    ///
    /// 任一步找不到类型/签名就返回 true，退回 AO 接管或 VTE 原逻辑。
    /// </summary>
    public sealed class VanillaTemperatureExpandedCompat : CompatModuleBase
    {
        internal const string AcUnitType = "VanillaTemperatureExpanded.Buildings.Building_AcUnit";
        internal const string HeaterWithOffsetType = "VanillaTemperatureExpanded.Buildings.Building_HeaterWithOffset";
        internal const string TwoCellHeaterType = "VanillaTemperatureExpanded.Buildings.Building_TwoCellHeater";
        internal const string TwoCellCoolerType = "VanillaTemperatureExpanded.Buildings.Building_TwoCellCooler";
        internal const string AoCompType = "AtmosphereOverhaul.Buildings.CompTempControlAtmosphere";

        private static readonly string[] VteTypeNames =
        {
            "Building_AcUnit",
            "Building_HeaterWithOffset",
            "Building_TwoCellHeater",
            "Building_TwoCellCooler",
        };

        private static FieldInfo resourceField;
        private static PropertyInfo resourceOnProperty;
        private static PropertyInfo pipeEfficiencyProperty;
        private static bool resolved;

        public override string PackageId
        {
            get { return "VanillaExpanded.Temperature"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_VTE"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_VTEHint"; }
        }

        public override AoCapability Required
        {
            get { return AoCapability.None; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings == null || AOPatchMod.Settings.EnableVanillaTemperatureExpanded;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableVanillaTemperatureExpanded = value;
            }
        }

        protected override bool TryApply(Harmony harmony)
        {
            Resolve();

            if (!AtmosphereApi.Available)
            {
                Detail = "AO not available";
                return false;
            }

            int patched = 0;

            // 1) 拦住 AO 对 VTE 温控的接管
            Type aoComp = ReflectionCache.Type(AoCompType);
            if (aoComp != null)
            {
                MethodInfo run = ReflectionCache.Method(aoComp, "Run", 1);
                if (run != null)
                {
                    harmony.Patch(run, prefix: new HarmonyMethod(typeof(VanillaTemperatureExpandedCompat), nameof(AoTempControlRunPrefix)));
                    patched++;
                }
            }

            // 2) 自己接管 VTE 的四个 TickRare
            string[] typeNames = { AcUnitType, HeaterWithOffsetType, TwoCellHeaterType, TwoCellCoolerType };
            for (int i = 0; i < typeNames.Length; i++)
            {
                Type type = ReflectionCache.Type(typeNames[i]);
                if (type == null)
                {
                    continue;
                }

                MethodInfo tickRare = type.GetMethod(
                    "TickRare",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);

                if (tickRare == null)
                {
                    continue;
                }

                harmony.Patch(
                    tickRare,
                    prefix: new HarmonyMethod(typeof(VanillaTemperatureExpandedCompat), nameof(TickRarePrefix))
                    {
                        priority = Priority.First,
                    });
                patched++;
            }

            if (patched == 0)
            {
                Detail = "no VTE temperature type found";
                return false;
            }

            Detail = "patched " + patched + " entry point(s)";
            return true;
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;

            Type acUnit = ReflectionCache.Type(AcUnitType);
            if (acUnit != null)
            {
                resourceField = ReflectionCache.Field(acUnit, "resourceComp");
                Type resourceType = ReflectionCache.Type("RimWorld.CompResourceTrader");
                if (resourceType != null)
                {
                    resourceOnProperty = resourceType.GetProperty("ResourceOn", BindingFlags.Public | BindingFlags.Instance);
                }
            }

            Type pipeNet = ReflectionCache.Type("VanillaTemperatureExpanded.PipeSystem.AcPipeNet");
            if (pipeNet == null)
            {
                pipeNet = ReflectionCache.Type("VanillaTemperatureExpanded.AcPipeNet");
            }

            if (pipeNet != null)
            {
                pipeEfficiencyProperty = pipeNet.GetProperty("Efficiency", BindingFlags.Public | BindingFlags.Static);
            }
        }

        /// <summary>AO 的温控接管：目标为 VTE 类型时直接跳过。</summary>
        private static bool AoTempControlRunPrefix(ThingComp __instance, Building_TempControl b)
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.EnableVtePhysical)
            {
                return true;
            }

            if (b == null || b.def == null || b.def.thingClass == null)
            {
                return true;
            }

            if (Array.IndexOf(VteTypeNames, b.def.thingClass.Name) >= 0)
            {
                return false;
            }

            return true;
        }

        private static bool TickRarePrefix(Building_TempControl __instance)
        {
            try
            {
                AOPatchSettings settings = AOPatchMod.Settings;
                if (settings == null || !settings.EnableVtePhysical || !AtmosphereApi.Available)
                {
                    return true;
                }

                if (__instance == null || !__instance.Spawned || __instance.def == null)
                {
                    return true;
                }

                CompTempControl control = __instance.compTempControl;
                CompPowerTrader power = __instance.compPowerTrader;
                if (control == null || power == null)
                {
                    return true;
                }

                if (!power.PowerOn)
                {
                    return true;
                }

                string typeName = __instance.GetType().Name;
                if (typeName == "Building_AcUnit")
                {
                    return RunAcUnit(__instance, control, power);
                }

                if (typeName == "Building_HeaterWithOffset")
                {
                    return RunHeaterWithOffset(__instance, control, power, false);
                }

                if (typeName == "Building_TwoCellHeater")
                {
                    return RunHeaterWithOffset(__instance, control, power, true);
                }

                // TwoCellCooler 暂按单向制冷处理
                if (typeName == "Building_TwoCellCooler")
                {
                    return RunCooler(__instance, control, power);
                }
            }
            catch (Exception exception)
            {
                PatchLog.WarnOnce("VteTickRare", "VTE 温控接管失败，回退原逻辑：" + exception.GetType().Name + ": " + exception.Message);
            }

            return true;
        }

        private static bool RunAcUnit(Building_TempControl building, CompTempControl control, CompPowerTrader power)
        {
            Resolve();

            object resource = resourceField == null ? null : resourceField.GetValue(building);
            if (resource != null && resourceOnProperty != null && !(bool)resourceOnProperty.GetValue(resource, null))
            {
                // 管网没有资源：跟原版一样不工作，但要把功耗降下来
                power.PowerOutput = -power.Props.PowerConsumption * control.Props.lowPowerConsumptionFactor;
                control.operatingAtHighPower = false;
                return false;
            }

            Map map = building.Map;
            IntVec3 cell = building.Position + OffsetFor(building.def).RotatedBy(building.Rotation);
            if (!cell.InBounds(map) || GenGrid.Impassable(cell, map))
            {
                return false;
            }

            float temperature = cell.GetTemperature(map);
            float target = control.TargetTemperature;

            // 与 VTE 一致：目标高于室温就加热，否则制冷（双向）
            bool heating = target >= temperature;

            float energy = Mathf.Abs(control.Props.energyPerSecond) * PipeEfficiency() / 60f;
            float gap = Mathf.Clamp01(Mathf.Abs(target - temperature) / 1f);
            float delta = energy * gap;

            if (heating)
            {
                // 靠近 200℃ 后效率衰减，与 AO 的加热分支保持一致
                float efficiency = temperature <= 200f ? 1f : Mathf.Clamp01((300f - temperature) / 100f);
                delta *= efficiency;
            }

            bool active = Mathf.Abs(delta) > 0.0005f;
            if (active)
            {
                AtmosphereApi.AddTemperatureDelta(map, cell, heating ? delta : -delta);
            }

            power.PowerOutput = active
                ? -power.Props.PowerConsumption
                : -power.Props.PowerConsumption * control.Props.lowPowerConsumptionFactor;
            control.operatingAtHighPower = active;
            return false;
        }

        private static bool RunHeaterWithOffset(Building_TempControl building, CompTempControl control, CompPowerTrader power, bool twoCell)
        {
            Map map = building.Map;
            IntVec3 first = building.Position + OffsetFor(building.def).RotatedBy(building.Rotation);

            bool any = PushHeat(building, control, map, first, twoCell ? 1000f : 120f);
            if (twoCell)
            {
                IntVec3 second = first + IntVec3.East.RotatedBy(building.Rotation);
                any |= PushHeat(building, control, map, second, 1000f);
            }

            power.PowerOutput = any
                ? -power.Props.PowerConsumption
                : -power.Props.PowerConsumption * control.Props.lowPowerConsumptionFactor;
            control.operatingAtHighPower = any;
            return false;
        }

        private static bool PushHeat(Building_TempControl building, CompTempControl control, Map map, IntVec3 cell, float maxTemperature)
        {
            if (!cell.InBounds(map) || GenGrid.Impassable(cell, map))
            {
                return false;
            }

            float temperature = cell.GetTemperature(map);
            float factor = temperature < 20f
                ? 1f
                : temperature > maxTemperature
                    ? 0f
                    : Mathf.InverseLerp(maxTemperature, 20f, temperature);

            if (factor <= 0f)
            {
                return false;
            }

            float energy = Mathf.Abs(control.Props.energyPerSecond) * factor / 60f;
            float target = control.TargetTemperature;
            float gap = Mathf.Clamp01((target - temperature) / 1f);
            float delta = energy * gap;
            if (delta <= 0.0005f)
            {
                return false;
            }

            AtmosphereApi.AddTemperatureDelta(map, cell, delta);
            return true;
        }

        private static bool RunCooler(Building_TempControl building, CompTempControl control, CompPowerTrader power)
        {
            Map map = building.Map;
            IntVec3 cell = building.Position + OffsetFor(building.def).RotatedBy(building.Rotation);
            if (!cell.InBounds(map) || GenGrid.Impassable(cell, map))
            {
                return false;
            }

            float temperature = cell.GetTemperature(map);
            float target = control.TargetTemperature;
            float gap = Mathf.Clamp01((temperature - target) / 1f);
            float energy = Mathf.Abs(control.Props.energyPerSecond) / 60f;
            float delta = energy * gap;

            bool active = delta > 0.0005f;
            if (active)
            {
                AtmosphereApi.AddTemperatureDelta(map, cell, -delta);
            }

            power.PowerOutput = active
                ? -power.Props.PowerConsumption
                : -power.Props.PowerConsumption * control.Props.lowPowerConsumptionFactor;
            control.operatingAtHighPower = active;
            return false;
        }

        private static float PipeEfficiency()
        {
            if (pipeEfficiencyProperty == null)
            {
                return 1f;
            }

            try
            {
                object value = pipeEfficiencyProperty.GetValue(null, null);
                return value is float efficiency && efficiency > 0f ? efficiency : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>读取 VTE 的出风偏移（modExtension 里的 offsetNorth），默认 North。</summary>
        private static IntVec3 OffsetFor(ThingDef def)
        {
            try
            {
                List<DefModExtension> extensions = def.modExtensions;
                if (extensions != null)
                {
                    for (int i = 0; i < extensions.Count; i++)
                    {
                        DefModExtension extension = extensions[i];
                        if (extension == null)
                        {
                            continue;
                        }

                        Type type = extension.GetType();
                        if (type.Name != "TemperatureOutputPositionModExtension")
                        {
                            continue;
                        }

                        FieldInfo field = type.GetField("offsetNorth", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null)
                        {
                            object value = field.GetValue(extension);
                            if (value is IntVec3 offset && offset != IntVec3.Zero)
                            {
                                return offset;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return IntVec3.North;
        }
    }
}
