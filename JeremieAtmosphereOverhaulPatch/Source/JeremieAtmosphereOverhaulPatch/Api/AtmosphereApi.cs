using System;
using System.Collections.Generic;
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
    /// 所有 MethodInfo/FieldInfo 在首次使用时缓存，避免高频路径上的反射查找。
    /// </summary>
    public static class AtmosphereApi
    {
        private const string InterfaceTypeName = "AtmosphereOverhaul.RimWorld.AtmosphereInterface";
        private const string SettingsTypeName = "AtmosphereOverhaul.AtmosphereOverhaulSettings";

        private static Type interfaceType;
        private static Type deltasType;
        private static Type ratesType;
        private static Type handleType;
        private static Type gasDefType;
        private static bool resolved;

        private static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, FieldInfo> DeltasFieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, FieldInfo> RatesFieldCache = new Dictionary<string, FieldInfo>();

        // ------------------------------------------------------------------
        // 能力探测
        // ------------------------------------------------------------------

        public static bool Available
        {
            get
            {
                Resolve();
                return interfaceType != null;
            }
        }

        /// <summary>AO 是否提供 AtmosphereDeltas 整包写入（AddDeltas 系列）。</summary>
        public static bool HasDeltas
        {
            get { Resolve(); return deltasType != null; }
        }

        /// <summary>AO 是否提供持续源 API。</summary>
        public static bool HasContinuousSources
        {
            get { Resolve(); return handleType != null && ratesType != null; }
        }

        /// <summary>AO 是否提供自定义气体 API。</summary>
        public static bool HasGasApi
        {
            get { Resolve(); return gasDefType != null; }
        }

        /// <summary>AO 的气体 Def 类型（不透明句柄，仅用于传给本类方法）。</summary>
        public static Type GasDefType
        {
            get { Resolve(); return gasDefType; }
        }

        /// <summary>
        /// AO 的模拟间隔（tick）。AddDeltas 的增量会在求解器步进时统一应用，
        /// 因此调用节奏必须与它对齐，否则同一求解器步内会重复累加。
        /// </summary>
        public static int SimulationIntervalTicks
        {
            get
            {
                try
                {
                    Type settingsType = ReflectionCache.Type(SettingsTypeName);
                    if (settingsType != null)
                    {
                        PropertyInfo property = settingsType.GetProperty(
                            "SimulationIntervalTicks",
                            BindingFlags.Public | BindingFlags.Static);
                        if (property != null)
                        {
                            object value = property.GetValue(null, null);
                            if (value is int ticks && ticks > 0)
                            {
                                return ticks;
                            }
                        }
                    }
                }
                catch
                {
                    // 读不到就用 AO 的默认值
                }

                return 6;
            }
        }

        public static float SolverPressureToPascals
        {
            get
            {
                try
                {
                    Type utility = ReflectionCache.Type("AtmosphereOverhaul.RimWorld.AtmosphereColorUtility");
                    if (utility != null)
                    {
                        MethodInfo method = ReflectionCache.Method(utility, "CurrentSolverPressureToPascals", 0);
                        if (method != null && method.Invoke(null, null) is float factor && factor > 0f)
                        {
                            return factor;
                        }
                    }
                }
                catch
                {
                    // 忽略
                }

                // AO 当前固定为 1000 Pa / solver 单位
                return 1000f;
            }
        }

        // ------------------------------------------------------------------
        // 解析与调用
        // ------------------------------------------------------------------

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;
            interfaceType = ReflectionCache.Type(InterfaceTypeName);
            if (interfaceType == null)
            {
                PatchLog.Warn("未找到 " + InterfaceTypeName + "，AO 相关补丁将以空操作运行。");
                return;
            }

            deltasType = FindParamType("AddDeltas", 3, 2);
            gasDefType = FindParamType("AddGas", 4, 1);

            MethodInfo register = FindMethod("RegisterContinuousSource");
            if (register != null)
            {
                handleType = register.ReturnType;
                ParameterInfo[] parameters = register.GetParameters();
                if (parameters.Length == 5)
                {
                    ratesType = parameters[4].ParameterType;
                }
            }
        }

        private static MethodInfo FindMethod(string name)
        {
            try
            {
                MethodInfo[] methods = interfaceType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == name)
                    {
                        return methods[i];
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return null;
        }

        private static Type FindParamType(string methodName, int parameterCount, int index)
        {
            try
            {
                MethodInfo[] methods = interfaceType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != methodName)
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (parameters.Length == parameterCount)
                    {
                        return parameters[index].ParameterType;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return null;
        }

        private static string Key(string name, Type[] types)
        {
            if (types == null || types.Length == 0)
            {
                return name + "/0";
            }

            string key = name + "/" + types.Length;
            for (int i = 0; i < types.Length; i++)
            {
                key += "/" + (types[i] == null ? "null" : types[i].Name);
            }

            return key;
        }

        private static MethodInfo Method(string name, params Type[] types)
        {
            Resolve();
            if (interfaceType == null)
            {
                return null;
            }

            string key = Key(name, types);
            MethodInfo cached;
            if (MethodCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            MethodInfo found = null;
            try
            {
                found = interfaceType.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                    null,
                    types,
                    null);
            }
            catch
            {
                found = null;
            }

            MethodCache[key] = found;
            return found;
        }

        private static object Invoke(MethodInfo method, object[] args)
        {
            if (method == null)
            {
                return null;
            }

            try
            {
                return method.Invoke(null, args);
            }
            catch (Exception exception)
            {
                PatchLog.Warn("调用 AO API " + method.Name + " 失败：" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private static bool InvokeBool(string name, Type[] types, object[] args)
        {
            return Invoke(Method(name, types), args) is bool ok && ok;
        }

        // ------------------------------------------------------------------
        // 读
        // ------------------------------------------------------------------

        public static bool TryGetTemperature(Map map, IntVec3 cell, out float celsius)
        {
            celsius = 0f;
            MethodInfo method = Method("TryGetTemperature", typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[2] is float value)
            {
                celsius = value;
                return true;
            }

            return false;
        }

        public static bool TryGetGroundTemperature(Map map, IntVec3 cell, out float celsius)
        {
            celsius = 0f;
            MethodInfo method = Method("TryGetGroundTemperature", typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[2] is float value)
            {
                celsius = value;
                return true;
            }

            return false;
        }

        public static bool TryGetPressurePascals(Map map, IntVec3 cell, out float pascals)
        {
            pascals = 0f;
            MethodInfo method = Method("TryGetPressurePascals", typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[2] is float value)
            {
                pascals = value;
                return true;
            }

            return false;
        }

        public static bool TryGetOxygenPercent(Map map, IntVec3 cell, out byte percent)
        {
            percent = 0;
            MethodInfo method = Method("TryGetOxygenPercent", typeof(Map), typeof(IntVec3), typeof(byte).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, (byte)0 };
            if (Invoke(method, args) is bool ok && ok && args[2] is byte value)
            {
                percent = value;
                return true;
            }

            return false;
        }

        public static bool TryGetRelativeHumidity(Map map, IntVec3 cell, out float humidity)
        {
            humidity = 0f;
            MethodInfo method = Method("TryGetRelativeHumidity", typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[2] is float value)
            {
                humidity = value;
                return true;
            }

            return false;
        }

        public static bool TryGetAbsoluteVapor(Map map, IntVec3 cell, out float vapor)
        {
            vapor = 0f;
            MethodInfo method = Method("TryGetAbsoluteVapor", typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[2] is float value)
            {
                vapor = value;
                return true;
            }

            return false;
        }

        public static bool TryGetMist(Map map, IntVec3 cell, out float mist)
        {
            mist = 0f;
            MethodInfo method = Method("TryGetMist", typeof(Map), typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[2] is float value)
            {
                mist = value;
                return true;
            }

            return false;
        }

        public static bool TryGetWind(Map map, IntVec3 cell, out Vector2 wind)
        {
            wind = Vector2.zero;
            MethodInfo method = Method("TryGetWind", typeof(Map), typeof(IntVec3), typeof(Vector2).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, cell, Vector2.zero };
            if (Invoke(method, args) is bool ok && ok && args[2] is Vector2 value)
            {
                wind = value;
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // 写（单字段增量）
        // ------------------------------------------------------------------

        public static bool AddTemperatureDelta(Map map, IntVec3 cell, float celsius)
        {
            return InvokeBool("AddTemperatureDelta", new[] { typeof(Map), typeof(IntVec3), typeof(float) }, new object[] { map, cell, celsius });
        }

        public static bool AddGroundTemperatureDelta(Map map, IntVec3 cell, float celsius)
        {
            return InvokeBool("AddGroundTemperatureDelta", new[] { typeof(Map), typeof(IntVec3), typeof(float) }, new object[] { map, cell, celsius });
        }

        public static bool AddMoistureDelta(Map map, IntVec3 cell, float vapor)
        {
            return InvokeBool("AddMoistureDelta", new[] { typeof(Map), typeof(IntVec3), typeof(float) }, new object[] { map, cell, vapor });
        }

        public static bool AddMistDelta(Map map, IntVec3 cell, float mist)
        {
            return InvokeBool("AddMistDelta", new[] { typeof(Map), typeof(IntVec3), typeof(float) }, new object[] { map, cell, mist });
        }

        public static bool AddOxygenPercentDelta(Map map, IntVec3 cell, float percent)
        {
            return InvokeBool("AddOxygenPercentDelta", new[] { typeof(Map), typeof(IntVec3), typeof(float) }, new object[] { map, cell, percent });
        }

        public static bool AddPressureDelta(Map map, IntVec3 cell, float solverUnits)
        {
            return InvokeBool("AddPressureDelta", new[] { typeof(Map), typeof(IntVec3), typeof(float) }, new object[] { map, cell, solverUnits });
        }

        public static bool AddWindImpulse(Map map, IntVec3 cell, Vector2 delta)
        {
            return InvokeBool("AddWindImpulse", new[] { typeof(Map), typeof(IntVec3), typeof(Vector2) }, new object[] { map, cell, delta });
        }

        public static bool SetAmbientWind(Map map, Vector2 wind)
        {
            return InvokeBool("SetAmbientWind", new[] { typeof(Map), typeof(Vector2) }, new object[] { map, wind });
        }

        // ------------------------------------------------------------------
        // 写（体积源）
        // ------------------------------------------------------------------

        public static bool AddVolumeSource(Map map, IntVec3 cell, float cellVolumes, float oxygenPercent, float temperatureOffsetCelsius)
        {
            return InvokeBool(
                "AddVolumeSource",
                new[] { typeof(Map), typeof(IntVec3), typeof(float), typeof(float), typeof(float) },
                new object[] { map, cell, cellVolumes, oxygenPercent, temperatureOffsetCelsius });
        }

        public static bool AddVolumeSourceAtTemperature(Map map, IntVec3 cell, float cellVolumes, float oxygenPercent, float gasCelsius)
        {
            return InvokeBool(
                "AddVolumeSourceAtTemperature",
                new[] { typeof(Map), typeof(IntVec3), typeof(float), typeof(float), typeof(float) },
                new object[] { map, cell, cellVolumes, oxygenPercent, gasCelsius });
        }

        // ------------------------------------------------------------------
        // 写（AtmosphereDeltas 整包）
        // ------------------------------------------------------------------

        private static FieldInfo DeltasField(string name)
        {
            Resolve();
            if (deltasType == null)
            {
                return null;
            }

            FieldInfo field;
            if (DeltasFieldCache.TryGetValue(name, out field))
            {
                return field;
            }

            field = ReflectionCache.Field(deltasType, name);
            DeltasFieldCache[name] = field;
            return field;
        }

        /// <summary>构造一个 boxed 的 AtmosphereDeltas；AO 不可用时返回 null。</summary>
        public static object NewDeltas()
        {
            Resolve();
            if (deltasType == null)
            {
                return null;
            }

            try
            {
                return Activator.CreateInstance(deltasType);
            }
            catch
            {
                return null;
            }
        }

        public static object NewRates()
        {
            Resolve();
            if (ratesType == null)
            {
                return null;
            }

            try
            {
                return Activator.CreateInstance(ratesType);
            }
            catch
            {
                return null;
            }
        }

        private static void SetDeltasField(object deltas, string name, object value)
        {
            FieldInfo field = DeltasField(name);
            if (field == null)
            {
                return;
            }

            try
            {
                field.SetValue(deltas, value);
            }
            catch
            {
                // 忽略单字段失败
            }
        }

        /// <summary>
        /// 一次性写入整组增量。任一读值失败时对应参数传 0 即可。
        /// 注意：AO 会把这些增量累加到「下一次求解器步进」，
        /// 因此调用节奏应与 <see cref="SimulationIntervalTicks"/> 对齐。
        /// </summary>
        public static bool AddDeltas(Map map, IntVec3 cell, float temperatureDelta, float pressureDelta, float oxygenDelta, float moistureDelta, float mistDelta, Vector2 windDelta)
        {
            object deltas = NewDeltas();
            if (deltas == null)
            {
                return false;
            }

            SetDeltasField(deltas, "Temperature", temperatureDelta);
            SetDeltasField(deltas, "Pressure", pressureDelta);
            SetDeltasField(deltas, "OxygenPercent", oxygenDelta);
            SetDeltasField(deltas, "Moisture", moistureDelta);
            SetDeltasField(deltas, "Mist", mistDelta);
            SetDeltasField(deltas, "Wind", windDelta);

            return InvokeBool(
                "AddDeltas",
                new[] { typeof(Map), typeof(IntVec3), deltasType },
                new object[] { map, cell, deltas });
        }

        public static int AddDeltas(Map map, CellRect cells, float temperatureDelta, float pressureDelta, float oxygenDelta, float moistureDelta, float mistDelta, Vector2 windDelta)
        {
            object deltas = NewDeltas();
            if (deltas == null)
            {
                return 0;
            }

            SetDeltasField(deltas, "Temperature", temperatureDelta);
            SetDeltasField(deltas, "Pressure", pressureDelta);
            SetDeltasField(deltas, "OxygenPercent", oxygenDelta);
            SetDeltasField(deltas, "Moisture", moistureDelta);
            SetDeltasField(deltas, "Mist", mistDelta);
            SetDeltasField(deltas, "Wind", windDelta);

            MethodInfo method = Method("AddDeltas", typeof(Map), typeof(CellRect), deltasType);
            if (method == null)
            {
                return 0;
            }

            object result = Invoke(method, new object[] { map, cells, deltas });
            return result is int count ? count : 0;
        }

        public static int AddDeltasInRadius(Map map, IntVec3 center, float radius, float temperatureDelta, float pressureDelta, float oxygenDelta, float moistureDelta, float mistDelta, Vector2 windDelta, bool linearFalloff)
        {
            object deltas = NewDeltas();
            if (deltas == null)
            {
                return 0;
            }

            SetDeltasField(deltas, "Temperature", temperatureDelta);
            SetDeltasField(deltas, "Pressure", pressureDelta);
            SetDeltasField(deltas, "OxygenPercent", oxygenDelta);
            SetDeltasField(deltas, "Moisture", moistureDelta);
            SetDeltasField(deltas, "Mist", mistDelta);
            SetDeltasField(deltas, "Wind", windDelta);

            MethodInfo method = Method("AddDeltasInRadius", typeof(Map), typeof(IntVec3), typeof(float), deltasType, typeof(bool));
            if (method == null)
            {
                return 0;
            }

            object result = Invoke(method, new object[] { map, center, radius, deltas, linearFalloff });
            return result is int count ? count : 0;
        }

        // ------------------------------------------------------------------
        // 气体
        // ------------------------------------------------------------------

        /// <summary>按 defName 取 AO 气体 Def；失败返回 null。</summary>
        public static Def GetGasDef(string defName)
        {
            Resolve();
            if (gasDefType == null || string.IsNullOrEmpty(defName))
            {
                return null;
            }

            try
            {
                return GenDefDatabase.GetDefSilentFail(gasDefType, defName, false);
            }
            catch
            {
                return null;
            }
        }

        public static bool AddGas(Map map, Def gasDef, IntVec3 cell, float amount)
        {
            Resolve();
            if (gasDefType == null || gasDef == null)
            {
                return false;
            }

            return InvokeBool("AddGas", new[] { typeof(Map), gasDefType, typeof(IntVec3), typeof(float) }, new object[] { map, gasDef, cell, amount });
        }

        public static bool TryGetGas(Map map, Def gasDef, IntVec3 cell, out float density)
        {
            density = 0f;
            Resolve();
            if (gasDefType == null || gasDef == null)
            {
                return false;
            }

            MethodInfo method = Method("TryGetGas", typeof(Map), gasDefType, typeof(IntVec3), typeof(float).MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, gasDef, cell, 0f };
            if (Invoke(method, args) is bool ok && ok && args[3] is float value)
            {
                density = value;
                return true;
            }

            return false;
        }

        /// <summary>遍历某气体所有非零格。AO 不可用时立即返回 0。</summary>
        public static int ForEachGasCell(Map map, Def gasDef, Action<IntVec3, float> action)
        {
            Resolve();
            if (gasDefType == null || gasDef == null || action == null)
            {
                return 0;
            }

            MethodInfo method = Method("ForEachGasCell", typeof(Map), gasDefType, typeof(Action<IntVec3, float>));
            if (method == null)
            {
                return 0;
            }

            int count = 0;
            Action<IntVec3, float> wrapped = delegate (IntVec3 cell, float density)
            {
                count++;
                action(cell, density);
            };

            Invoke(method, new object[] { map, gasDef, wrapped });
            return count;
        }

        // ------------------------------------------------------------------
        // 持续源
        // ------------------------------------------------------------------

        private static FieldInfo RatesField(string name)
        {
            Resolve();
            if (ratesType == null)
            {
                return null;
            }

            FieldInfo field;
            if (RatesFieldCache.TryGetValue(name, out field))
            {
                return field;
            }

            field = ReflectionCache.Field(ratesType, name);
            RatesFieldCache[name] = field;
            return field;
        }

        public static void SetRate(object rates, string name, object value)
        {
            FieldInfo field = RatesField(name);
            if (field == null)
            {
                return;
            }

            try
            {
                field.SetValue(rates, value);
            }
            catch
            {
                // 忽略
            }
        }

        public static float GetRate(object rates, string name)
        {
            FieldInfo field = RatesField(name);
            if (field == null)
            {
                return 0f;
            }

            try
            {
                object value = field.GetValue(rates);
                return value is float number ? number : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        public static object RegisterContinuousSource(Map map, Thing owner, string sourceKey, IntVec3 cell, object rates)
        {
            Resolve();
            if (handleType == null)
            {
                return null;
            }

            MethodInfo method = Method("RegisterContinuousSource", typeof(Map), typeof(Thing), typeof(string), typeof(IntVec3), ratesType);
            if (method == null)
            {
                return null;
            }

            return Invoke(method, new object[] { map, owner, sourceKey, cell, rates });
        }

        public static bool UpdateContinuousSource(Map map, Thing owner, object handle, object rates)
        {
            Resolve();
            if (handleType == null)
            {
                return false;
            }

            MethodInfo method = Method("UpdateContinuousSource", typeof(Map), typeof(Thing), handleType, ratesType);
            if (method == null)
            {
                return false;
            }

            return Invoke(method, new object[] { map, owner, handle, rates }) is bool ok && ok;
        }

        public static bool UpdateContinuousSource(Map map, Thing owner, object handle, IntVec3 cell, object rates)
        {
            Resolve();
            if (handleType == null)
            {
                return false;
            }

            MethodInfo method = Method("UpdateContinuousSource", typeof(Map), typeof(Thing), handleType, typeof(IntVec3), ratesType);
            if (method == null)
            {
                return false;
            }

            return Invoke(method, new object[] { map, owner, handle, cell, rates }) is bool ok && ok;
        }

        public static bool UnregisterContinuousSource(Map map, Thing owner, object handle)
        {
            Resolve();
            if (handleType == null)
            {
                return false;
            }

            MethodInfo method = Method("UnregisterContinuousSource", typeof(Map), typeof(Thing), handleType);
            if (method == null)
            {
                return false;
            }

            return Invoke(method, new object[] { map, owner, handle }) is bool ok && ok;
        }

        public static int UnregisterContinuousSources(Map map, Thing owner)
        {
            MethodInfo method = Method("UnregisterContinuousSources", typeof(Map), typeof(Thing));
            if (method == null)
            {
                return 0;
            }

            object result = Invoke(method, new object[] { map, owner });
            return result is int count ? count : 0;
        }

        public static bool TryGetContinuousSourceHandle(Map map, Thing owner, string sourceKey, out object handle)
        {
            handle = null;
            Resolve();
            if (handleType == null)
            {
                return false;
            }

            MethodInfo method = Method("TryGetContinuousSourceHandle", typeof(Map), typeof(Thing), typeof(string), handleType.MakeByRefType());
            if (method == null)
            {
                return false;
            }

            object[] args = { map, owner, sourceKey, Activator.CreateInstance(handleType) };
            if (Invoke(method, args) is bool ok && ok)
            {
                handle = args[3];
                return true;
            }

            return false;
        }
    }
}
