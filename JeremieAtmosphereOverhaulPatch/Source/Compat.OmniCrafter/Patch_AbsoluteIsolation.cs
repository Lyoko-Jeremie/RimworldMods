using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 穹顶绝对隔离：在 AO 每模拟步的「投影定稿之后、快照发布之前」
    /// 把穹顶覆盖格六条通道硬写成地图目标值。
    ///
    /// 为什么挂在 LocalAtmosphereEngine.PublishSnapshot 的 Prefix：
    /// 1. 该函数在 engineLock 内、模拟 Task 内被调用，此时 ProjectAndFinalize 已完成，
    ///    步内所有守恒演化、边界松弛、相变都已结算；
    /// 2. PublishSnapshot 会把 Read 复制到 Snapshot，写 Read 后立刻对外可见，
    ///    不会有「场里是目标值、读数还是旧值」的一步延迟；
    /// 3. 早于此写（例如 PrepareTemperatureSources 的 Postfix）会被后续演化覆盖。
    ///
    /// 写的是 AO 引擎的活数组，不经过 AddDelta/连续源：
    /// 后者有锁与装箱开销，且在 30 tick 一档的步末覆盖场景下没有意义。
    ///
    /// 线程约束：模拟 Task 与主线程并行，Prefix 里只允许读「主线程预先构建好」的数据
    /// （引擎上下文与穹顶格数组），绝不枚举建筑或地图列表。
    /// </summary>
    internal static class Patch_AbsoluteIsolation
    {
        private const string EngineTypeName = "AtmosphereOverhaul.Simulation.LocalAtmosphereEngine";

        internal sealed class EngineContext
        {
            public object Component;
            public Map Map;
            public MapComponent_OmniAtmosphereTargets Targets;
        }

        private static readonly Dictionary<object, EngineContext> Contexts =
            new Dictionary<object, EngineContext>();

        private static readonly object Sync = new object();

        public static bool Install(Harmony harmony)
        {
            Type engineType = AccessTools.TypeByName(EngineTypeName);
            if (engineType == null)
            {
                return false;
            }

            MethodInfo target = AccessTools.Method(engineType, "PublishSnapshot");
            if (target == null)
            {
                return false;
            }

            if (!AtmosphereFieldBridge.SixChannelAvailable)
            {
                return false;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Patch_AbsoluteIsolation), nameof(Prefix)));
            return true;
        }

        /// <summary>主线程调用：把某地图的引擎与目标值登记好，供模拟线程前缀查表。</summary>
        public static void Register(object engine, object component, Map map,
            MapComponent_OmniAtmosphereTargets targets)
        {
            if (engine == null || map == null || targets == null)
            {
                return;
            }

            lock (Sync)
            {
                Contexts[engine] = new EngineContext
                {
                    Component = component,
                    Map = map,
                    Targets = targets,
                };
            }
        }

        /// <summary>地图卸载时清理，避免静态字典钉住 Map 与引擎。</summary>
        public static void Forget(Map map)
        {
            if (map == null)
            {
                return;
            }

            lock (Sync)
            {
                List<object> remove = null;
                foreach (KeyValuePair<object, EngineContext> pair in Contexts)
                {
                    if (ReferenceEquals(pair.Value.Map, map))
                    {
                        if (remove == null)
                        {
                            remove = new List<object>();
                        }
                        remove.Add(pair.Key);
                    }
                }

                if (remove != null)
                {
                    for (int i = 0; i < remove.Count; i++)
                    {
                        Contexts.Remove(remove[i]);
                    }
                }
            }
        }

        private static void Prefix(object __instance)
        {
            if (__instance == null || !AtmosphereFieldBridge.SixChannelAvailable)
            {
                return;
            }

            EngineContext ctx;
            lock (Sync)
            {
                Contexts.TryGetValue(__instance, out ctx);
            }

            if (ctx == null || ctx.Map == null || ctx.Map.Disposed || ctx.Targets == null)
            {
                return;
            }

            Map map = ctx.Map;
            int[] cells = OmniDomeZoneCache.GetCells(map);
            if (cells.Length == 0)
            {
                return;
            }

            MapComponent_OmniAtmosphereTargets targets = ctx.Targets;
            object settings = AtmosphereFieldBridge.GetSettings(ctx.Component);
            if (settings == null)
            {
                return;
            }

            // 每步只算一次的派生量。
            float vaporCapacity = AtmosphereFieldBridge.CalculateVaporCapacity(
                __instance, targets.TargetTemperature, settings);
            if (vaporCapacity <= 0f)
            {
                return;
            }

            float pascals = AtmosphereFieldBridge.GetSolverPascalsPerUnit();
            if (pascals <= 0f)
            {
                pascals = 1000f;
            }

            float ambientPa = AtmosphereFieldBridge.GetAmbientSurfacePressurePa(__instance);
            // 气压可以「跟随地图大气」：直接用环境基准，避免玩家手填值与环境差太大，
            // 在穹顶边缘形成巨大面通量（AO 的边界扩散按两侧压差驱动）。
            float targetPa = targets.ResolveTargetPressurePa(ambientPa);
            float pressureReadTarget = (targetPa - 100000f) / pascals;
            float storedPressureTarget = targetPa - ambientPa;
            float moistureTarget = targets.TargetHumidity * 0.01f * vaporCapacity;
            float humidityTarget = targets.TargetHumidity * 0.01f;
            float temperatureTarget = targets.TargetTemperature;
            byte oxygenTarget = (byte)Mathf.Clamp(Mathf.RoundToInt(targets.TargetOxygenPercent), 0, 100);

            float[] temperature = AtmosphereFieldBridge.GetTemperatureRead(__instance);
            float[] moisture = AtmosphereFieldBridge.GetMoistureRead(__instance);
            float[] relativeHumidity = AtmosphereFieldBridge.GetRelativeHumidityRead(__instance);
            float[] pressure = AtmosphereFieldBridge.GetPressureRead(__instance);
            float[] storedPressure = AtmosphereFieldBridge.GetStoredPressurePa(__instance);
            float[] velocityX = AtmosphereFieldBridge.GetVelocityXRead(__instance);
            float[] velocityZ = AtmosphereFieldBridge.GetVelocityZRead(__instance);
            float[] groundTemperature = AtmosphereFieldBridge.GetGroundTemperature(__instance);
            byte[] oxygen = AtmosphereFieldBridge.GetOxygenRead(__instance);
            float[] oxygenResidual = AtmosphereFieldBridge.GetOxygenResidual(__instance);
            float[] roofMask = AtmosphereFieldBridge.GetRoofMask(__instance);

            if (temperature == null || moisture == null || relativeHumidity == null
                || pressure == null || storedPressure == null
                || velocityX == null || velocityZ == null
                || groundTemperature == null || oxygen == null)
            {
                return;
            }

            for (int k = 0; k < cells.Length; k++)
            {
                int index = cells[k];
                // 不按 obstacleMask 跳过：几何隔离开启时整片穹顶的掩膜会被置 0，
                // 若仍跳过就会让读出层对整片区域失效。真实墙格写 Read 无害——
                // 相邻 faceOpen 为 0，采样与守恒统计都不会取到墙格。
                temperature[index] = temperatureTarget;
                moisture[index] = moistureTarget;
                relativeHumidity[index] = humidityTarget;
                pressure[index] = pressureReadTarget;
                storedPressure[index] = storedPressureTarget;
                groundTemperature[index] = temperatureTarget;
                velocityX[index] = 0f;
                velocityZ[index] = 0f;
                oxygen[index] = oxygenTarget;
                if (oxygenResidual != null)
                {
                    oxygenResidual[index] = 0f;
                }

                // 大气屋顶掩膜与真实屋顶解耦：AO 按有顶处理（不落雨、无环境风、相变按室内），
                // 但不动 map.roofGrid，所以原版采光、植物光照与房间判定保持原样。
                if (roofMask != null)
                {
                    roofMask[index] = 1f;
                }
            }
        }
    }
}
