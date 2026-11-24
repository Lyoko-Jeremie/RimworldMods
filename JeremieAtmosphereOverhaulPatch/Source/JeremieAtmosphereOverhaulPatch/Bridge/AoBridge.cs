using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
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
        // 公共产水接口：AO 作者推荐用它替代内部 WaterMath.EmitWater。
        // 同一套出口（每 0.25 kg 一个 AO_Water，含 Dubs 兼容），返回剩余 kg。
        private static MethodInfo _emitCondensate;

        // AO 的 AtmosphereSourceRates 是 struct，反射调用 Set 时必须装箱。
        // 字段在 Resolve 里一次性解析成数组，热路径只按下标取值，不再查字典。
        private static FieldInfo[] _rateFieldArray;

        private static Func<object> _ratesFactory;

        // AO 的每步完成事件。StepApplied 在 AO 主线程、一步模拟完成并发布快照后触发，
        // 存档期间不触发。用它替代「patch CompleteFinishedSimulation + 反射
        // lastSynchronizedSimulationTick 算差值」的旧写法：elapsedTicks 由 AO 直接给出，
        // 不会因为补丁安装顺序或存档流程出现偏差。
        private static EventInfo _stepAppliedEvent;
        private static Delegate _stepAppliedDelegate;
        private static bool _stepAppliedBound;
        private static string _stepAppliedFailure;
        private static readonly List<StepAppliedHandler> _stepAppliedHandlers = new List<StepAppliedHandler>();
        private static readonly object _stepAppliedLock = new object();

        // AO 的每步准备事件。StepPreparing 在 AO 主线程、模拟线程锁之前触发，
        // 这一步要消费的大气增量/连续源/热量都在它之后被 Drain 进 pending。
        // 用它替代「patch PrepareTemperatureSources 直接写 pending 数组」的旧写法：
        // 不再依赖 AO 内部数组名，也不会与 AO 自己的 Clear/Drain 顺序耦合。
        private static EventInfo _stepPreparingEvent;
        private static Delegate _stepPreparingDelegate;
        private static bool _stepPreparingBound;
        private static string _stepPreparingFailure;
        private static readonly List<StepPreparingHandler> _stepPreparingHandlers = new List<StepPreparingHandler>();
        private static readonly object _stepPreparingLock = new object();

        // 批量温度增量：AO 在一次 lock 内把整批 indices/deltas 累加进 incoming 缓冲，
        // 比逐格 AddTemperatureDelta 少 N-1 次锁与 N-1 次参数打包。ProxyHeat 每步
        // 要写上千格，这是第 4 步里最值得走的公共入口。
        private static MethodInfo _addTemperatureDeltas;
        private static AddTemperatureDeltasDelegate _addTemperatureDeltasFast;
        private static string _addTemperatureDeltasFailure;
        [ThreadStatic] private static object[] _argsAddTemperatureDeltas;

        // 公共连续源接口：替代内部 SourceSlots（ctor/Set/ZeroFrom/RemoveAll）。
        // AO 作者明确推荐用这套公共 API，迁移完成后内部成员才允许重构。
        private static MethodInfo _setContinuousSource;
        private static MethodInfo _zeroContinuousSourcesWithPrefix;
        private static MethodInfo _removeContinuousSourcesWithPrefix;
        private static MethodInfo _unregisterContinuousSources;
        private static string _continuousSourceFailure;

        // 参数全是 BCL/游戏类型的方法可以缓存强类型委托，绕过 MethodInfo.Invoke
        // 的 object[] 打包与参数检查。读温度/氧/水汽在 CCC、GC、AcNet 的
        // 每模拟步路径里被调数百次，这是最值得去掉反射热点的入口。
        // 委托创建失败时回退到原来的 Invoke，行为不变。
        private delegate bool TryGetTemperatureDelegate(Map map, IntVec3 cell, out float celsius);
        private delegate bool TryGetOxygenDelegate(Map map, IntVec3 cell, out byte percent);
        private delegate bool TryGetVaporDelegate(Map map, IntVec3 cell, out float vapor);
        private delegate bool AddVolumeDelegate(Map map, IntVec3 cell, float volumes, float oxygen, float temperature);
        private delegate bool AddMoistureDelegate(Map map, IntVec3 cell, float deltaVapor);
        private delegate bool TryGetPressureDelegate(Map map, IntVec3 cell, out float pascals);
        private delegate bool PushHeatDelegate(Map map, IntVec3 cell, float energy);

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

        /// <summary>
        /// AO 每完成一步模拟后的回调。map 是本次推进的地图，gameTick 是当前游戏 tick，
        /// elapsedTicks 是这一步实际覆盖的游戏 tick 数（AO 直接给出，不需要自己算差分）。
        /// </summary>
        public delegate void StepAppliedHandler(Map map, int gameTick, int elapsedTicks);

        /// <summary>
        /// AO 每步准备阶段的回调。map 是本次推进的地图，gameTick 是当前游戏 tick，
        /// elapsedTicks 是距上次准备的游戏 tick 数。回调在 AO 主线程、模拟线程锁之前触发。
        /// </summary>
        public delegate void StepPreparingHandler(Map map, int gameTick, int elapsedTicks);

        /// <summary>批量写温度增量；一次锁内累加整批。</summary>
        private delegate void AddTemperatureDeltasDelegate(Map map, int[] indices, float[] deltas, int count);

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
                return (_emitCondensate != null || _emitWater != null)
                       && DefDatabase<ThingDef>.GetNamedSilentFail("AO_Water") != null;
            }
        }

        /// <summary>
        /// 把以 kg 计的凝结水倒进 AO 的水池：满 0.25 kg 就在 parent 那一格生成一个 AO_Water。
        /// 返回池里剩下的量。parent 必须是已 spawn 的建筑，否则 AO 那边放不下会丢掉。
        /// </summary>
        public static float EmitWater(Thing parent, float poolKg)
        {
            Resolve();
            if (parent == null || !parent.Spawned || poolKg <= 0f)
            {
                return poolKg;
            }

            try
            {
                if (_emitCondensate != null)
                {
                    object[] cargs = GetArgs(ref _args2, 2);
                    cargs[0] = parent;
                    cargs[1] = poolKg;
                    try
                    {
                        return (float)_emitCondensate.Invoke(null, cargs);
                    }
                    finally
                    {
                        cargs[0] = null;
                    }
                }

                if (_emitWater == null)
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
                finally
                {
                    args[0] = null;
                }
            }
            catch (Exception)
            {
                // AO 换版本把签名改了也不要让整个模拟停摆，水就地丢弃。
                return poolKg;
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

                if (_ratesType == null || interfaceType == null)
                {
                    _failure = "AO 核心类型缺失：" + RatesTypeName + " / " + InterfaceTypeName;
                    return;
                }

                // 每步完成事件：可选能力，缺失时不影响其它接口，调用方各自回退到旧补丁。
                try
                {
                    _stepAppliedEvent = interfaceType.GetEvent(
                        "StepApplied",
                        BindingFlags.Public | BindingFlags.Static);
                }
                catch (Exception)
                {
                    _stepAppliedEvent = null;
                }

                // 每步准备事件：同样是可选能力，缺失时调用方回退到旧补丁。
                try
                {
                    _stepPreparingEvent = interfaceType.GetEvent(
                        "StepPreparing",
                        BindingFlags.Public | BindingFlags.Static);
                }
                catch (Exception)
                {
                    _stepPreparingEvent = null;
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
                // 气压改走公共接口：TryGetPressurePascals 内部就是 TryGetStaticPressurePascals，
                // 返回绝对压力（含海拔/真空/密封房间修正），不再需要区分 solver 单位。
                _staticPressure = AccessTools.Method(
                    interfaceType,
                    "TryGetPressurePascals",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType() });

                // 批量温度增量：AO 一次锁内累加整批，参数全是 BCL/游戏类型，可缓存强类型委托。
                _addTemperatureDeltas = AccessTools.Method(
                    interfaceType,
                    "AddTemperatureDeltas",
                    new[] { typeof(Map), typeof(int[]), typeof(float[]), typeof(int) });
                if (_addTemperatureDeltas == null)
                {
                    _addTemperatureDeltasFailure = "AtmosphereInterface.AddTemperatureDeltas 未找到";
                }

                // 公共连续源：SetContinuousSource 有则更新、无则注册；前缀版本用于批量清零/删除。
                _setContinuousSource = AccessTools.Method(
                    interfaceType,
                    "SetContinuousSource",
                    new[] { typeof(Map), typeof(Thing), typeof(string), typeof(IntVec3), _ratesType });
                _zeroContinuousSourcesWithPrefix = AccessTools.Method(
                    interfaceType,
                    "ZeroContinuousSourcesWithPrefix",
                    new[] { typeof(Map), typeof(Thing), typeof(string) });
                _removeContinuousSourcesWithPrefix = AccessTools.Method(
                    interfaceType,
                    "RemoveContinuousSourcesWithPrefix",
                    new[] { typeof(Map), typeof(Thing), typeof(string) });
                _unregisterContinuousSources = AccessTools.Method(
                    interfaceType,
                    "UnregisterContinuousSources",
                    new[] { typeof(Map), typeof(Thing) });
                if (_setContinuousSource == null || _zeroContinuousSourcesWithPrefix == null)
                {
                    _continuousSourceFailure = "AtmosphereInterface.SetContinuousSource/ZeroContinuousSourcesWithPrefix 未找到";
                }

                // 室外机排热/吸热：走公共 AtmosphereInterface.PushHeat。
                // 能量单位是 K·格总量，AO 内部把它均分到该格所在房间的十字 5 格。
                _pushHeat = AccessTools.Method(
                    interfaceType,
                    "PushHeat",
                    new[] { typeof(Map), typeof(IntVec3), typeof(float) });

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
                // 公共产水接口优先：AO 作者推荐 EmitCondensate，签名更简单且不依赖 internal 类型。
                _emitCondensate = AccessTools.Method(
                    interfaceType,
                    "EmitCondensate",
                    new[] { typeof(Thing), typeof(float) });

                // 回退路径：旧版 AO 只有 internal WaterMath.EmitWater。
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
                if (_tryGetTemperature == null) { _failure = "AtmosphereInterface.TryGetTemperature 未找到"; return; }

                // SourceSlots 与 CompTempControlAtmosphere.slots 是 AO 的内部实现，
                // 作者已声明迁移完成后会重构。它们只作为旧版兼容路径，不再作为可用性门禁；
                // 新版 AO 缺失时，公共 SetContinuousSource / AO_OutletLayout 仍能工作。

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
                _addTemperatureDeltasFast = MakeFast<AddTemperatureDeltasDelegate>(_addTemperatureDeltas);
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

        /// <summary>AO 的 StepApplied 事件是否可用。</summary>
        public static bool StepAppliedAvailable
        {
            get { Resolve(); return _stepAppliedEvent != null; }
        }

        /// <summary>StepApplied 不可用时的具体原因，用于日志。</summary>
        public static string StepAppliedFailure
        {
            get { Resolve(); return _stepAppliedFailure; }
        }

        /// <summary>
        /// 订阅 AO 的每步完成事件。第一次订阅时反射绑定静态事件；
        /// 返回 false 表示 AO 没有这个事件，调用方应回退到「patch
        /// CompleteFinishedSimulation + lastSynchronizedSimulationTick 差分」的旧写法。
        /// 回调在 AO 主线程、一步完成并发布快照后触发，存档期间不触发。
        /// </summary>
        public static bool SubscribeStepApplied(StepAppliedHandler handler)
        {
            if (handler == null)
            {
                return false;
            }

            Resolve();
            if (_stepAppliedEvent == null)
            {
                if (string.IsNullOrEmpty(_stepAppliedFailure))
                {
                    _stepAppliedFailure = "AtmosphereInterface.StepApplied 未找到";
                }
                return false;
            }

            lock (_stepAppliedLock)
            {
                if (_stepAppliedHandlers.Contains(handler))
                {
                    return true;
                }

                if (!_stepAppliedBound)
                {
                    try
                    {
                        MethodInfo callback = typeof(AoBridge).GetMethod(
                            nameof(OnStepAppliedEvent),
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _stepAppliedDelegate = Delegate.CreateDelegate(
                            _stepAppliedEvent.EventHandlerType, callback);
                        _stepAppliedEvent.AddEventHandler(null, _stepAppliedDelegate);
                        _stepAppliedBound = true;
                    }
                    catch (Exception e)
                    {
                        _stepAppliedFailure = e.GetType().Name + ": " + e.Message;
                        _stepAppliedDelegate = null;
                        return false;
                    }
                }

                _stepAppliedHandlers.Add(handler);
            }

            return true;
        }

        /// <summary>取消订阅；最后一个订阅者移除后保留静态挂钩，避免反复增删事件。</summary>
        public static void UnsubscribeStepApplied(StepAppliedHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (_stepAppliedLock)
            {
                _stepAppliedHandlers.Remove(handler);
            }
        }

        /// <summary>
        /// 事件转发。AO 的 StepApplied 在主线程触发，但订阅者仍逐个 try/catch：
        /// 一个模块抛异常不能拖垮其它模块，也不能让 AO 的 Raise 记录一次失败。
        /// </summary>
        private static void OnStepAppliedEvent(Map map, int gameTick, int elapsedTicks)
        {
            StepAppliedHandler[] handlers;
            lock (_stepAppliedLock)
            {
                if (_stepAppliedHandlers.Count == 0)
                {
                    return;
                }
                handlers = _stepAppliedHandlers.ToArray();
            }

            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i](map, gameTick, elapsedTicks);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(
                        "[AOPatch/AO] StepApplied 订阅者异常：" + e.GetType().Name + ": " + e.Message,
                        ("AOPatch.StepApplied." + handlers[i].Method.Name).GetHashCode());
                }
            }
        }

        /// <summary>AO 的 StepPreparing 事件是否可用。</summary>
        public static bool StepPreparingAvailable
        {
            get { Resolve(); return _stepPreparingEvent != null; }
        }

        /// <summary>StepPreparing 不可用时的具体原因，用于日志。</summary>
        public static string StepPreparingFailure
        {
            get { Resolve(); return _stepPreparingFailure; }
        }

        /// <summary>
        /// 订阅 AO 的每步准备事件。返回 false 表示 AO 没有这个事件，
        /// 调用方应回退到 patch PrepareTemperatureSources 的旧写法。
        /// 回调在 AO 主线程、模拟线程锁之前触发。
        /// </summary>
        public static bool SubscribeStepPreparing(StepPreparingHandler handler)
        {
            if (handler == null)
            {
                return false;
            }

            Resolve();
            if (_stepPreparingEvent == null)
            {
                if (string.IsNullOrEmpty(_stepPreparingFailure))
                {
                    _stepPreparingFailure = "AtmosphereInterface.StepPreparing 未找到";
                }
                return false;
            }

            lock (_stepPreparingLock)
            {
                if (_stepPreparingHandlers.Contains(handler))
                {
                    return true;
                }

                if (!_stepPreparingBound)
                {
                    try
                    {
                        MethodInfo callback = typeof(AoBridge).GetMethod(
                            nameof(OnStepPreparingEvent),
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _stepPreparingDelegate = Delegate.CreateDelegate(
                            _stepPreparingEvent.EventHandlerType, callback);
                        _stepPreparingEvent.AddEventHandler(null, _stepPreparingDelegate);
                        _stepPreparingBound = true;
                    }
                    catch (Exception e)
                    {
                        _stepPreparingFailure = e.GetType().Name + ": " + e.Message;
                        _stepPreparingDelegate = null;
                        return false;
                    }
                }

                _stepPreparingHandlers.Add(handler);
            }

            return true;
        }

        /// <summary>取消订阅；最后一个订阅者移除后保留静态挂钩，避免反复增删事件。</summary>
        public static void UnsubscribeStepPreparing(StepPreparingHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (_stepPreparingLock)
            {
                _stepPreparingHandlers.Remove(handler);
            }
        }

        /// <summary>事件转发：逐个 try/catch，一个模块抛异常不拖垮其它模块。</summary>
        private static void OnStepPreparingEvent(Map map, int gameTick, int elapsedTicks)
        {
            StepPreparingHandler[] handlers;
            lock (_stepPreparingLock)
            {
                if (_stepPreparingHandlers.Count == 0)
                {
                    return;
                }
                handlers = _stepPreparingHandlers.ToArray();
            }

            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i](map, gameTick, elapsedTicks);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(
                        "[AOPatch/AO] StepPreparing 订阅者异常：" + e.GetType().Name + ": " + e.Message,
                        ("AOPatch.StepPreparing." + handlers[i].Method.Name).GetHashCode());
                }
            }
        }

        /// <summary>批量温度增量入口是否可用。</summary>
        public static bool AddTemperatureDeltasAvailable
        {
            get { Resolve(); return _addTemperatureDeltas != null && _addTemperatureDeltasFailure == null; }
        }

        /// <summary>批量温度增量不可用时的具体原因，用于日志。</summary>
        public static string AddTemperatureDeltasFailure
        {
            get { Resolve(); return _addTemperatureDeltasFailure; }
        }

        /// <summary>
        /// 一次锁内给一批格子累加温度增量（摄氏度）。index 是 z*Width+x。
        /// count 为有效条目数；越界/NaN 的条目由 AO 自己跳过并计数。
        /// </summary>
        public static bool AddTemperatureDeltas(Map map, int[] indices, float[] deltas, int count)
        {
            if (map == null || indices == null || deltas == null || count <= 0)
            {
                return false;
            }

            Resolve();
            if (_addTemperatureDeltas == null)
            {
                return false;
            }

            int n = count;
            if (n > indices.Length)
            {
                n = indices.Length;
            }
            if (n > deltas.Length)
            {
                n = deltas.Length;
            }
            if (n <= 0)
            {
                return false;
            }

            try
            {
                if (_addTemperatureDeltasFast != null)
                {
                    _addTemperatureDeltasFast(map, indices, deltas, n);
                }
                else
                {
                    object[] args = GetArgs(ref _argsAddTemperatureDeltas, 4);
                    args[0] = map;
                    args[1] = indices;
                    args[2] = deltas;
                    args[3] = n;
                    _addTemperatureDeltas.Invoke(null, args);
                }
                return true;
            }
            catch (Exception e)
            {
                _addTemperatureDeltasFailure = e.GetType().Name + ": " + e.Message;
                return false;
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
        /// 读一格的绝对气压（Pa）。走 AO 公开的 TryGetPressurePascals：
        /// 它内部转发到 TryGetStaticPressurePascals，返回
        /// AmbientSurfacePressurePa + StoredPressureAt，已经包含海拔/真空/密封房间修正，
        /// 不需要调用方再区分 solver 单位。
        /// </summary>
        public static bool TryGetStaticPressure(Map map, IntVec3 cell, out float pascals)
        {
            pascals = 0f;
            Resolve();
            if (map == null || _staticPressure == null)
            {
                return false;
            }

            if (_staticPressureFast != null)
            {
                return _staticPressureFast(map, cell, out pascals);
            }

            object[] args = GetArgs(ref _args3, 3);
            args[0] = map;
            args[1] = cell;
            args[2] = null;
            bool ok = (bool)_staticPressure.Invoke(null, args);
            if (ok)
            {
                pascals = (float)args[2];
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

            try
            {
                if (_pushHeatFast != null)
                {
                    return _pushHeatFast(map, cell, energy);
                }
                object[] args = GetArgs(ref _args3, 3);
                args[0] = map;
                args[1] = cell;
                args[2] = energy;
                return (bool)_pushHeat.Invoke(null, args);
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
            if (!_available || thing == null || _tempControlSlotsField == null)
            {
                // 新版 AO 若移除了 CompTempControlAtmosphere.slots，旧槽位路径直接不可用；
                // 调用方应改用公共 SetContinuousSource。
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
        /// <summary>公共连续源接口是否可用（SetContinuousSource/前缀清零）。</summary>
        public static bool ContinuousSourceAvailable
        {
            get { Resolve(); return _setContinuousSource != null && _zeroContinuousSourcesWithPrefix != null; }
        }

        /// <summary>公共连续源接口不可用时的具体原因，用于日志。</summary>
        public static string ContinuousSourceFailure
        {
            get { Resolve(); return _continuousSourceFailure; }
        }

        /// <summary>
        /// 注册或更新一个连续源：同 owner+key 已存在时更新速率并移动格子。
        /// rates 每游戏 tick 生效；owner 摧毁后自动失效。key 建议带本 mod 前缀。
        /// </summary>
        public static bool SetContinuousSource(Map map, Thing owner, string key, IntVec3 cell, SlotRates rates)
        {
            if (map == null || owner == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            Resolve();
            if (_setContinuousSource == null)
            {
                return false;
            }

            object boxed = GetRatesBox();
            if (boxed == null)
            {
                return false;
            }
            FillRates(boxed, rates);

            object[] args = GetArgs(ref _args5, 5);
            args[0] = map;
            args[1] = owner;
            args[2] = key;
            args[3] = cell;
            args[4] = boxed;
            try
            {
                return (bool)_setContinuousSource.Invoke(null, args);
            }
            catch (Exception e)
            {
                _continuousSourceFailure = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                args[0] = null;
                args[1] = null;
                args[2] = null;
                args[4] = null;
            }
        }

        /// <summary>把 owner 名下 key 以 prefix 开头的连续源速率清零（源仍在，不再产出）。</summary>
        public static bool ZeroContinuousSourcesWithPrefix(Map map, Thing owner, string prefix)
        {
            if (map == null || owner == null || string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            Resolve();
            if (_zeroContinuousSourcesWithPrefix == null)
            {
                return false;
            }

            object[] args = GetArgs(ref _args3, 3);
            args[0] = map;
            args[1] = owner;
            args[2] = prefix;
            try
            {
                return (bool)_zeroContinuousSourcesWithPrefix.Invoke(null, args);
            }
            catch (Exception e)
            {
                _continuousSourceFailure = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                args[0] = null;
                args[1] = null;
                args[2] = null;
            }
        }

        /// <summary>删除 owner 名下 key 以 prefix 开头的连续源。</summary>
        public static bool RemoveContinuousSourcesWithPrefix(Map map, Thing owner, string prefix)
        {
            if (map == null || owner == null || string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            Resolve();
            if (_removeContinuousSourcesWithPrefix == null)
            {
                return false;
            }

            object[] args = GetArgs(ref _args3, 3);
            args[0] = map;
            args[1] = owner;
            args[2] = prefix;
            try
            {
                return (bool)_removeContinuousSourcesWithPrefix.Invoke(null, args);
            }
            catch (Exception e)
            {
                _continuousSourceFailure = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                args[0] = null;
                args[1] = null;
                args[2] = null;
            }
        }

        /// <summary>注销 owner 名下的全部连续源（建筑拆除/换图时用）。</summary>
        public static bool UnregisterContinuousSources(Map map, Thing owner)
        {
            if (map == null || owner == null)
            {
                return false;
            }

            Resolve();
            if (_unregisterContinuousSources == null)
            {
                return false;
            }

            object[] args = GetArgs(ref _args2, 2);
            args[0] = map;
            args[1] = owner;
            try
            {
                return (bool)_unregisterContinuousSources.Invoke(null, args);
            }
            catch (Exception e)
            {
                _continuousSourceFailure = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                args[0] = null;
                args[1] = null;
            }
        }

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

        // ===== 动态接口代理 =====
        // AO 的 IAtmosphereShield / IAtmospherePawnAirProvider 是接口，注册与注销都要求
        // 真实实现实例。本工程编译期不引用 AO，无法写 class : IAtmosphereShield，
        // 因此在运行时用 Reflection.Emit 生成一个只做静态转发的类型，例如：
        //   Covers(Map, IntVec3) -> 我们的静态判定
        //   TryGetAir(Pawn, out float, out float) -> 我们的静态判定
        // 类型只生成一次并缓存，之后每次判定都是普通静态调用，没有反射开销。

        private static ModuleBuilder _proxyModule;

        /// <summary>
        /// 生成一个实现 interfaceType 的代理实例，把名为 methodName 的接口方法
        /// 原样转发给静态方法 target。target 的参数与返回类型必须与接口方法一致。
        /// 生成失败返回 null，调用方回退到旧的 Harmony 补丁。
        /// </summary>
        public static object CreateForwardingProxy(Type interfaceType, string methodName, MethodInfo target)
        {
            if (interfaceType == null || string.IsNullOrEmpty(methodName) || target == null)
            {
                return null;
            }

            try
            {
                MethodInfo ifaceMethod = interfaceType.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (ifaceMethod == null || !SignatureMatches(ifaceMethod, target))
                {
                    return null;
                }

                ModuleBuilder module = GetProxyModule();
                if (module == null)
                {
                    return null;
                }

                string typeName = "AOPatch.Proxy." + interfaceType.Name + "." + methodName;
                TypeBuilder builder = module.DefineType(
                    typeName,
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                    typeof(object),
                    new[] { interfaceType });

                ParameterInfo[] parameters = ifaceMethod.GetParameters();
                Type[] parameterTypes = new Type[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    parameterTypes[i] = parameters[i].ParameterType;
                }

                MethodBuilder method = builder.DefineMethod(
                    methodName,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final
                        | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    ifaceMethod.ReturnType,
                    parameterTypes);

                ILGenerator il = method.GetILGenerator();
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    il.Emit(OpCodes.Ldarg, (short)(i + 1));
                }
                il.Emit(OpCodes.Call, target);
                il.Emit(OpCodes.Ret);

                builder.DefineMethodOverride(method, ifaceMethod);
                Type proxyType = builder.CreateType();
                return proxyType == null ? null : Activator.CreateInstance(proxyType);
            }
            catch (Exception e)
            {
                Log.Warning(
                    "[AOPatch/AO] 动态接口代理生成失败（" + interfaceType.Name + "." + methodName + "）："
                    + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static ModuleBuilder GetProxyModule()
        {
            if (_proxyModule == null)
            {
                AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                    new AssemblyName("AOPatch.DynamicProxies"),
                    AssemblyBuilderAccess.Run);
                _proxyModule = assembly.DefineDynamicModule("AOPatch.DynamicProxies");
            }
            return _proxyModule;
        }

        private static bool SignatureMatches(MethodInfo ifaceMethod, MethodInfo target)
        {
            if (ifaceMethod.ReturnType != target.ReturnType)
            {
                return false;
            }

            ParameterInfo[] a = ifaceMethod.GetParameters();
            ParameterInfo[] b = target.GetParameters();
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].ParameterType != b[i].ParameterType)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
