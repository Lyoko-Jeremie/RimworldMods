using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        public float GroundTemperaturePerTick;
        public float MoisturePerTick;
        public float MistPerTick;
        public Vector2 WindPerTick;
        public float PressurePerTick;
        public float OxygenPercentPerTick;
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
        private static MethodInfo _pushHeat;
        private static MethodInfo _tryGetOxygen;
        private static MethodInfo _tryGetVapor;
        private static MethodInfo _addMoistureDelta;
        private static Type _waterMathType;
        private static MethodInfo _emitWater;

        // AO 的 AtmosphereSourceRates 是 struct，反射调用 Set 时必须装箱。
        // 字段在 Resolve 里一次性解析成数组，热路径只按下标取值，不再查字典。
        private static FieldInfo[] _rateFieldArray;

        private static Func<object> _ratesFactory;

        // 参数全是 BCL/游戏类型的方法可以缓存强类型委托，绕过 MethodInfo.Invoke
        // 的 object[] 打包与参数检查。读温度/氧/水汽在 CCC、GC、AcNet 的
        // 每模拟步路径里被调数百次，这是最值得去掉反射热点的入口。
        // 委托创建失败时回退到原来的 Invoke，行为不变。
        private delegate bool TryGetTemperatureDelegate(Map map, IntVec3 cell, out float celsius);
        private delegate bool TryGetOxygenDelegate(Map map, IntVec3 cell, out byte percent);
        private delegate bool TryGetVaporDelegate(Map map, IntVec3 cell, out float vapor);
        private delegate bool AddVolumeDelegate(Map map, IntVec3 cell, float volumes, float oxygen, float temperature);
        private delegate bool AddMoistureDelegate(Map map, IntVec3 cell, float deltaVapor);
        private delegate bool TryGetPressureDelegate(object component, IntVec3 cell, out float pascals);
        private delegate bool PushHeatDelegate(object component, IntVec3 cell, float energy);

        private static TryGetTemperatureDelegate _tryGetTemperatureFast;
        private static TryGetOxygenDelegate _tryGetOxygenFast;
        private static TryGetVaporDelegate _tryGetVaporFast;
        private static AddVolumeDelegate _addVolumeSourceFast;
        private static AddVolumeDelegate _addVolumeSourceAtTemperatureFast;
        private static AddMoistureDelegate _addMoistureDeltaFast;
        private static TryGetPressureDelegate _staticPressureFast;
        private static PushHeatDelegate _pushHeatFast;

        // 每台设备的 SourceSlots 在 AO 温控组件构造时创建一次，之后不变。
        // AcNet 每模拟步对每台室内机都要 GetSlots，原来每次线性扫 AllComps。
        // 用弱表缓存：Thing 被销毁后条目自动回收，不会拖住地图。
        private static readonly ConditionalWeakTable<Thing, object> _slotsCache =
            new ConditionalWeakTable<Thing, object>();

        private static readonly string[] RateFieldNames =
        {
            "TemperaturePerTick",
            "GroundTemperaturePerTick",
            "MoisturePerTick",
            "MistPerTick",
            "WindPerTick",
            "PressurePerTick",
            "OxygenPercentPerTick",
            "MaintainTargetWind",
            "TargetWind",
            "TargetWindBlendPerTick",
        };

        // Invoke 的参数数组与装箱速率按线程复用：AO 的模拟跑在线程池上，
        // 共享静态实例会写竞争；每线程一份既避免每步分配，也不会串数据。
        // 同线程内若有重入，Invoke 在进入被调方法前已读完参数，不会被后来的写入影响。
        // SourceSlots.Set 按值读取 rates，复用同一个装箱实例是安全的。
        [ThreadStatic] private static object[] _args1;
        [ThreadStatic] private static object[] _args2;
        [ThreadStatic] private static object[] _args3;
        [ThreadStatic] private static object[] _args4;
        [ThreadStatic] private static object[] _args5;
        [ThreadStatic] private static object _ratesBox;

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

        /// <summary>氧与水汽读写接口是否可用。</summary>
        public static bool MoistureAvailable
        {
            get { Resolve(); return _tryGetOxygen != null && _tryGetVapor != null && _addMoistureDelta != null; }
        }

        /// <summary>凝结水出水口是否可用（AO 有 WaterMath.EmitWater 且注册了 AO_Water）。</summary>
        public static bool WaterAvailable
        {
            get
            {
                Resolve();
                return _emitWater != null && DefDatabase<ThingDef>.GetNamedSilentFail("AO_Water") != null;
            }
        }

        /// <summary>
        /// 把以 kg 计的凝结水倒进 AO 的水池：满 0.25 kg 就在 parent 那一格生成一个 AO_Water。
        /// 返回池里剩下的量。parent 必须是已 spawn 的建筑，否则 AO 那边放不下会丢掉。
        /// </summary>
        public static float EmitWater(Thing parent, float poolKg)
        {
            Resolve();
            if (_emitWater == null || parent == null || !parent.Spawned || poolKg <= 0f)
            {
                return poolKg;
            }

            object[] args = GetArgs(ref _args2, 2);
            args[0] = parent;
            args[1] = poolKg;
            try
            {
                _emitWater.Invoke(null, args);
                return (float)args[1];
            }
            catch (Exception)
            {
                // AO 换版本把签名改了也不要让整个模拟停摆，水就地丢弃。
                return poolKg;
            }
            finally
            {
                args[0] = null;
            }
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
                    // 室外机排热/吸热：能量单位是 K·格总量，内部均分到十字 5 格。
                    _pushHeat = AccessTools.Method(
                        _atmosphereComponentType,
                        "PushHeat",
                        new[] { typeof(IntVec3), typeof(float) });
                }

                // 氧与水汽：CCC 气袋要把进气成分带进管道、出气时再吐回房间。
                // AO 的 MixReleasedGas 只处理氧与温度，水汽必须单独走 AddMoistureDelta。
                _tryGetOxygen = AccessTools.Method(
                    interfaceType,
                    "TryGetOxygenPercent",
                    new[] { typeof(Map), typeof(IntVec3), typeof(byte).MakeByRefType() });
                _tryGetVapor = AccessTools.Method(
                    interfaceType,
                    "TryGetAbsoluteVapor",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType() });
                _addMoistureDelta = AccessTools.Method(
                    interfaceType,
                    "AddMoistureDelta",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float) });

                // 产水：AO 自带 AO_Water（250 g/单位）与 WaterMath.EmitWater，
                // 除湿器和「空气压缩机的冷凝水」都走这条路。CCC 的空气处理机
                // 把回路里的过饱和水汽凝结下来时，直接复用同一套出口。
                // internal 类型，只能按名字找。
                _waterMathType = AccessTools.TypeByName("AtmosphereOverhaul.Buildings.WaterMath");
                if (_waterMathType != null)
                {
                    _emitWater = AccessTools.Method(
                        _waterMathType,
                        "EmitWater",
                        new[] { typeof(Thing), typeof(float).MakeByRefType() });
                }

                if (_tryGetTemperature == null) { _failure = "AtmosphereInterface.TryGetTemperature 未找到"; return; }
                if (_slotsCtor == null) { _failure = "SourceSlots(string) 未找到"; return; }
                if (_slotsSet == null) { _failure = "SourceSlots.Set 未找到"; return; }
                if (_slotsZeroFrom == null) { _failure = "SourceSlots.ZeroFrom 未找到"; return; }
                if (_slotsRemoveAll == null) { _failure = "SourceSlots.RemoveAll 未找到"; return; }
                if (_tempControlSlotsField == null) { _failure = "CompTempControlAtmosphere.slots 未找到"; return; }

                _rateFieldArray = new FieldInfo[RateFieldNames.Length];
                for (int i = 0; i < RateFieldNames.Length; i++)
                {
                    _rateFieldArray[i] = AccessTools.Field(_ratesType, RateFieldNames[i]);
                }

                // 缓存强类型委托；创建失败时保持 null，调用点回退到 Invoke。
                _tryGetTemperatureFast = MakeFast<TryGetTemperatureDelegate>(_tryGetTemperature);
                _tryGetOxygenFast = MakeFast<TryGetOxygenDelegate>(_tryGetOxygen);
                _tryGetVaporFast = MakeFast<TryGetVaporDelegate>(_tryGetVapor);
                _addVolumeSourceFast = MakeFast<AddVolumeDelegate>(_addVolumeSource);
                _addVolumeSourceAtTemperatureFast = MakeFast<AddVolumeDelegate>(_addVolumeSourceAtTemperature);
                _addMoistureDeltaFast = MakeFast<AddMoistureDelegate>(_addMoistureDelta);
                _staticPressureFast = MakeFast<TryGetPressureDelegate>(_staticPressure);
                _pushHeatFast = MakeFast<PushHeatDelegate>(_pushHeat);

                _available = true;
            }
            catch (Exception e)
            {
                _available = false;
                _failure = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>把 MethodInfo 绑定成强类型委托；失败返回 null，调用点回退到 Invoke。</summary>
        private static T MakeFast<T>(MethodInfo method) where T : class
        {
            if (method == null)
            {
                return null;
            }
            try
            {
                return (T)(object)method.CreateDelegate(typeof(T));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>该地图上是否存在 AO 的大气组件。非 AO 地图上所有接管都应回退。</summary>
        public static bool IsActive(Map map)
        {
            Resolve();
            return map != null && FindAtmosphereComponent(map) != null;
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

            if (_tryGetTemperatureFast != null)
            {
                return _tryGetTemperatureFast(map, cell, out celsius);
            }

            object[] args = GetArgs(ref _args3, 3);
            args[0] = map;
            args[1] = cell;
            args[2] = null;
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

            if (_staticPressureFast != null)
            {
                return _staticPressureFast(component, cell, out pascals);
            }

            object[] args = GetArgs(ref _args2, 2);
            args[0] = cell;
            args[1] = null;
            bool ok = (bool)_staticPressure.Invoke(component, args);
            if (ok)
            {
                pascals = (float)args[1];
            }
            return ok;
        }

        /// <summary>
        /// 在 cell 处注入/抽取一份热量。energy 单位是 K·格总量（正=升温，负=降温），
        /// AO 内部把它均分到中心 + 四邻共 5 格，只保留同房间的格。
        /// 室外机排热/吸热走这条。
        /// </summary>
        public static bool PushHeat(Map map, IntVec3 cell, float energy)
        {
            Resolve();
            if (map == null || _pushHeat == null)
            {
                return false;
            }

            MapComponent component = FindAtmosphereComponent(map);
            if (component == null)
            {
                return false;
            }

            try
            {
                if (_pushHeatFast != null)
                {
                    return _pushHeatFast(component, cell, energy);
                }
                object[] args = GetArgs(ref _args2, 2);
                args[0] = cell;
                args[1] = energy;
                return (bool)_pushHeat.Invoke(component, args);
            }
            catch (Exception)
            {
                return false;
            }
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
                if (_addVolumeSourceFast != null)
                {
                    return _addVolumeSourceFast(map, cell, cellVolumes, oxygenPercent, temperatureOffsetCelsius);
                }
                object[] args = GetArgs(ref _args5, 5);
                args[0] = map;
                args[1] = cell;
                args[2] = cellVolumes;
                args[3] = oxygenPercent;
                args[4] = temperatureOffsetCelsius;
                _addVolumeSource.Invoke(null, args);
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
                if (_addVolumeSourceAtTemperatureFast != null)
                {
                    return _addVolumeSourceAtTemperatureFast(map, cell, cellVolumes, oxygenPercent, gasCelsius);
                }
                object[] args = GetArgs(ref _args5, 5);
                args[0] = map;
                args[1] = cell;
                args[2] = cellVolumes;
                args[3] = oxygenPercent;
                args[4] = gasCelsius;
                _addVolumeSourceAtTemperature.Invoke(null, args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>读一格的氧体积百分比（0-100）。</summary>
        public static bool TryGetOxygenPercent(Map map, IntVec3 cell, out float percent)
        {
            percent = 21f;
            Resolve();
            if (map == null || _tryGetOxygen == null)
            {
                return false;
            }

            if (_tryGetOxygenFast != null)
            {
                byte raw;
                if (!_tryGetOxygenFast(map, cell, out raw))
                {
                    return false;
                }
                percent = raw;
                return true;
            }

            object[] args = GetArgs(ref _args3, 3);
            args[0] = map;
            args[1] = cell;
            args[2] = null;
            bool ok = (bool)_tryGetOxygen.Invoke(null, args);
            if (ok)
            {
                percent = (byte)args[2];
            }
            return ok;
        }

        /// <summary>读一格的绝对水汽含量，量纲同 AO 的 Moisture 场。</summary>
        public static bool TryGetAbsoluteVapor(Map map, IntVec3 cell, out float vapor)
        {
            vapor = 0f;
            Resolve();
            if (map == null || _tryGetVapor == null)
            {
                return false;
            }

            if (_tryGetVaporFast != null)
            {
                return _tryGetVaporFast(map, cell, out vapor);
            }

            object[] args = GetArgs(ref _args3, 3);
            args[0] = map;
            args[1] = cell;
            args[2] = null;
            bool ok = (bool)_tryGetVapor.Invoke(null, args);
            if (ok)
            {
                vapor = (float)args[2];
            }
            return ok;
        }

        /// <summary>
        /// 给一格叠加水汽浓度增量。体积源接口不带水汽，所以出风口搬湿要走这条。
        /// </summary>
        public static bool AddMoistureDelta(Map map, IntVec3 cell, float deltaVapor)
        {
            Resolve();
            if (map == null || _addMoistureDelta == null)
            {
                return false;
            }
            try
            {
                if (_addMoistureDeltaFast != null)
                {
                    return _addMoistureDeltaFast(map, cell, deltaVapor);
                }
                object[] args = GetArgs(ref _args3, 3);
                args[0] = map;
                args[1] = cell;
                args[2] = deltaVapor;
                _addMoistureDelta.Invoke(null, args);
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

            object cachedSlots;
            if (_slotsCache.TryGetValue(thing, out cachedSlots))
            {
                return cachedSlots;
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
                    object slots = _tempControlSlotsField.GetValue(comp);
                    if (slots != null)
                    {
                        try
                        {
                            _slotsCache.Add(thing, slots);
                        }
                        catch (ArgumentException)
                        {
                            // 模拟线程与主线程可能同时首次取同一 Thing 的 slots；
                            // 另一线程已缓存同一引用，忽略即可
                        }
                    }
                    return slots;
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
            object boxed = GetRatesBox();
            if (boxed == null)
            {
                return;
            }
            FillRates(boxed, rates);

            object[] args = GetArgs(ref _args4, 4);
            args[0] = owner;
            args[1] = slot;
            args[2] = cell;
            args[3] = boxed;
            try
            {
                _slotsSet.Invoke(slots, args);
            }
            finally
            {
                // 复用数组不能长期持有 Thing 引用，否则地图销毁后会被拖住。
                args[0] = null;
                args[3] = null;
            }
        }

        /// <summary>把 from 号及之后的所有槽位清零。</summary>
        public static void ZeroFrom(object slots, Thing owner, int from)
        {
            if (slots == null)
            {
                return;
            }
            object[] args = GetArgs(ref _args2, 2);
            args[0] = owner;
            args[1] = from;
            try
            {
                _slotsZeroFrom.Invoke(slots, args);
            }
            finally
            {
                args[0] = null;
            }
        }

        /// <summary>注销该 owner 的全部槽位。用于建筑拆除。</summary>
        public static void RemoveAll(object slots, Thing owner, Map map)
        {
            if (slots == null)
            {
                return;
            }
            // 参数个数必须与 SourceSlots.RemoveAll(Thing, Map) 严格一致，
            // 多传一个会在 Invoke 之前直接抛 TargetParameterCountException。
            object[] args = GetArgs(ref _args2, 2);
            args[0] = owner;
            args[1] = map;
            try
            {
                _slotsRemoveAll.Invoke(slots, args);
            }
            catch (Exception)
            {
                // AO 换版本改了签名也不要让异常冲穿 tick；句柄会随建筑销毁自行失效。
            }
            finally
            {
                args[0] = null;
                args[1] = null;
            }
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
                object[] args = GetArgs(ref _args1, 1);
                args[0] = def;
                object ext = _facilityExtFor.Invoke(null, args);
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

        /// <summary>
        /// 取本线程复用的 rates 装箱实例。
        /// AO 的 SourceSlots.Set 按值接收 rates，不会保留装箱引用，
        /// 因此同一线程内原地改写是安全的；每线程一份则避免模拟线程与主线程写竞争。
        /// </summary>
        private static object GetRatesBox()
        {
            object boxed = _ratesBox;
            if (boxed == null)
            {
                if (_ratesFactory == null)
                {
                    _ratesFactory = CreateFactory(_ratesType);
                }
                boxed = _ratesFactory != null ? _ratesFactory() : null;
                _ratesBox = boxed;
            }
            return boxed;
        }

        private static void FillRates(object boxed, SlotRates rates)
        {
            FieldInfo[] fields = _rateFieldArray;
            if (fields == null)
            {
                return;
            }
            if (fields[0] != null) { fields[0].SetValue(boxed, rates.TemperaturePerTick); }
            if (fields[1] != null) { fields[1].SetValue(boxed, rates.GroundTemperaturePerTick); }
            if (fields[2] != null) { fields[2].SetValue(boxed, rates.MoisturePerTick); }
            if (fields[3] != null) { fields[3].SetValue(boxed, rates.MistPerTick); }
            if (fields[4] != null) { fields[4].SetValue(boxed, rates.WindPerTick); }
            if (fields[5] != null) { fields[5].SetValue(boxed, rates.PressurePerTick); }
            if (fields[6] != null) { fields[6].SetValue(boxed, rates.OxygenPercentPerTick); }
            if (fields[7] != null) { fields[7].SetValue(boxed, rates.MaintainTargetWind); }
            if (fields[8] != null) { fields[8].SetValue(boxed, rates.TargetWind); }
            if (fields[9] != null) { fields[9].SetValue(boxed, rates.TargetWindBlendPerTick); }
        }

        /// <summary>为 AO struct 的装箱实例生成构造委托，替代每次 Activator.CreateInstance。</summary>
        private static Func<object> CreateFactory(Type type)
        {
            if (type == null)
            {
                return null;
            }
            try
            {
                return Expression.Lambda<Func<object>>(Expression.New(type)).Compile();
            }
            catch (Exception)
            {
                return delegate { return Activator.CreateInstance(type); };
            }
        }

        /// <summary>
        /// 取本线程复用的反射参数数组。长度固定后不再分配；
        /// 调用方必须在每次使用前写满全部元素，避免残留上一次的引用。
        /// </summary>
        private static object[] GetArgs(ref object[] pool, int length)
        {
            object[] args = pool;
            if (args == null || args.Length != length)
            {
                args = new object[length];
                pool = args;
            }
            return args;
        }
    }
}
