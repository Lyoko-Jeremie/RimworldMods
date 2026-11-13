using System;
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

                if (_engineField == null || _pendingDeltasField == null
                    || _engineSize == null || _engineWidth == null
                    || _temperatureProp == null || _groundTemperatureProp == null
                    || _obstacleMaskProp == null || _fieldReadProp == null)
                {
                    _failure = "AO 模拟场成员不完整";
                    return false;
                }

                ResolveAirMover();

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

                _airMoverAvailable = true;
            }
            catch (Exception e)
            {
                _airMoverFailure = e.GetType().Name + ": " + e.Message;
                _airMoverFields = null;
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
                object settings = Activator.CreateInstance(_airMoverSettingsType);
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

                object handle = _registerAirMover.Invoke(null, new object[]
                {
                    map, owner, key, inlet, outlet, settings, activeTicks,
                });
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
                _unregisterAirMovers.Invoke(null, new object[] { map, owner });
            }
            catch (Exception e)
            {
                _airMoverFailure = e.GetType().Name + ": " + e.Message;
                _airMoverAvailable = false;
            }
        }
    }
}
