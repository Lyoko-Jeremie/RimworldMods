using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using FullyAutomaticOmniCrafter;
using HarmonyLib;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 让原版温度读数与 AO 场里的目标值一致。
    ///
    /// 超维自己的温度补丁是「读数覆盖」：穹顶直接返回 props.targetTemperature，
    /// 生物圈直接返回 targetTemperature。它们用 HarmonyPriority.Last(100)，
    /// 会盖掉 AO 在 800 上做的权威温度覆盖；而 AO 场里我们写的是地图级目标值，
    /// 于是读数与场会分裂成两个数。
    ///
    /// 这里用更晚的优先级（90）再覆盖一次，让两者的读数都落到地图级目标值上。
    /// 生物圈只在「控制温度」开启时覆盖，保留玩家关掉控温后回到自然温度的开关语义。
    ///
    /// 性能：GetTemperatureForCell 每帧会被调用上千次，这里把地图级查询结果
    /// 缓存在弱表里，热路径上只剩一次弱表查找 + 一次格数组判断。
    /// </summary>
    internal static class Patch_OmniAtmosphereTemperature
    {
        private sealed class MapRefs
        {
            public MapComponent_OmniAtmosphereTargets Targets;
            public OmniForceFieldDomeNetworkManager Domes;
        }

        private static readonly ConditionalWeakTable<Map, MapRefs> Refs =
            new ConditionalWeakTable<Map, MapRefs>();

        public static bool Install(Harmony harmony)
        {
            bool ok = true;

            MethodInfo get = AccessTools.Method(typeof(GenTemperature), "GetTemperatureForCell",
                new[] { typeof(IntVec3), typeof(Map) });
            if (get != null)
            {
                harmony.Patch(get, postfix: new HarmonyMethod(
                    typeof(Patch_OmniAtmosphereTemperature), nameof(GetTemperaturePostfix))
                {
                    priority = 90
                });
            }
            else
            {
                ok = false;
            }

            MethodInfo tryGet = AccessTools.Method(typeof(GenTemperature), "TryGetTemperatureForCell",
                new[] { typeof(IntVec3), typeof(Map), typeof(float).MakeByRefType() });
            if (tryGet != null)
            {
                harmony.Patch(tryGet, postfix: new HarmonyMethod(
                    typeof(Patch_OmniAtmosphereTemperature), nameof(TryGetTemperaturePostfix))
                {
                    priority = 90
                });
            }
            else
            {
                ok = false;
            }

            return ok;
        }

        private static MapRefs GetRefs(Map map)
        {
            MapRefs refs;
            if (Refs.TryGetValue(map, out refs))
            {
                return refs;
            }

            refs = new MapRefs
            {
                Targets = MapComponent_OmniAtmosphereTargets.Get(map),
                Domes = map.GetComponent<OmniForceFieldDomeNetworkManager>(),
            };

            // 组件还没建好时不要缓存，下一次调用再试。
            if (refs.Targets != null && refs.Domes != null)
            {
                try
                {
                    Refs.Add(map, refs);
                }
                catch (ArgumentException)
                {
                    // 弱表不支持并发 Add：若 AO 模拟线程与主线程同时首次查询同一张地图，
                    // 后到者会撞上「键已存在」。此时用本地实例返回即可，
                    // 下一次调用会命中先写入的那份等价对象。
                }
            }
            return refs;
        }

        /// <summary>
        /// 主线程预热：把该地图的组件引用提前写进弱表。
        /// AO 的温度查询补丁链可能在模拟线程上被调用（第三方 mod 也会调 GenTemperature），
        /// 提前在主线程完成 GetComponent 可以避免模拟线程首次查询时遍历地图组件列表。
        /// </summary>
        internal static void Warm(Map map)
        {
            if (map != null)
            {
                GetRefs(map);
            }
        }

        private static bool TryResolve(Map map, IntVec3 cell, out float temperature)
        {
            temperature = 0f;
            if (map == null || !cell.InBounds(map))
            {
                return false;
            }

            MapRefs refs = GetRefs(map);
            if (refs.Targets == null)
            {
                return false;
            }

            if (refs.Domes != null && refs.Domes.IsDomeCell(cell))
            {
                temperature = refs.Targets.TargetTemperature;
                return true;
            }

            CompBiosphere biosphere = CompBiosphereManager.GetBiosphereAt(map, cell);
            if (biosphere != null && biosphere.controlTemperature)
            {
                temperature = refs.Targets.TargetTemperature;
                return true;
            }

            return false;
        }

        private static void GetTemperaturePostfix(IntVec3 c, Map map, ref float __result)
        {
            float temperature;
            if (TryResolve(map, c, out temperature))
            {
                __result = temperature;
            }
        }

        private static void TryGetTemperaturePostfix(IntVec3 c, Map map, ref bool __result, ref float tempResult)
        {
            float temperature;
            if (__result && TryResolve(map, c, out temperature))
            {
                tempResult = temperature;
            }
        }
    }
}
