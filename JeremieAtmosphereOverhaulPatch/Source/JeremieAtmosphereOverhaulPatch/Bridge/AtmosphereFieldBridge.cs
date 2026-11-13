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

        public static bool Available
        {
            get { Resolve(); return _available; }
        }

        public static string Failure
        {
            get { Resolve(); return _failure; }
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

                _available = true;
                return true;
            }
            catch (Exception e)
            {
                _failure = e.GetType().Name + ": " + e.Message;
                return false;
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
    }
}
