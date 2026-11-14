using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>
    /// 对 ProxyHeat 的反射桥。
    ///
    /// 设计取舍与 AoBridge 一致：
    /// 1. 全部按名字解析，ProxyHeat 缺省或改名时整体降级为「不接管」。
    /// 2. 只暴露转译器真正需要的最小面：读源列表、读/写 active、读 Props。
    /// 3. 类型、字段、方法只解析一次，缓存在静态字段里。
    ///
    /// 关键事实（来自 ProxyHeat 1.6 MVID 7358A296）：
    /// - ProxyHeatMod 是 internal，但 settings 是 public static 字段，反射可取。
    /// - ProxyHeatManager.compTemperatures / compTemperaturesToTick 都是 public List。
    /// - CompTemperatureSource.AffectedCells 是 public HashSet&lt;IntVec3&gt;。
    /// - active 是 private 字段，SetActive / ShouldBeActive 都是 private。
    /// - MarkDirty() 是 public，内部会把自己塞进 manager 的 dirtyComps。
    /// </summary>
    public static class ProxyHeatBridge
    {
        private const string ModTypeName = "ProxyHeat.ProxyHeatMod";
        private const string SettingsTypeName = "ProxyHeat.ProxyHeatSettings";
        private const string ManagerTypeName = "ProxyHeat.ProxyHeatManager";
        private const string CompTypeName = "ProxyHeat.CompTemperatureSource";
        private const string PropsTypeName = "ProxyHeat.CompProperties_TemperatureSource";

        private static bool _resolved;
        private static bool _available;
        private static string _failure;

        private static Type _modType;
        private static Type _settingsType;
        private static Type _managerType;
        private static Type _compType;
        private static Type _propsType;

        private static FieldInfo _settingsField;
        private static FieldInfo _enableProxyHeatField;
        private static FieldInfo _enableIndoorsField;

        private static FieldInfo _compTemperaturesField;
        private static FieldInfo _compTemperaturesToTickField;

        private static PropertyInfo _propsProp;
        private static PropertyInfo _affectedCellsProp;
        private static FieldInfo _activeField;
        private static PropertyInfo _shouldBeActiveProp;
        private static MethodInfo _setActiveMethod;
        private static MethodInfo _markDirtyMethod;

        private static FieldInfo _propTempOutcome;
        private static FieldInfo _propMinTemperature;
        private static FieldInfo _propMaxTemperature;
        private static FieldInfo _propRadius;
        private static FieldInfo _propDisabled;

        // SetActive 在每期模拟里按源调用，复用参数数组避免每次分配。
        // 模拟可能跑在线程池上，共享静态数组会写竞争，因此按线程各一份。
        [ThreadStatic] private static object[] _setActiveArgs;

        public static bool Available
        {
            get { Resolve(); return _available; }
        }

        public static string Failure
        {
            get { Resolve(); return _failure; }
        }

        /// <summary>ProxyHeat 的 CompTemperatureSource 类型，供 GetGizmos 之类做快速类型比对。</summary>
        public static Type CompType
        {
            get { Resolve(); return _compType; }
        }

        public static bool Resolve()
        {
            if (_resolved)
            {
                return _available;
            }
            _resolved = true;

            try
            {
                _modType = AccessTools.TypeByName(ModTypeName);
                _settingsType = AccessTools.TypeByName(SettingsTypeName);
                _managerType = AccessTools.TypeByName(ManagerTypeName);
                _compType = AccessTools.TypeByName(CompTypeName);
                _propsType = AccessTools.TypeByName(PropsTypeName);
                if (_modType == null || _settingsType == null || _managerType == null
                    || _compType == null || _propsType == null)
                {
                    _failure = "ProxyHeat 类型缺失";
                    return false;
                }

                _settingsField = AccessTools.Field(_modType, "settings");
                _enableProxyHeatField = AccessTools.Field(_settingsType, "enableProxyHeat");
                _enableIndoorsField = AccessTools.Field(_settingsType, "enableProxyHeatEffectIndoors");

                _compTemperaturesField = AccessTools.Field(_managerType, "compTemperatures");
                _compTemperaturesToTickField = AccessTools.Field(_managerType, "compTemperaturesToTick");

                _propsProp = AccessTools.Property(_compType, "Props");
                _affectedCellsProp = AccessTools.Property(_compType, "AffectedCells");
                _activeField = AccessTools.Field(_compType, "active");
                _shouldBeActiveProp = AccessTools.Property(_compType, "ShouldBeActive");
                _setActiveMethod = AccessTools.Method(_compType, "SetActive", new[] { typeof(bool) });
                _markDirtyMethod = AccessTools.Method(_compType, "MarkDirty", Type.EmptyTypes);

                _propTempOutcome = AccessTools.Field(_propsType, "tempOutcome");
                _propMinTemperature = AccessTools.Field(_propsType, "minTemperature");
                _propMaxTemperature = AccessTools.Field(_propsType, "maxTemperature");
                _propRadius = AccessTools.Field(_propsType, "radius");
                _propDisabled = AccessTools.Field(_propsType, "disabled");

                if (_settingsField == null || _enableProxyHeatField == null || _enableIndoorsField == null
                    || _compTemperaturesField == null || _compTemperaturesToTickField == null
                    || _propsProp == null || _affectedCellsProp == null || _activeField == null
                    || _shouldBeActiveProp == null || _setActiveMethod == null || _markDirtyMethod == null
                    || _propTempOutcome == null || _propMinTemperature == null || _propMaxTemperature == null
                    || _propRadius == null || _propDisabled == null)
                {
                    _failure = "ProxyHeat 成员不完整";
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

        // ---------------------------------------------------------------- 设置

        public static object GetSettings()
        {
            Resolve();
            if (!_available)
            {
                return null;
            }
            return _settingsField.GetValue(null);
        }

        public static bool GetEnableProxyHeat()
        {
            object settings = GetSettings();
            return settings != null && (bool)_enableProxyHeatField.GetValue(settings);
        }

        public static void SetEnableProxyHeat(bool value)
        {
            object settings = GetSettings();
            if (settings != null)
            {
                _enableProxyHeatField.SetValue(settings, value);
            }
        }

        public static void SetEnableIndoors(bool value)
        {
            object settings = GetSettings();
            if (settings != null)
            {
                _enableIndoorsField.SetValue(settings, value);
            }
        }

        // ---------------------------------------------------------------- 管理器和源列表

        public static object FindManager(Map map)
        {
            Resolve();
            if (!_available || map == null)
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
                MapComponent component = components[i];
                if (component != null && component.GetType() == _managerType)
                {
                    return component;
                }
            }
            return null;
        }

        public static IList GetActiveComps(object manager)
        {
            Resolve();
            if (!_available || manager == null)
            {
                return null;
            }
            return _compTemperaturesField.GetValue(manager) as IList;
        }

        public static IList GetTickComps(object manager)
        {
            Resolve();
            if (!_available || manager == null)
            {
                return null;
            }
            return _compTemperaturesToTickField.GetValue(manager) as IList;
        }

        // ---------------------------------------------------------------- 单个源

        public static Thing GetParent(object comp)
        {
            ThingComp thingComp = comp as ThingComp;
            return thingComp == null ? null : thingComp.parent;
        }

        public static object GetProps(object comp)
        {
            Resolve();
            if (!_available || comp == null)
            {
                return null;
            }
            return _propsProp.GetValue(comp);
        }

        public static HashSet<IntVec3> GetAffectedCells(object comp)
        {
            Resolve();
            if (!_available || comp == null)
            {
                return null;
            }
            return _affectedCellsProp.GetValue(comp) as HashSet<IntVec3>;
        }

        public static bool GetActive(object comp)
        {
            Resolve();
            return _available && comp != null && (bool)_activeField.GetValue(comp);
        }

        /// <summary>设置 active。内部同时把 dirty 置 true，但不会通知 manager。</summary>
        public static void SetActive(object comp, bool value)
        {
            Resolve();
            if (!_available || comp == null)
            {
                return;
            }
            object[] args = _setActiveArgs;
            if (args == null)
            {
                args = new object[1];
                _setActiveArgs = args;
            }
            args[0] = value;
            _setActiveMethod.Invoke(comp, args);
            args[0] = null;
        }

        /// <summary>public MarkDirty()，把自己塞进 manager 的 dirtyComps，下一个 tick 会重建照射区。</summary>
        public static void MarkDirty(object comp)
        {
            Resolve();
            if (!_available || comp == null)
            {
                return;
            }
            _markDirtyMethod.Invoke(comp, null);
        }

        public static bool GetShouldBeActive(object comp)
        {
            Resolve();
            return _available && comp != null && (bool)_shouldBeActiveProp.GetValue(comp);
        }

        /// <summary>在 Thing 上找 CompTemperatureSource。用 AllComps 逐项比对，不分配。</summary>
        public static bool TryGetComp(Thing thing, out object comp)
        {
            comp = null;
            Resolve();
            if (!_available || thing == null)
            {
                return false;
            }
            ThingWithComps holder = thing as ThingWithComps;
            if (holder == null)
            {
                return false;
            }
            List<ThingComp> comps = holder.AllComps;
            if (comps == null)
            {
                return false;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp candidate = comps[i];
                if (candidate != null && candidate.GetType() == _compType)
                {
                    comp = candidate;
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- Props

        public static bool IsDynamic(object props)
        {
            Resolve();
            return _available && props != null && _propTempOutcome.GetValue(props) == null;
        }

        public static float? GetTempOutcome(object props)
        {
            object value = _propTempOutcome.GetValue(props);
            return value == null ? (float?)null : (float)value;
        }

        public static float? GetMinTemperature(object props)
        {
            object value = _propMinTemperature.GetValue(props);
            return value == null ? (float?)null : (float)value;
        }

        public static float? GetMaxTemperature(object props)
        {
            object value = _propMaxTemperature.GetValue(props);
            return value == null ? (float?)null : (float)value;
        }

        public static float GetRadius(object props)
        {
            return (float)_propRadius.GetValue(props);
        }

        public static bool GetDisabled(object props)
        {
            return (bool)_propDisabled.GetValue(props);
        }
    }
}
