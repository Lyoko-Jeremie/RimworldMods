using System;
using System.Collections.Generic;
using System.Reflection;
using Jeremie.AtmosphereOverhaulPatch.Util;

namespace Jeremie.AtmosphereOverhaulPatch.Api
{
    /// <summary>
    /// AO 公开 API 的能力集合。模块通过 <see cref="CompatModuleBase.Required"/> 声明自己依赖哪些能力，
    /// 缺能力时模块会被标成 Skipped 而不是 Failed。
    /// </summary>
    [Flags]
    public enum AoCapability
    {
        None = 0,

        /// <summary>AtmosphereDeltas 整包写入（AddDeltas 系列）。</summary>
        Deltas = 1 << 0,

        /// <summary>持续源（RegisterContinuousSource 系列）。</summary>
        ContinuousSources = 1 << 1,

        /// <summary>自定义气体（AddGas/TryGetGas/ForEachGasCell）。</summary>
        Gas = 1 << 2,

        /// <summary>空气搬运器（RegisterAirMover）。</summary>
        AirMover = 1 << 3,

        /// <summary>体积源（AddVolumeSource 系列）。</summary>
        VolumeSource = 1 << 4,
    }

    /// <summary>
    /// AO 能力探测。全部基于反射结果，AO 缺席或改签名时对应能力为 false。
    /// </summary>
    public static class AoCapabilities
    {
        public static AoCapability Available
        {
            get
            {
                AoCapability value = AoCapability.None;

                if (!AtmosphereApi.Available)
                {
                    return value;
                }

                if (AtmosphereApi.HasDeltas)
                {
                    value |= AoCapability.Deltas;
                }

                if (AtmosphereApi.HasContinuousSources)
                {
                    value |= AoCapability.ContinuousSources;
                }

                if (AtmosphereApi.HasGasApi)
                {
                    value |= AoCapability.Gas;
                }

                if (HasMethod("RegisterAirMover"))
                {
                    value |= AoCapability.AirMover;
                }

                if (HasMethod("AddVolumeSource"))
                {
                    value |= AoCapability.VolumeSource;
                }

                return value;
            }
        }

        public static int SimulationIntervalTicks
        {
            get { return AtmosphereApi.SimulationIntervalTicks; }
        }

        /// <summary>人类可读的能力列表，写进模块 Detail 与日志。</summary>
        public static string Describe(AoCapability capability)
        {
            if (capability == AoCapability.None)
            {
                return "none";
            }

            List<string> parts = new List<string>();
            if ((capability & AoCapability.Deltas) != 0)
            {
                parts.Add("deltas");
            }

            if ((capability & AoCapability.ContinuousSources) != 0)
            {
                parts.Add("sources");
            }

            if ((capability & AoCapability.Gas) != 0)
            {
                parts.Add("gas");
            }

            if ((capability & AoCapability.AirMover) != 0)
            {
                parts.Add("airmover");
            }

            if ((capability & AoCapability.VolumeSource) != 0)
            {
                parts.Add("volume");
            }

            return string.Join(", ", parts.ToArray());
        }

        private static bool HasMethod(string name)
        {
            try
            {
                Type type = ReflectionCache.Type("AtmosphereOverhaul.RimWorld.AtmosphereInterface");
                if (type == null)
                {
                    return false;
                }

                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == name)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return false;
        }
    }
}
