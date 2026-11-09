using System;
using System.Reflection;
using Jeremie.AtmosphereOverhaulPatch.Util;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Api
{
    /// <summary>
    /// 对 Atmosphere Overhaul 公开 API（AtmosphereOverhaul.RimWorld.AtmosphereInterface）的反射封装。
    ///
    /// 采用反射而非静态引用，原因有三：
    /// 1. AO 未安装时本程序集仍能加载（软依赖）；
    /// 2. AO 更新改签名/改类型名时，本层降级为「返回 false」，不会连带崩溃；
    /// 3. 避免与 AO 的编译期版本强绑定。
    ///
    /// 约定：所有方法失败都返回 false，out 值给默认值，绝不抛异常。
    /// </summary>
    public static class AtmosphereApi
    {
        private const string InterfaceTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereInterface";

        private static Type interfaceType;
        private static bool resolved;

        public static bool Available
        {
            get
            {
                Resolve();
                return interfaceType != null;
            }
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            interfaceType = ReflectionCache.Type(InterfaceTypeName);
            resolved = true;
            if (interfaceType == null)
            {
                PatchLog.Warn("未找到 " + InterfaceTypeName + "，AO 相关补丁将以空操作运行。");
            }
        }

        private static object Invoke(string methodName, int parameterCount, object[] args)
        {
            Resolve();
            if (interfaceType == null)
            {
                return null;
            }

            MethodInfo method = ReflectionCache.Method(interfaceType, methodName, parameterCount);
            if (method == null)
            {
                PatchLog.Debug("AO API 中不存在方法 " + methodName + "（参数数 " + parameterCount + "）");
                return null;
            }

            try
            {
                return method.Invoke(null, args);
            }
            catch (Exception exception)
            {
                PatchLog.Warn("调用 AO API " + methodName + " 失败：" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 读
        // ------------------------------------------------------------------

        public static bool TryGetTemperature(Map map, IntVec3 cell, out float celsius)
        {
            celsius = 0f;
            object[] args = { map, cell, 0f };
            if (Invoke("TryGetTemperature", 3, args) is bool ok && ok && args[2] is float value)
            {
                celsius = value;
                return true;
            }

            return false;
        }

        public static bool TryGetPressurePascals(Map map, IntVec3 cell, out float pascals)
        {
            pascals = 0f;
            object[] args = { map, cell, 0f };
            if (Invoke("TryGetPressurePascals", 3, args) is bool ok && ok && args[2] is float value)
            {
                pascals = value;
                return true;
            }

            return false;
        }

        public static bool TryGetOxygenPercent(Map map, IntVec3 cell, out byte percent)
        {
            percent = 0;
            object[] args = { map, cell, (byte)0 };
            if (Invoke("TryGetOxygenPercent", 3, args) is bool ok && ok && args[2] is byte value)
            {
                percent = value;
                return true;
            }

            return false;
        }

        public static bool TryGetRelativeHumidity(Map map, IntVec3 cell, out float humidity)
        {
            humidity = 0f;
            object[] args = { map, cell, 0f };
            if (Invoke("TryGetRelativeHumidity", 3, args) is bool ok && ok && args[2] is float value)
            {
                humidity = value;
                return true;
            }

            return false;
        }

        public static bool TryGetWind(Map map, IntVec3 cell, out Vector2 wind)
        {
            wind = Vector2.zero;
            object[] args = { map, cell, Vector2.zero };
            if (Invoke("TryGetWind", 3, args) is bool ok && ok && args[2] is Vector2 value)
            {
                wind = value;
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // 写（增量）
        // ------------------------------------------------------------------

        public static bool AddTemperatureDelta(Map map, IntVec3 cell, float celsius)
        {
            return Invoke("AddTemperatureDelta", 3, new object[] { map, cell, celsius }) is bool ok && ok;
        }

        public static bool AddMoistureDelta(Map map, IntVec3 cell, float vapor)
        {
            return Invoke("AddMoistureDelta", 3, new object[] { map, cell, vapor }) is bool ok && ok;
        }

        public static bool AddMistDelta(Map map, IntVec3 cell, float mist)
        {
            return Invoke("AddMistDelta", 3, new object[] { map, cell, mist }) is bool ok && ok;
        }

        public static bool AddOxygenPercentDelta(Map map, IntVec3 cell, float percent)
        {
            return Invoke("AddOxygenPercentDelta", 3, new object[] { map, cell, percent }) is bool ok && ok;
        }

        public static bool AddPressureDelta(Map map, IntVec3 cell, float solverUnits)
        {
            return Invoke("AddPressureDelta", 3, new object[] { map, cell, solverUnits }) is bool ok && ok;
        }

        public static bool AddWindImpulse(Map map, IntVec3 cell, Vector2 delta)
        {
            return Invoke("AddWindImpulse", 3, new object[] { map, cell, delta }) is bool ok && ok;
        }

        public static bool SetAmbientWind(Map map, Vector2 wind)
        {
            return Invoke("SetAmbientWind", 2, new object[] { map, wind }) is bool ok && ok;
        }

        // TODO: AddGas(Map, AtmosphereGasDef, IntVec3, Single) 需要 AO 的 Def 类型句柄，
        // 后续在需要自定义气体时用 ReflectionCache.Type("AtmosphereOverhaul.Gases.AtmosphereGasDef")
        // 配合 DefDatabase 取实例再反射调用。
    }
}
