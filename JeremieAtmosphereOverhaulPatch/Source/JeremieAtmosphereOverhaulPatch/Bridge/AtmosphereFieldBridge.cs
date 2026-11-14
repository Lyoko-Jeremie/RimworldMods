using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Bridge
{
    /// <summary>
    /// 直读 AO 模拟场数组的反射桥。
    ///
    /// 与 AoBridge 分工：AoBridge 管「写连续源」，这里只管「拿到数组引用」。
    /// ProxyHeat 转译器每模拟周期要按格遍历，逐格走 AO 的公开 API
    /// （AtmosphereInterface.TryGetTemperature）会分配 object[] 并做字典查找，
    /// 量级上万格时不可接受，因此直接持有 float[] 引用。
    ///
    /// 所有反射结果只解析一次；AO 缺省或改名时整体降级为不可用，
    /// 由调用方回退到「不注入」，绝不让游戏启动失败。
    /// </summary>
    public static class AtmosphereFieldBridge
    {
        private const string ComponentTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereMapComponent";
        private const string EngineTypeName = "AtmosphereOverhaul.Simulation.LocalAtmosphereEngine";
        private const string FieldTypeName = "AtmosphereOverhaul.Simulation.AtmosphereField";
        private const string AirMoverTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereAirMoverSettings";
        private const string AirMoverInterfaceName = "AtmosphereOverhaul.RimWorld.AtmosphereInterface";
        private const string SourceRatesTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereSourceRates";
        private const string SourceHandleTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereSourceHandle";

        private static bool _resolved;
        private static bool _available;
        private static string _failure;

        private static Type _componentType;
        private static Type _engineType;
        private static Type _fieldType;

        private static FieldInfo _engineField;
        private static FieldInfo _pendingDeltasField;
        private static PropertyInfo _engineSize;
        private static PropertyInfo _engineWidth;
        private static PropertyInfo _temperatureProp;
        private static PropertyInfo _groundTemperatureProp;
        private static PropertyInfo _obstacleMaskProp;
        private static PropertyInfo _fieldReadProp;
        private static MethodInfo _markCachedDirty;

        private static Type _airMoverSettingsType;
        private static MethodInfo _registerAirMover;
        private static MethodInfo _unregisterAirMovers;
        private static PropertyInfo _airMoverHandleIsValid;
        private static FieldInfo[] _airMoverFields;
        private static readonly string[] AirMoverFieldNames =
        {
            "CubicMetresPerTick",
            "AirColumnHeightMetres",
            "InletDirection",
            "OutletDirection",
            "MaxPressureRisePascals",
            "ConditionExhaust",
            "ExhaustSpeed",
            "ExhaustTemperatureFloor",
            "ExhaustPressureRatio",
            "InletPressureRatio",
            "ExhaustOxygenPercent",
            "WaterKgPerTick",
        };

        private static bool _airMoverAvailable;
        private static string _airMoverFailure;

        private static Type _sourceRatesType;
        private static Type _sourceHandleType;
        private static MethodInfo _registerSource;
        private static MethodInfo _updateSource;
        private static MethodInfo _unregisterSources;
        private static PropertyInfo _sourceHandleIsValid;
        private static MethodInfo _addTemperatureDelta;
        private static FieldInfo[] _sourceFields;
        private static readonly string[] SourceFieldNames =
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

        private static bool _sourceAvailable;
        private static string _sourceFailure;

        // AO 的 AtmosphereSourceRates 是 struct，反射调用必须装箱。
        // 连续源在每期通风/喷流刷新时都要调一次，这里按线程复用装箱实例与参数数组，
        // 避免每 60 tick、每条源都产生一次分配。AO 端按值接收 rates，不保留装箱引用。
        [ThreadStatic] private static object _sourceRatesBox;
        [ThreadStatic] private static object[] _sourceArgs5;
        [ThreadStatic] private static object[] _sourceArgs3;
        [ThreadStatic] private static object[] _sourceArgs2;

        // 喷流设置同样是 struct，注册时反射写字段必须装箱；按线程复用。
        [ThreadStatic] private static object _airMoverSettingsBox;
        [ThreadStatic] private static object[] _airMoverArgs7;

        // 装箱实例的构造委托：Activator.CreateInstance 每次都要查构造器，
        // 这里在首次使用时编译一次。
        private static Func<object> _sourceRatesFactory;
        private static Func<object> _airMoverSettingsFactory;

        // 参数全是 BCL/游戏类型的方法可以缓存强类型委托，绕过 Invoke 的打包。
        private delegate bool AddTemperatureDeltaDelegate(Map map, IntVec3 cell, float delta);
        private delegate int UnregisterByOwnerDelegate(Map map, Thing owner);
        private static AddTemperatureDeltaDelegate _addTemperatureDeltaFast;
        private static UnregisterByOwnerDelegate _unregisterSourcesFast;
        private static UnregisterByOwnerDelegate _unregisterAirMoversFast;

        public static bool Available
        {
            get { Resolve(); return _available; }
        }

        public static string Failure
        {
            get { Resolve(); return _failure; }
        }

        /// <summary>喷流通道是否可用（独立于空气/地暖）。</summary>
        public static bool AirMoverAvailable
        {
            get { Resolve(); return _airMoverAvailable; }
        }

        public static string AirMoverFailure
        {
            get { Resolve(); return _airMoverFailure; }
        }

        /// <summary>连续源通道是否可用。</summary>
        public static bool SourceAvailable
        {
            get { Resolve(); return _sourceAvailable; }
        }

        public static string SourceFailure
        {
            get { Resolve(); return _sourceFailure; }
        }

        /// <summary>解析 AO 的模拟场内部成员。可重复调用，只在首次真正解析。</summary>
        public static bool Resolve()
        {
            if (_resolved)
            {
                return _available;
            }
            _resolved = true;

            try
            {
                _componentType = AccessTools.TypeByName(ComponentTypeName);
                _engineType = AccessTools.TypeByName(EngineTypeName);
                _fieldType = AccessTools.TypeByName(FieldTypeName);
                if (_componentType == null || _engineType == null || _fieldType == null)
                {
                    _failure = "AO 模拟场类型缺失";
                    return false;
                }

                _engineField = AccessTools.Field(_componentType, "engine");
                _pendingDeltasField = AccessTools.Field(_componentType, "pendingTemperatureDeltas");
                _engineSize = AccessTools.Property(_engineType, "Size");
                _engineWidth = AccessTools.Property(_engineType, "Width");
                _temperatureProp = AccessTools.Property(_engineType, "Temperature");
                _groundTemperatureProp = AccessTools.Property(_engineType, "GroundTemperature");
                _obstacleMaskProp = AccessTools.Property(_engineType, "ObstacleMask");
                _fieldReadProp = AccessTools.Property(_fieldType, "Read");
                _markCachedDirty = AccessTools.Method(_componentType, "MarkCachedMapDataDirty");

                if (_engineField == null || _pendingDeltasField == null
                    || _engineSize == null || _engineWidth == null
                    || _temperatureProp == null || _groundTemperatureProp == null
                    || _obstacleMaskProp == null || _fieldReadProp == null)
                {
                    _failure = "AO 模拟场成员不完整";
                    return false;
                }

                ResolveAirMover();
                ResolveSources();

                _available = true;
                return true;
            }
            catch (Exception e)
            {
                _failure = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 解析喷流通道需要的 AtmosphereInterface.RegisterAirMover。
        /// 这是独立能力：失败只让「喷流模式」降级，不影响空气/地暖。
        /// </summary>
        private static void ResolveAirMover()
        {
            try
            {
                _airMoverSettingsType = AccessTools.TypeByName(AirMoverTypeName);
                Type interfaceType = AccessTools.TypeByName(AirMoverInterfaceName);
                if (_airMoverSettingsType == null || interfaceType == null)
                {
                    _airMoverFailure = "AO 喷流类型缺失";
                    return;
                }

                _airMoverFields = new FieldInfo[AirMoverFieldNames.Length];
                for (int i = 0; i < AirMoverFieldNames.Length; i++)
                {
                    _airMoverFields[i] = AccessTools.Field(_airMoverSettingsType, AirMoverFieldNames[i]);
                    if (_airMoverFields[i] == null)
                    {
                        _airMoverFailure = "AO 喷流字段缺失: " + AirMoverFieldNames[i];
                        _airMoverFields = null;
                        return;
                    }
                }

                _registerAirMover = AccessTools.Method(interfaceType, "RegisterAirMover", new[]
                {
                    typeof(Map), typeof(Thing), typeof(string), typeof(IntVec3), typeof(IntVec3),
                    _airMoverSettingsType, typeof(float),
                });
                if (_registerAirMover == null)
                {
                    _airMoverFailure = "AtmosphereInterface.RegisterAirMover 未找到";
                    _airMoverFields = null;
                    return;
                }

                _airMoverHandleIsValid = AccessTools.Property(_registerAirMover.ReturnType, "IsValid");
                if (_airMoverHandleIsValid == null)
                {
                    _airMoverFailure = "AtmosphereAirMoverHandle.IsValid 未找到";
                    _airMoverFields = null;
                    return;
                }

                // 注销用：关掉喷流开关后必须主动清掉已经注册的 mover，
                // 因为我们用 activeTicks = -1（永不过期），不会自己消失。
                _unregisterAirMovers = AccessTools.Method(interfaceType, "UnregisterAirMovers", new[]
                {
                    typeof(Map), typeof(Thing),
                });
                if (_unregisterAirMovers == null)
                {
                    _airMoverFailure = "AtmosphereInterface.UnregisterAirMovers 未找到";
                    _airMoverFields = null;
                    return;
                }

                _unregisterAirMoversFast = MakeFast<UnregisterByOwnerDelegate>(_unregisterAirMovers);
                _airMoverAvailable = true;
            }
            catch (Exception e)
            {
                _airMoverFailure = e.GetType().Name + ": " + e.Message;
                _airMoverFields = null;
            }
        }

        /// <summary>
        /// 解析连续源通道（AtmosphereInterface.RegisterContinuousSource 等）。
        /// 与喷流一样是独立能力：失败只让「端口源」降级，不影响其它通道。
        /// </summary>
        private static void ResolveSources()
        {
            try
            {
                _sourceRatesType = AccessTools.TypeByName(SourceRatesTypeName);
                _sourceHandleType = AccessTools.TypeByName(SourceHandleTypeName);
                Type interfaceType = AccessTools.TypeByName(AirMoverInterfaceName);
                if (_sourceRatesType == null || _sourceHandleType == null || interfaceType == null)
                {
                    _sourceFailure = "AO 连续源类型缺失";
                    return;
                }

                _sourceFields = new FieldInfo[SourceFieldNames.Length];
                for (int i = 0; i < SourceFieldNames.Length; i++)
                {
                    _sourceFields[i] = AccessTools.Field(_sourceRatesType, SourceFieldNames[i]);
                    if (_sourceFields[i] == null)
                    {
                        _sourceFailure = "AO 连续源字段缺失: " + SourceFieldNames[i];
                        _sourceFields = null;
                        return;
                    }
                }

                _registerSource = AccessTools.Method(interfaceType, "RegisterContinuousSource", new[]
                {
                    typeof(Map), typeof(Thing), typeof(string), typeof(IntVec3), _sourceRatesType,
                });
                _updateSource = AccessTools.Method(interfaceType, "UpdateContinuousSource", new[]
                {
                    typeof(Map), typeof(Thing), _sourceHandleType, typeof(IntVec3), _sourceRatesType,
                });
                _unregisterSources = AccessTools.Method(interfaceType, "UnregisterContinuousSources", new[]
                {
                    typeof(Map), typeof(Thing),
                });
                _addTemperatureDelta = AccessTools.Method(interfaceType, "AddTemperatureDelta", new[]
                {
                    typeof(Map), typeof(IntVec3), typeof(float),
                });

                if (_registerSource == null || _updateSource == null || _unregisterSources == null
                    || _addTemperatureDelta == null)
                {
                    _sourceFailure = "AtmosphereInterface 连续源方法缺失";
                    _sourceFields = null;
                    return;
                }

                _sourceHandleIsValid = AccessTools.Property(_sourceHandleType, "IsValid");
                if (_sourceHandleIsValid == null)
                {
                    _sourceFailure = "AtmosphereSourceHandle.IsValid 未找到";
                    _sourceFields = null;
                    return;
                }

                _addTemperatureDeltaFast = MakeFast<AddTemperatureDeltaDelegate>(_addTemperatureDelta);
                _unregisterSourcesFast = MakeFast<UnregisterByOwnerDelegate>(_unregisterSources);
                _sourceAvailable = true;
            }
            catch (Exception e)
            {
                _sourceFailure = e.GetType().Name + ": " + e.Message;
                _sourceFields = null;
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

        /// <summary>找地图上的 AO 大气组件（不是 AO 地图时返回 null）。</summary>
        public static object FindComponent(Map map)
        {
            Resolve();
            if (map == null || _componentType == null)
            {
                return null;
            }
            System.Collections.Generic.List<MapComponent> components = map.components;
            if (components == null)
            {
                return null;
            }
            for (int i = 0; i < components.Count; i++)
            {
                MapComponent component = components[i];
                if (component != null && component.GetType() == _componentType)
                {
                    return component;
                }
            }
            return null;
        }

        public static object GetEngine(object component)
        {
            Resolve();
            if (!_available || component == null)
            {
                return null;
            }
            return _engineField.GetValue(component);
        }

        public static float[] GetPendingDeltas(object component)
        {
            Resolve();
            if (!_available || component == null)
            {
                return null;
            }
            return _pendingDeltasField.GetValue(component) as float[];
        }

        public static int GetSize(object engine)
        {
            Resolve();
            if (!_available || engine == null)
            {
                return 0;
            }
            return (int)_engineSize.GetValue(engine);
        }

        public static int GetWidth(object engine)
        {
            Resolve();
            if (!_available || engine == null)
            {
                return 0;
            }
            return (int)_engineWidth.GetValue(engine);
        }

        /// <summary>当前空气温度场（摄氏度）。返回的是活引用，只读。</summary>
        public static float[] GetTemperatureRead(object engine)
        {
            Resolve();
            if (!_available || engine == null)
            {
                return null;
            }
            object field = _temperatureProp.GetValue(engine);
            if (field == null)
            {
                return null;
            }
            return _fieldReadProp.GetValue(field) as float[];
        }

        public static float[] GetGroundTemperature(object engine)
        {
            Resolve();
            if (!_available || engine == null)
            {
                return null;
            }
            return _groundTemperatureProp.GetValue(engine) as float[];
        }

        public static float[] GetObstacleMask(object engine)
        {
            Resolve();
            if (!_available || engine == null)
            {
                return null;
            }
            return _obstacleMaskProp.GetValue(engine) as float[];
        }

        /// <summary>
        /// 通知 AO 重建地图掩码（障碍/屋顶/面开放/通风口表）。
        /// 改 CompAirVent.speedLimit 后必须调用，否则 vent 格的 obstacleMask 与 faceOpen 不会更新。
        /// </summary>
        public static void MarkCachedMapDataDirty(Map map)
        {
            Resolve();
            if (map == null || _markCachedDirty == null)
            {
                return;
            }

            object component = FindComponent(map);
            if (component == null)
            {
                return;
            }

            _markCachedDirty.Invoke(component, null);
        }

        /// <summary>
        /// 连续源参数。字段顺序与 AO 的 <c>AtmosphereSourceRates</c> 一一对应。
        /// 单位：TemperaturePerTick 摄氏度/游戏 tick；Wind 为 Vector2。
        /// </summary>
        public struct SourceRates
        {
            public float TemperaturePerTick;
            public float GroundTemperaturePerTick;
            public float MoisturePerTick;
            public float MistPerTick;
            public float WindX;
            public float WindY;
            public float PressurePerTick;
            public float OxygenPercentPerTick;
            public bool MaintainTargetWind;
            public float TargetWindX;
            public float TargetWindY;
            public float TargetWindBlendPerTick;
        }

        /// <summary>
        /// 注册一条连续源，返回 AO 的句柄（装箱的 AtmosphereSourceHandle），失败返回 null。
        /// 句柄必须由调用方保存，后续用 <see cref="UpdateContinuousSource"/> 更新；
        /// 注销用 <see cref="UnregisterContinuousSources"/>（按 owner 一次清全部）。
        /// </summary>
        public static object RegisterContinuousSource(Map map, Thing owner, string key, IntVec3 cell, SourceRates r)
        {
            Resolve();
            if (!_sourceAvailable || map == null || owner == null || _sourceFields == null)
            {
                return null;
            }

            try
            {
                object rates = GetSourceRatesBox();
                if (rates == null)
                {
                    return null;
                }
                WriteSourceRates(rates, r);
                object[] args = GetArgs(ref _sourceArgs5, 5);
                args[0] = map;
                args[1] = owner;
                args[2] = key;
                args[3] = cell;
                args[4] = rates;
                object handle = _registerSource.Invoke(null, args);
                if (handle == null)
                {
                    return null;
                }
                return (bool)_sourceHandleIsValid.GetValue(handle) ? handle : null;
            }
            catch (Exception e)
            {
                _sourceFailure = e.GetType().Name + ": " + e.Message;
                _sourceAvailable = false;
                return null;
            }
            finally
            {
                // 复用数组不能长期持有地图/建筑引用。
                if (_sourceArgs5 != null)
                {
                    _sourceArgs5[0] = null;
                    _sourceArgs5[1] = null;
                    _sourceArgs5[2] = null;
                }
            }
        }

        /// <summary>更新已有连续源的参数（可同时改挂载格）。</summary>
        public static bool UpdateContinuousSource(Map map, Thing owner, object handle, IntVec3 cell, SourceRates r)
        {
            Resolve();
            if (!_sourceAvailable || handle == null || map == null || owner == null || _sourceFields == null)
            {
                return false;
            }

            try
            {
                object rates = GetSourceRatesBox();
                if (rates == null)
                {
                    return false;
                }
                WriteSourceRates(rates, r);
                object[] args = GetArgs(ref _sourceArgs5, 5);
                args[0] = map;
                args[1] = owner;
                args[2] = handle;
                args[3] = cell;
                args[4] = rates;
                object result = _updateSource.Invoke(null, args);
                return result is bool && (bool)result;
            }
            catch (Exception e)
            {
                _sourceFailure = e.GetType().Name + ": " + e.Message;
                _sourceAvailable = false;
                return false;
            }
            finally
            {
                if (_sourceArgs5 != null)
                {
                    _sourceArgs5[0] = null;
                    _sourceArgs5[1] = null;
                }
            }
        }

        /// <summary>注销某个 owner 名下的全部连续源，返回清理数量。</summary>
        public static int UnregisterContinuousSources(Map map, Thing owner)
        {
            Resolve();
            if (!_sourceAvailable || _unregisterSources == null || map == null || owner == null)
            {
                return 0;
            }

            try
            {
                if (_unregisterSourcesFast != null)
                {
                    return _unregisterSourcesFast(map, owner);
                }
                object[] args = GetArgs(ref _sourceArgs2, 2);
                args[0] = map;
                args[1] = owner;
                object result = _unregisterSources.Invoke(null, args);
                args[0] = null;
                args[1] = null;
                return result is int ? (int)result : 0;
            }
            catch (Exception e)
            {
                _sourceFailure = e.GetType().Name + ": " + e.Message;
                _sourceAvailable = false;
                return 0;
            }
        }

        /// <summary>
        /// 成对温度注入的单边调用。AO 内部会加锁并入队到下一步，
        /// 因此同一模拟周期内对 inlet/outlet 各调一次即可。
        /// </summary>
        public static bool AddTemperatureDelta(Map map, IntVec3 cell, float deltaCelsius)
        {
            Resolve();
            if (!_sourceAvailable || _addTemperatureDelta == null || map == null)
            {
                return false;
            }

            try
            {
                if (_addTemperatureDeltaFast != null)
                {
                    return _addTemperatureDeltaFast(map, cell, deltaCelsius);
                }
                object[] args = GetArgs(ref _sourceArgs3, 3);
                args[0] = map;
                args[1] = cell;
                args[2] = deltaCelsius;
                object result = _addTemperatureDelta.Invoke(null, args);
                args[0] = null;
                return result is bool && (bool)result;
            }
            catch (Exception e)
            {
                _sourceFailure = e.GetType().Name + ": " + e.Message;
                _sourceAvailable = false;
                return false;
            }
        }

        private static void WriteSourceRates(object rates, SourceRates r)
        {
            _sourceFields[0].SetValue(rates, r.TemperaturePerTick);
            _sourceFields[1].SetValue(rates, r.GroundTemperaturePerTick);
            _sourceFields[2].SetValue(rates, r.MoisturePerTick);
            _sourceFields[3].SetValue(rates, r.MistPerTick);
            _sourceFields[4].SetValue(rates, new UnityEngine.Vector2(r.WindX, r.WindY));
            _sourceFields[5].SetValue(rates, r.PressurePerTick);
            _sourceFields[6].SetValue(rates, r.OxygenPercentPerTick);
            _sourceFields[7].SetValue(rates, r.MaintainTargetWind);
            _sourceFields[8].SetValue(rates, new UnityEngine.Vector2(r.TargetWindX, r.TargetWindY));
            _sourceFields[9].SetValue(rates, r.TargetWindBlendPerTick);
        }

        /// <summary>
        /// 喷流参数。字段顺序与 AO 的 <c>AtmosphereAirMoverSettings</c> 一一对应，
        /// 由 <see cref="RegisterAirMover"/> 反射填进去。
        /// 单位：CubicMetresPerTick 立方米/tick，方向为归一化 Vector2。
        /// </summary>
        public struct AirMoverParams
        {
            public float CubicMetresPerTick;
            public float AirColumnHeightMetres;
            public float InletDirectionX;
            public float InletDirectionY;
            public float OutletDirectionX;
            public float OutletDirectionY;
            public float MaxPressureRisePascals;
            public bool ConditionExhaust;
            public float ExhaustSpeed;
            public float ExhaustTemperatureFloor;
            public float ExhaustPressureRatio;
            public float InletPressureRatio;
            public float ExhaustOxygenPercent;
            public float WaterKgPerTick;
        }

        /// <summary>
        /// 注册（或刷新）一条喷流。AO 内部按 owner+key 去重并就地更新，
        /// 因此可以每模拟周期无脑调用，不必自己维护 handle 生命周期。
        /// 返回 AO 是否接受了这条喷流。
        /// </summary>
        public static bool RegisterAirMover(Map map, Thing owner, string key, IntVec3 inlet, IntVec3 outlet,
                                           AirMoverParams p, float activeTicks)
        {
            Resolve();
            if (!_airMoverAvailable || map == null || owner == null || _airMoverFields == null)
            {
                return false;
            }

            try
            {
                object settings = GetAirMoverSettingsBox();
                if (settings == null)
                {
                    return false;
                }
                _airMoverFields[0].SetValue(settings, p.CubicMetresPerTick);
                _airMoverFields[1].SetValue(settings, p.AirColumnHeightMetres);
                _airMoverFields[2].SetValue(settings, new UnityEngine.Vector2(p.InletDirectionX, p.InletDirectionY));
                _airMoverFields[3].SetValue(settings, new UnityEngine.Vector2(p.OutletDirectionX, p.OutletDirectionY));
                _airMoverFields[4].SetValue(settings, p.MaxPressureRisePascals);
                _airMoverFields[5].SetValue(settings, p.ConditionExhaust);
                _airMoverFields[6].SetValue(settings, p.ExhaustSpeed);
                _airMoverFields[7].SetValue(settings, p.ExhaustTemperatureFloor);
                _airMoverFields[8].SetValue(settings, p.ExhaustPressureRatio);
                _airMoverFields[9].SetValue(settings, p.InletPressureRatio);
                _airMoverFields[10].SetValue(settings, p.ExhaustOxygenPercent);
                _airMoverFields[11].SetValue(settings, p.WaterKgPerTick);

                object[] args = GetArgs(ref _airMoverArgs7, 7);
                args[0] = map;
                args[1] = owner;
                args[2] = key;
                args[3] = inlet;
                args[4] = outlet;
                args[5] = settings;
                args[6] = activeTicks;
                object handle;
                try
                {
                    handle = _registerAirMover.Invoke(null, args);
                }
                finally
                {
                    // 复用数组不能长期持有地图/建筑引用。
                    args[0] = null;
                    args[1] = null;
                    args[2] = null;
                    args[5] = null;
                }
                if (handle == null)
                {
                    return false;
                }
                return (bool)_airMoverHandleIsValid.GetValue(handle);
            }
            catch (Exception e)
            {
                _airMoverFailure = e.GetType().Name + ": " + e.Message;
                _airMoverAvailable = false;
                return false;
            }
        }

        /// <summary>
        /// 注销某个 owner 注册过的全部 AirMover。
        /// 我们统一用 activeTicks = -1，AO 不会自己让它们过期，
        /// 所以关掉喷流开关、建筑断电、目标温度跌破环境温度时都必须显式清掉，
        /// 否则会残留一条永远在吹的热气流。
        /// </summary>
        public static void UnregisterAirMovers(Map map, Thing owner)
        {
            Resolve();
            if (!_airMoverAvailable || _unregisterAirMovers == null || map == null || owner == null)
            {
                return;
            }

            try
            {
                if (_unregisterAirMoversFast != null)
                {
                    _unregisterAirMoversFast(map, owner);
                    return;
                }
                object[] args = GetArgs(ref _sourceArgs2, 2);
                args[0] = map;
                args[1] = owner;
                _unregisterAirMovers.Invoke(null, args);
                args[0] = null;
                args[1] = null;
            }
            catch (Exception e)
            {
                _airMoverFailure = e.GetType().Name + ": " + e.Message;
                _airMoverAvailable = false;
            }
        }

        /// <summary>
        /// 取本线程复用的 rates 装箱实例。AO 的接口按值接收 struct，
        /// 不会把装箱对象留到下一次调用之后，因此同线程内原地改写是安全的。
        /// </summary>
        private static object GetSourceRatesBox()
        {
            object boxed = _sourceRatesBox;
            if (boxed == null)
            {
                if (_sourceRatesFactory == null)
                {
                    _sourceRatesFactory = CreateFactory(_sourceRatesType);
                }
                boxed = _sourceRatesFactory != null ? _sourceRatesFactory() : null;
                _sourceRatesBox = boxed;
            }
            return boxed;
        }

        /// <summary>取本线程复用的喷流设置装箱实例。</summary>
        private static object GetAirMoverSettingsBox()
        {
            object boxed = _airMoverSettingsBox;
            if (boxed == null)
            {
                if (_airMoverSettingsFactory == null)
                {
                    _airMoverSettingsFactory = CreateFactory(_airMoverSettingsType);
                }
                boxed = _airMoverSettingsFactory != null ? _airMoverSettingsFactory() : null;
                _airMoverSettingsBox = boxed;
            }
            return boxed;
        }

        /// <summary>取本线程复用的反射参数数组，长度固定后不再分配。</summary>
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
