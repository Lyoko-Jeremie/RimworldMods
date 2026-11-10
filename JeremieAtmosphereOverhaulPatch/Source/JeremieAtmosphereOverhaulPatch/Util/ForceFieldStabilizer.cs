using System.Collections.Generic;
using Jeremie.AtmosphereOverhaulPatch.Api;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Util
{
    /// <summary>
    /// 每张地图一个，负责把「强制稳定区」（生物圈、穹顶发生器）内的 AO 物理场
    /// 周期性拉回目标值。
    ///
    /// 设计要点：
    /// - AO 的 AddDeltas 只是把增量排进下一次求解器步进，所以这里按
    ///   SimulationIntervalTicks 的整数倍触发，避免同一步内重复累加。
    /// - 小区域逐格精确修正；大区域先采样求平均，再用 CellRect 批量写统一增量，
    ///   用多轮迭代收敛，避免几千格逐格反射把帧率拖死。
    /// - 无 AO 时整体空操作，不会因为缺 API 报错。
    /// </summary>
    public class ForceFieldStabilizer : MapComponent
    {
        /// <summary>超过这个格数就走「采样 + 批量」路径。</summary>
        private const int BulkThreshold = 256;

        /// <summary>批量路径每次采样的格数。</summary>
        private const int SampleCount = 64;

        /// <summary>批量路径的收敛系数（一次只补一半，多轮逼近）。</summary>
        private const float BulkGain = 0.5f;

        /// <summary>逐格路径的修正系数（1 = 一次补足）。</summary>
        private const float CellGain = 1f;

        /// <summary>目标气压：常压 101325 Pa。</summary>
        private const float TargetPressurePascals = 101325f;

        /// <summary>湿度目标（相对湿度）。超过就抽湿，低于不管，避免把干燥房间强行加湿。</summary>
        private const float TargetRelativeHumidity = 0.75f;

        private sealed class Entry
        {
            public IForceFieldRegion Region;
            public int NextIndex;
        }

        private readonly List<Entry> entries = new List<Entry>();

        public ForceFieldStabilizer(Map map) : base(map)
        {
        }

        public int RegionCount
        {
            get { return entries.Count; }
        }

        public void Register(IForceFieldRegion region)
        {
            if (region == null)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i].Region, region))
                {
                    return;
                }
            }

            entries.Add(new Entry { Region = region });
            PatchLog.Debug("强制稳定区注册：" + region.RegionName + "（当前 " + entries.Count + " 个）");
        }

        public void Unregister(IForceFieldRegion region)
        {
            if (region == null)
            {
                return;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(entries[i].Region, region))
                {
                    entries.RemoveAt(i);
                    PatchLog.Debug("强制稳定区注销：" + region.RegionName);
                }
            }
        }

        public override void MapComponentTick()
        {
            if (entries.Count == 0)
            {
                return;
            }

            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.EnableForceField)
            {
                return;
            }

            if (!AtmosphereApi.Available)
            {
                return;
            }

            int interval = Mathf.Max(1, settings.ForceFieldIntervalTicks);
            if (!map.IsHashIntervalTick(interval))
            {
                return;
            }

            int budget = Mathf.Max(16, settings.ForceFieldCellsPerInterval);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                IForceFieldRegion region = entry.Region;
                if (region == null || !region.Active)
                {
                    continue;
                }

                try
                {
                    Process(entry, budget);
                }
                catch (System.Exception exception)
                {
                    PatchLog.WarnOnce(
                        "ForceField:" + region.RegionName,
                        "强制稳定区 " + region.RegionName + " 更新失败：" + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private void Process(Entry entry, int budget)
        {
            IForceFieldRegion region = entry.Region;
            List<IntVec3> cells = region.Cells;
            if (cells == null || cells.Count == 0)
            {
                return;
            }

            CellRect? rect = region.BulkRect;
            if (cells.Count > BulkThreshold && rect.HasValue && rect.Value.Width > 0 && rect.Value.Height > 0)
            {
                ProcessBulk(region, cells, rect.Value);
                return;
            }

            int count = cells.Count;
            int index = entry.NextIndex;
            if (index < 0 || index >= count)
            {
                index = 0;
            }

            int processed = 0;
            while (processed < budget && index < count)
            {
                Stabilize(region, cells[index]);
                index++;
                processed++;
            }

            if (index >= count)
            {
                index = 0;
            }

            entry.NextIndex = index;
        }

        /// <summary>采样求均值后对整个矩形写一次统一增量（多轮收敛）。</summary>
        private void ProcessBulk(IForceFieldRegion region, List<IntVec3> cells, CellRect rect)
        {
            int count = cells.Count;
            int step = Mathf.Max(1, count / SampleCount);

            float sumTemperature = 0f;
            int validTemperature = 0;
            float sumPressure = 0f;
            int validPressure = 0;

            for (int i = 0; i < count; i += step)
            {
                IntVec3 cell = cells[i];

                float temperature;
                if (AtmosphereApi.TryGetTemperature(map, cell, out temperature))
                {
                    sumTemperature += temperature;
                    validTemperature++;
                }

                if (region.StabilizePressure)
                {
                    float pascals;
                    if (AtmosphereApi.TryGetPressurePascals(map, cell, out pascals))
                    {
                        sumPressure += pascals;
                        validPressure++;
                    }
                }
            }

            if (validTemperature == 0)
            {
                return;
            }

            float deltaTemperature = (region.TargetTemperature - sumTemperature / validTemperature) * BulkGain;

            float deltaPressure = 0f;
            if (region.StabilizePressure && validPressure > 0)
            {
                deltaPressure = (TargetPressurePascals - sumPressure / validPressure)
                                / AtmosphereApi.SolverPressureToPascals * BulkGain;
            }

            if (Mathf.Abs(deltaTemperature) < 0.01f && Mathf.Abs(deltaPressure) < 0.001f)
            {
                return;
            }

            if (AtmosphereApi.HasDeltas)
            {
                AtmosphereApi.AddDeltas(map, rect, deltaTemperature, deltaPressure, 0f, 0f, 0f, Vector2.zero);
                return;
            }

            // 没有批量 API 时退化为逐格，但限制数量避免卡顿
            int limit = Mathf.Min(count, 128);
            for (int i = 0; i < limit; i++)
            {
                Stabilize(region, cells[i]);
            }
        }

        /// <summary>逐格精确修正。</summary>
        private void Stabilize(IForceFieldRegion region, IntVec3 cell)
        {
            float deltaTemperature = 0f;
            float currentTemperature;
            if (AtmosphereApi.TryGetTemperature(map, cell, out currentTemperature))
            {
                deltaTemperature = (region.TargetTemperature - currentTemperature) * CellGain;
            }

            float deltaPressure = 0f;
            if (region.StabilizePressure)
            {
                float pascals;
                if (AtmosphereApi.TryGetPressurePascals(map, cell, out pascals))
                {
                    deltaPressure = (TargetPressurePascals - pascals)
                                    / AtmosphereApi.SolverPressureToPascals * CellGain;
                }
            }

            float deltaMoisture = 0f;
            if (region.StabilizeHumidity)
            {
                float humidity;
                if (AtmosphereApi.TryGetRelativeHumidity(map, cell, out humidity) && humidity > TargetRelativeHumidity)
                {
                    // 只抽湿不加湿；系数偏保守，具体手感需要进游戏实测调整
                    deltaMoisture = -(humidity - TargetRelativeHumidity) * 0.2f;
                }
            }

            Vector2 windDelta = Vector2.zero;
            if (region.KillWind)
            {
                Vector2 wind;
                if (AtmosphereApi.TryGetWind(map, cell, out wind))
                {
                    windDelta = -wind * 0.5f;
                }
            }

            bool any = Mathf.Abs(deltaTemperature) >= 0.005f
                       || Mathf.Abs(deltaPressure) >= 0.0005f
                       || Mathf.Abs(deltaMoisture) >= 0.0005f
                       || windDelta != Vector2.zero;
            if (!any)
            {
                return;
            }

            if (AtmosphereApi.HasDeltas)
            {
                AtmosphereApi.AddDeltas(map, cell, deltaTemperature, deltaPressure, 0f, deltaMoisture, 0f, windDelta);
                return;
            }

            if (Mathf.Abs(deltaTemperature) >= 0.005f)
            {
                AtmosphereApi.AddTemperatureDelta(map, cell, deltaTemperature);
            }

            if (Mathf.Abs(deltaPressure) >= 0.0005f)
            {
                AtmosphereApi.AddPressureDelta(map, cell, deltaPressure);
            }

            if (Mathf.Abs(deltaMoisture) >= 0.0005f)
            {
                AtmosphereApi.AddMoistureDelta(map, cell, deltaMoisture);
            }

            if (windDelta != Vector2.zero)
            {
                AtmosphereApi.AddWindImpulse(map, cell, windDelta);
            }
        }
    }
}
