using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Bridge
{
    /// <summary>
    /// 写一个 AO 连续源所需字段的子集。
    /// 用本地结构体承载，避免宿主工程直接引用 AtmosphereOverhaul 程序集。
    /// </summary>
    public struct SlotRates
    {
        public float TemperaturePerTick;
        public Vector2 WindPerTick;
        public bool MaintainTargetWind;
        public Vector2 TargetWind;
        public float TargetWindBlendPerTick;
    }

    /// <summary>
    /// 对 AtmosphereOverhaul 连续源 API 的反射桥。
    ///
    /// 设计取舍：
    /// 1. 全部按名字解析，AO 缺省、改名或换签名时整体降级为「未接管」，
    ///    由调用方回退到 AO 自己的行为，绝不让游戏启动失败。
    /// 2. 只暴露本 mod 真正需要的最小面：写一个连续源、读一格气温、
    ///    读 AO 设施扩展的三个字段。不碰 AO 的引擎内部状态。
    /// 3. 反射结果按类型缓存在静态字段里，热路径（每 tick 每格）不重复解析。
    ///
    /// 单位约定（来自 AO 源码 AtmosphereMapComponent.ApplyContinuousSourcesUnsafe）：
    /// TemperaturePerTick 的单位是「摄氏度 / 游戏 tick」，不是「每模拟步」。
    /// 积分时乘的是 ticksGame - LastAppliedTick，与 SimulationIntervalTicks 无关。
    /// </summary>
    public static class AoBridge
    {
        private const string RatesTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereSourceRates";
        private const string SlotsTypeName = "AtmosphereOverhaul.Buildings.SourceSlots";
        private const string InterfaceTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereInterface";
        private const string TempControlTypeName = "AtmosphereOverhaul.Buildings.CompTempControlAtmosphere";
        private const string FacilityExtTypeName = "AtmosphereOverhaul.Buildings.AO_FacilityExtension";

        private static bool _resolved;
        private static bool _available;
        private static string _failure;

        private static Type _ratesType;
        private static Type _slotsType;
        private static Type _tempControlType;
        private static Type _facilityExtType;

        private static ConstructorInfo _slotsCtor;
        private static MethodInfo _slotsSet;
        private static MethodInfo _slotsZeroFrom;
        private static MethodInfo _slotsRemoveAll;
        private static FieldInfo _tempControlSlotsField;
        private static MethodInfo _tryGetTemperature;
        private static MethodInfo _facilityExtFor;
        private static FieldInfo _extHeatFactor;
        private static FieldInfo _extOutletWindSpeed;
        private static FieldInfo _extOutletToleranceK;

        private static Type _atmosphereComponentType;
        private static MethodInfo _addVolumeSource;
        private static MethodInfo _addVolumeSourceAtTemperature;
        private static MethodInfo _staticPressure;

        private static readonly Dictionary<string, FieldInfo> RateFields = new Dictionary<string, FieldInfo>();

        /// <summary>AO 连续源 API 是否完整可用。</summary>
        public static bool Available
        {
            get { Resolve(); return _available; }
        }

        /// <summary>不可用时的具体原因，用于日志。</summary>
        public static string Failure
        {
            get { Resolve(); return _failure; }
        }

        /// <summary>体积源接口是否可用（CCC 气袋这类需要往 AO 写散度的模块要先查这个）。</summary>
        public static bool VolumeSourceAvailable
        {
            get { Resolve(); return _addVolumeSource != null && _addVolumeSourceAtTemperature != null; }
        }

        /// <summary>解析一次并缓存。可安全重复调用。</summary>
        public static void Resolve()
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;

            try
            {
                _ratesType = AccessTools.TypeByName(RatesTypeName);
                _slotsType = AccessTools.TypeByName(SlotsTypeName);
                Type interfaceType = AccessTools.TypeByName(InterfaceTypeName);
                _tempControlType = AccessTools.TypeByName(TempControlTypeName);
                _facilityExtType = AccessTools.TypeByName(FacilityExtTypeName);

                if (_ratesType == null || _slotsType == null || interfaceType == null)
                {
                    _failure = "AO 核心类型缺失：" + RatesTypeName + " / " + SlotsTypeName + " / " + InterfaceTypeName;
                    return;
                }

                _tryGetTemperature = AccessTools.Method(
                    interfaceType,
                    "TryGetTemperature",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType() });

                _slotsCtor = AccessTools.Constructor(_slotsType, new[] { typeof(string) });
                _slotsSet = AccessTools.Method(
                    _slotsType,
                    "Set",
                    new[] { typeof(Thing), typeof(int), typeof(IntVec3), _ratesType });
                _slotsZeroFrom = AccessTools.Method(_slotsType, "ZeroFrom", new[] { typeof(Thing), typeof(int) });
                _slotsRemoveAll = AccessTools.Method(_slotsType, "RemoveAll", new[] { typeof(Thing), typeof(Map) });

                if (_tempControlType != null)
                {
                    _tempControlSlotsField = AccessTools.Field(_tempControlType, "slots");
                }

                if (_facilityExtType != null)
                {
                    _facilityExtFor = AccessTools.Method(_facilityExtType, "For", new[] { typeof(ThingDef) });
                    _extHeatFactor = AccessTools.Field(_facilityExtType, "heatFactor");
                    _extOutletWindSpeed = AccessTools.Field(_facilityExtType, "outletWindSpeed");
                    _extOutletToleranceK = AccessTools.Field(_facilityExtType, "outletToleranceK");
                }

                // 体积源与静态气压：给 CCC 气袋这类「需要向 AO 写散度」的扩展用。
                // 解析失败不算致命，调用方各自回退。
                _atmosphereComponentType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
                _addVolumeSource = AccessTools.Method(
                    interfaceType,
                    "AddVolumeSource",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float), typeof(float), typeof(float) });
                _addVolumeSourceAtTemperature = AccessTools.Method(
                    interfaceType,
                    "AddVolumeSourceAtTemperature",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float), typeof(float), typeof(float) });
                if (_atmosphereComponentType != null)
                {
                    _staticPressure = AccessTools.Method(
                        _atmosphereComponentType,
                        "TryGetStaticPressurePascals",
                        new[] { typeof(IntVec3), typeof(float).MakeByRefType() });
                }

                if (_tryGetTemperature == null) { _failure = "AtmosphereInterface.TryGetTemperature 未找到"; return; }
                if (_slotsCtor == null) { _failure = "SourceSlots(string) 未找到"; return; }
                if (_slotsSet == null) { _failure = "SourceSlots.Set 未找到"; return; }
                if (_slotsZeroFrom == null) { _failure = "SourceSlots.ZeroFrom 未找到"; return; }
                if (_slotsRemoveAll == null) { _failure = "SourceSlots.RemoveAll 未找到"; return; }
                if (_tempControlSlotsField == null) { _failure = "CompTempControlAtmosphere.slots 未找到"; return; }

                _available = true;
            }
            catch (Exception e)
            {
                _available = false;
                _failure = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>读一格的空气温度（摄氏度）。AO 不在该地图上时返回 false。</summary>
        public static bool TryGetTemperature(Map map, IntVec3 cell, out float celsius)
        {
            celsius = 0f;
            Resolve();
            if (!_available || map == null)
            {
                return false;
            }

            object[] args = { map, cell, null };
            bool ok = (bool)_tryGetTemperature.Invoke(null, args);
            if (ok)
            {
                celsius = (float)args[2];
            }
            return ok;
        }

        /// <summary>
        /// 读一格的静态气压（Pa，绝对压力）。
        /// 用 AO 的 internal TryGetStaticPressurePascals，而不是公开的 TryGetPressurePascals
        /// ——后者把 solver 单位按 1000 Pa 换算，偏差被放大到约 83 倍。
        /// </summary>
        public static bool TryGetStaticPressure(Map map, IntVec3 cell, out float pascals)
        {
            pascals = 0f;
            Resolve();
            if (map == null || _staticPressure == null)
            {
                return false;
            }

            MapComponent component = FindAtmosphereComponent(map);
            if (component == null)
            {
                return false;
            }

            object[] args = { cell, null };
            bool ok = (bool)_staticPressure.Invoke(component, args);
            if (ok)
            {
                pascals = (float)args[1];
            }
            return ok;
        }

        /// <summary>向 AO 写一个点体积源（格体积单位，1 = 5 m³）。负值即抽气。</summary>
        public static bool AddVolumeSource(Map map, IntVec3 cell, float cellVolumes, float oxygenPercent, float temperatureOffsetCelsius)
        {
            Resolve();
            if (map == null || _addVolumeSource == null)
            {
                return false;
            }
            try
            {
                _addVolumeSource.Invoke(null, new object[] { map, cell, cellVolumes, oxygenPercent, temperatureOffsetCelsius });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>向 AO 注入一团设定温度与氧含量的气体（出风口用）。</summary>
        public static bool AddVolumeSourceAtTemperature(Map map, IntVec3 cell, float cellVolumes, float oxygenPercent, float gasCelsius)
        {
            Resolve();
            if (map == null || _addVolumeSourceAtTemperature == null)
            {
                return false;
            }
            try
            {
                _addVolumeSourceAtTemperature.Invoke(null, new object[] { map, cell, cellVolumes, oxygenPercent, gasCelsius });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static MapComponent FindAtmosphereComponent(Map map)
        {
            if (map == null || _atmosphereComponentType == null)
            {
                return null;
            }
            List<MapComponent> components = map.components;
            if (components == null)
            {
                return null;
            }
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] != null && components[i].GetType() == _atmosphereComponentType)
                {
                    return components[i];
                }
            }
            return null;
        }

        /// <summary>新建一组连续源槽位。prefix 决定内部句柄命名，同图内必须唯一。</summary>
        public static object NewSlots(string prefix)
        {
            Resolve();
            if (!_available)
            {
                return null;
            }
            return _slotsCtor.Invoke(new object[] { prefix });
        }

        /// <summary>
        /// 取某个建筑身上 AO 温控组件已经创建好的槽位。
        /// 复用它的生命周期：AO 组件自毁时会 RemoveAll，本 mod 无需另建一套。
        /// 目标没有 AO 温控组件（未被注入）时返回 null，调用方应回退。
        /// </summary>
        public static object GetSlots(Thing thing)
        {
            Resolve();
            if (!_available || thing == null)
            {
                return null;
            }

            ThingWithComps holder = thing as ThingWithComps;
            if (holder == null)
            {
                return null;
            }

            List<ThingComp> comps = holder.AllComps;
            if (comps == null)
            {
                return null;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp != null && comp.GetType() == _tempControlType)
                {
                    return _tempControlSlotsField.GetValue(comp);
                }
            }
            return null;
        }

        /// <summary>把第 slot 号槽位绑定到 cell，并写入给定速率。</summary>
        public static void SetSlot(object slots, Thing owner, int slot, IntVec3 cell, SlotRates rates)
        {
            if (slots == null)
            {
                return;
            }
            object boxed = NewRates(rates);
            _slotsSet.Invoke(slots, new object[] { owner, slot, cell, boxed });
        }

        /// <summary>把 from 号及之后的所有槽位清零。</summary>
        public static void ZeroFrom(object slots, Thing owner, int from)
        {
            if (slots == null)
            {
                return;
            }
            _slotsZeroFrom.Invoke(slots, new object[] { owner, from });
        }

        /// <summary>注销该 owner 的全部槽位。用于建筑拆除。</summary>
        public static void RemoveAll(object slots, Thing owner, Map map)
        {
            if (slots == null)
            {
                return;
            }
            _slotsRemoveAll.Invoke(slots, new object[] { owner, map });
        }

        /// <summary>读 AO_FacilityExtension.heatFactor；没有扩展时用 fallback。</summary>
        public static float HeatFactor(ThingDef def, float fallback)
        {
            return ReadExtFloat(def, _extHeatFactor, fallback);
        }

        /// <summary>读 AO_FacilityExtension.outletWindSpeed；没有扩展时用 fallback。</summary>
        public static float OutletWindSpeed(ThingDef def, float fallback)
        {
            return ReadExtFloat(def, _extOutletWindSpeed, fallback);
        }

        /// <summary>读 AO_FacilityExtension.outletToleranceK；没有扩展时用 fallback。</summary>
        public static float OutletTolerance(ThingDef def, float fallback)
        {
            return ReadExtFloat(def, _extOutletToleranceK, fallback);
        }

        private static float ReadExtFloat(ThingDef def, FieldInfo field, float fallback)
        {
            Resolve();
            if (!_available || def == null || _facilityExtFor == null || field == null)
            {
                return fallback;
            }

            try
            {
                object ext = _facilityExtFor.Invoke(null, new object[] { def });
                if (ext == null)
                {
                    return fallback;
                }
                return (float)field.GetValue(ext);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static object NewRates(SlotRates rates)
        {
            // AtmosphereSourceRates 是 struct：CreateInstance 得到装箱副本，
            // 反射写字段后整箱传给 SourceSlots.Set，Set 内部拆箱读值。
            object boxed = Activator.CreateInstance(_ratesType);
            SetRateField(boxed, "TemperaturePerTick", rates.TemperaturePerTick);
            SetRateField(boxed, "WindPerTick", rates.WindPerTick);
            SetRateField(boxed, "MaintainTargetWind", rates.MaintainTargetWind);
            SetRateField(boxed, "TargetWind", rates.TargetWind);
            SetRateField(boxed, "TargetWindBlendPerTick", rates.TargetWindBlendPerTick);
            return boxed;
        }

        private static void SetRateField(object boxed, string name, object value)
        {
            FieldInfo field;
            if (!RateFields.TryGetValue(name, out field))
            {
                field = AccessTools.Field(_ratesType, name);
                RateFields[name] = field;
            }
            if (field != null)
            {
                field.SetValue(boxed, value);
            }
        }
    }
}
