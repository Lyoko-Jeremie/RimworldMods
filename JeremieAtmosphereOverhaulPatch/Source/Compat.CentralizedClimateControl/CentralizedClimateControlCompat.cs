using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Api;
using Jeremie.AtmosphereOverhaulPatch.Util;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// Centralized Climate Control（CCC）兼容。
    ///
    /// CCC 没有被 AO 的 TempControl 接管体系覆盖：它的风口 <c>Building_AirVent</c>
    /// 有自己的 TickRare，直接读写 <c>Room.Vacuum</c> 与 <c>Room.Temperature</c>。
    /// 其中温度写入已经被 AO 的 RoomTemperatureWriteHook 翻译进物理场，
    /// 但真空判定用的仍是原版 Room.Vacuum——在 AO 地图上通常恒为 0，
    /// 于是「往真空房间送气」这件事在 AO 下完全失效。
    ///
    /// 本模块用 Prefix 接管 <c>Building_AirVent.TickRare</c>：
    /// - 完整复刻原逻辑（流量、效率、温差、传热）；
    /// - 真空度改由 AO 气压推算（1 - Pa / 101325）；
    /// - 补真空时写 AddPressureDelta，而不是改 Room.Vacuum；
    /// - 可选（默认关）把风口前 2 格额外写温度与风，做「局部化」。
    ///
    /// 签名对不上时 Prefix 返回 true，退回 CCC 原方法，不会让建筑失效。
    /// </summary>
    public sealed class CentralizedClimateControlCompat : CompatModuleBase
    {
        internal const string AirVentType = "CentralizedClimateControl.Building_AirVent";
        internal const string ConsumerType = "CentralizedClimateControl.CompAirFlowConsumer";

        private static readonly float StandardPressurePascals = 101325f;

        private static Type airVentType;
        private static Type consumerType;
        private static FieldInfo consumerField;
        private static FieldInfo convertedTemperatureField;
        private static PropertyInfo exhaustAirFlowProperty;
        private static PropertyInfo flowEfficiencyProperty;
        private static PropertyInfo thermalEfficiencyProperty;
        private static PropertyInfo airPerSecondProperty;
        private static MethodInfo consumerTickRare;
        private static MethodInfo isOperating;
        private static MethodInfo isActive;
        private static bool resolved;
        private static bool signatureOk;

        public override string PackageId
        {
            get { return "Mlie.CentralizedClimateControl"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_CCC"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_CCCHint"; }
        }

        public override AoCapability Required
        {
            get { return AoCapability.None; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings == null || AOPatchMod.Settings.EnableCentralizedClimateControl;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableCentralizedClimateControl = value;
            }
        }

        protected override bool TryApply(Harmony harmony)
        {
            Resolve();
            if (!signatureOk)
            {
                Detail = "CCC Building_AirVent signature mismatch";
                return false;
            }

            if (!AtmosphereApi.Available)
            {
                Detail = "AO not available";
                return false;
            }

            MethodInfo tickRare = airVentType.GetMethod(
                "TickRare",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);

            if (tickRare == null)
            {
                Detail = "Building_AirVent.TickRare not found";
                return false;
            }

            harmony.Patch(tickRare, prefix: new HarmonyMethod(typeof(CentralizedClimateControlCompat), nameof(TickRarePrefix)));
            Detail = "AO pressure drives vent vacuum";
            return true;
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;

            airVentType = ReflectionCache.Type(AirVentType);
            consumerType = ReflectionCache.Type(ConsumerType);
            if (airVentType == null || consumerType == null)
            {
                return;
            }

            consumerField = ReflectionCache.Field(airVentType, "CompAirFlowConsumer");
            convertedTemperatureField = ReflectionCache.Field(consumerType, "ConvertedTemperature");
            exhaustAirFlowProperty = consumerType.GetProperty("ExhaustAirFlow", BindingFlags.Public | BindingFlags.Instance);
            flowEfficiencyProperty = consumerType.GetProperty("FlowEfficiency", BindingFlags.Public | BindingFlags.Instance);
            thermalEfficiencyProperty = consumerType.GetProperty("ThermalEfficiency", BindingFlags.Public | BindingFlags.Instance);
            airPerSecondProperty = consumerType.GetProperty("AirPerSecondPerHundredCells", BindingFlags.Public | BindingFlags.Instance);
            consumerTickRare = ReflectionCache.Method(consumerType, "TickRare", 0);
            isOperating = ReflectionCache.Method(consumerType, "IsOperating", 0);
            isActive = ReflectionCache.Method(consumerType, "IsActive", 0);

            signatureOk = consumerField != null
                          && convertedTemperatureField != null
                          && exhaustAirFlowProperty != null
                          && flowEfficiencyProperty != null
                          && thermalEfficiencyProperty != null
                          && airPerSecondProperty != null
                          && consumerTickRare != null
                          && isOperating != null
                          && isActive != null;
        }

        /// <summary>返回 false 表示完全接管；任何异常或条件不满足都退回原方法。</summary>
        private static bool TickRarePrefix(object __instance)
        {
            try
            {
                AOPatchSettings settings = AOPatchMod.Settings;
                if (settings == null || !settings.EnableCccVacuum)
                {
                    return true;
                }

                Building building = __instance as Building;
                if (building == null || !building.Spawned)
                {
                    return true;
                }

                Resolve();
                if (!signatureOk || !AtmosphereApi.Available)
                {
                    return true;
                }

                object consumer = consumerField.GetValue(__instance);
                if (consumer == null)
                {
                    return true;
                }

                // 与 CCC 原方法一致：先让 consumer 刷新流量/温度状态
                consumerTickRare.Invoke(consumer, null);
                if (!(bool)isOperating.Invoke(consumer, null) || !(bool)isActive.Invoke(consumer, null))
                {
                    return false;
                }

                Map map = building.Map;
                IntVec3 cell = building.Position + IntVec3.North.RotatedBy(building.Rotation);
                if (GenGrid.Impassable(cell, map))
                {
                    return false;
                }

                float converted = (float)convertedTemperatureField.GetValue(consumer);
                float delta = converted - cell.GetTemperature(map);
                float magnitude = Mathf.Abs(delta);
                if (magnitude > 10f)
                {
                    magnitude = 10f;
                }

                float sign = delta < 0f ? -1f : 1f;
                float thermal = (float)thermalEfficiencyProperty.GetValue(consumer, null);
                int thermalFactor = thermal <= 0f ? 0 : 1;
                float flow = (float)flowEfficiencyProperty.GetValue(consumer, null);
                float efficiency = flow * flow * thermalFactor;
                float exhaust = (float)exhaustAirFlowProperty.GetValue(consumer, null);

                float energy = magnitude * 0.25f * (exhaust / 100f) * efficiency * 4.1666665f * 12f * sign;
                float temperatureDelta = GenTemperature.ControlTemperatureTempChange(cell, map, energy, converted);
                Room room = cell.GetRoomOrAdjacent(map, (RegionType)14);
                if (room == null || Mathf.Approximately(temperatureDelta, 0f))
                {
                    return false;
                }

                // 真空度改由 AO 气压推算：1 个大气压 = 无真空
                float vacuum = 0f;
                float pascals;
                if (AtmosphereApi.TryGetPressurePascals(map, cell, out pascals))
                {
                    vacuum = Mathf.Clamp01(1f - pascals / StandardPressurePascals);
                }
                else
                {
                    vacuum = room.Vacuum;
                }

                if (vacuum > 0f)
                {
                    float airPerSecond = (float)airPerSecondProperty.GetValue(consumer, null);
                    float vacuumRecovered = 100f / Mathf.Max(1, room.CellCount) * airPerSecond * 4.1666665f * flow;

                    // 把「真空度恢复」换算成 AO 的气压增量（solver 单位 = 1000 Pa）
                    float deltaPascals = vacuumRecovered * StandardPressurePascals;
                    float solverDelta = deltaPascals / AtmosphereApi.SolverPressureToPascals;
                    if (!AtmosphereApi.AddPressureDelta(map, cell, solverDelta))
                    {
                        // AO 不可写时退回原版行为，至少不让建筑彻底失效
                        room.Vacuum = Mathf.Max(0f, room.Vacuum - vacuumRecovered);
                    }

                    if (vacuum > vacuumRecovered)
                    {
                        temperatureDelta *= 1f - vacuum;
                    }
                }

                // 温度仍写 Room.Temperature，交给 AO 的 RoomTemperatureWriteHook 翻译成逐格注入
                room.Temperature += temperatureDelta;

                if (settings.EnableCccLocalizedVent)
                {
                    ApplyLocalized(building, map, cell, temperatureDelta, sign);
                }

                return false;
            }
            catch (Exception exception)
            {
                PatchLog.WarnOnce("CccTickRare", "CCC 风口接管失败，回退原方法：" + exception.GetType().Name + ": " + exception.Message);
                return true;
            }
        }

        /// <summary>可选：把温度与风写到风口前两格，让效果更「看起来像从风口吹出来」。</summary>
        private static void ApplyLocalized(Building building, Map map, IntVec3 cell, float temperatureDelta, float sign)
        {
            IntVec3 direction = IntVec3.North.RotatedBy(building.Rotation);
            IntVec3 second = cell + direction;

            AtmosphereApi.AddTemperatureDelta(map, cell, temperatureDelta * 0.15f);
            if (second.InBounds(map) && !GenGrid.Impassable(second, map))
            {
                AtmosphereApi.AddTemperatureDelta(map, second, temperatureDelta * 0.08f);
            }

            Vector2 wind = new Vector2(direction.x, direction.z) * 0.5f * sign;
            AtmosphereApi.AddWindImpulse(map, cell, wind);
        }
    }
}
