using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    namespace UtilApi
    {
        /// <summary>
        /// 电网扣电时使用的储能分组。
        /// </summary>
        public enum OmniPowerBatteryDrainGroup
        {
            SmartInfiniteBattery = 0,
            MatterEnergyConverterBattery = 1,
            OmniPowerBattery = 2,
            OrdinaryBattery = 3
        }

        /// <summary>
        /// 单个储能分组的统计信息。
        /// </summary>
        public class OmniPowerStorageBucket
        {
            public OmniPowerBatteryDrainGroup Group;
            public int BatteryCount;
            public int AvailableBatteryCount;
            public int EmpStunnedBatteryCount;
            public float StoredEnergyWd;
            public float MaxStoredEnergyWd;
        }

        /// <summary>
        /// 当前电网储能和 Omni 发电机状态。
        /// </summary>
        public class OmniPowerNetStorageState
        {
            public bool HasPowerNet;
            public bool CanDeduct;
            public bool CanDeductFromStoredEnergy;
            public float RequestedEnergyWd;
            public float AvailableStoredEnergyWd;
            public float MissingStoredEnergyWd;
            public int TotalBatteryCount;
            public int ActiveOmniPowerGeneratorCount;
            public int ActiveInfiniteOmniPowerGeneratorCount;
            public bool HasActiveOmniPowerGenerator;
            public bool HasActiveInfiniteOmniPowerGenerator;
            public float ActiveOmniPowerGeneratorEnergyWdPerTick;
            public readonly List<OmniPowerStorageBucket> Buckets = new List<OmniPowerStorageBucket>(4);
        }

        /// <summary>
        /// 单个电池的实际扣电记录。
        /// </summary>
        public class OmniPowerDrainRecord
        {
            public OmniPowerBatteryDrainGroup Group;
            public CompPowerBattery Battery;
            public float DrawnEnergyWd;
        }

        /// <summary>
        /// 电网扣电结果。
        /// </summary>
        public class OmniPowerDrainResult
        {
            public bool Success;
            public float RequestedEnergyWd;
            public float DrawnFromBatteriesWd;
            public float CoveredByInfiniteGeneratorWd;
            public float RemainingEnergyWd;
            public OmniPowerNetStorageState StateBefore;
            public OmniPowerNetStorageState StateAfter;
            public readonly List<OmniPowerDrainRecord> Records = new List<OmniPowerDrainRecord>();
        }

        /// <summary>
        /// 对外开放的电网储能检测和扣电工具。
        /// </summary>
        public static class OmniPowerNetUtility
        {
            private const float Epsilon = 0.0001f;

            private static readonly OmniPowerBatteryDrainGroup[] DefaultDrainOrderInternal =
            {
                OmniPowerBatteryDrainGroup.SmartInfiniteBattery,
                OmniPowerBatteryDrainGroup.MatterEnergyConverterBattery,
                OmniPowerBatteryDrainGroup.OmniPowerBattery,
                OmniPowerBatteryDrainGroup.OrdinaryBattery
            };

            /// <summary>
            /// 返回默认扣电顺序：智能无限电池、物质能量转化电池、Omni 电池、普通电池。
            /// </summary>
            public static OmniPowerBatteryDrainGroup[] GetDefaultDrainOrder()
            {
                return (OmniPowerBatteryDrainGroup[])DefaultDrainOrderInternal.Clone();
            }

            /// <summary>
            /// 检测电网中是否有足够储能可扣，并返回详细状态。
            /// 如果 allowInfiniteOmniPowerGenerator 为 true，激活的 Infinite 模式 Omni 发电机会覆盖储能缺口。
            /// </summary>
            public static bool CanDeductFromPowerNet(
                PowerNet powerNet,
                float amountWd,
                out OmniPowerNetStorageState state,
                bool allowInfiniteOmniPowerGenerator = true)
            {
                state = GetPowerNetStorageState(powerNet, amountWd, allowInfiniteOmniPowerGenerator);
                return state.CanDeduct;
            }

            /// <summary>
            /// 获取电网储能和 Omni 发电机状态。
            /// </summary>
            public static OmniPowerNetStorageState GetPowerNetStorageState(
                PowerNet powerNet,
                float requestedEnergyWd = 0f,
                bool allowInfiniteOmniPowerGenerator = true)
            {
                if (requestedEnergyWd < 0f)
                {
                    requestedEnergyWd = 0f;
                }

                OmniPowerNetStorageState state = new OmniPowerNetStorageState
                {
                    HasPowerNet = powerNet != null,
                    RequestedEnergyWd = requestedEnergyWd
                };

                for (int i = 0; i < DefaultDrainOrderInternal.Length; i++)
                {
                    state.Buckets.Add(new OmniPowerStorageBucket
                    {
                        Group = DefaultDrainOrderInternal[i]
                    });
                }

                if (powerNet == null)
                {
                    state.MissingStoredEnergyWd = requestedEnergyWd;
                    state.CanDeduct = requestedEnergyWd <= Epsilon;
                    state.CanDeductFromStoredEnergy = state.CanDeduct;
                    return state;
                }

                CollectBatteryState(powerNet, state);
                CollectOmniGeneratorState(powerNet, state);

                state.CanDeductFromStoredEnergy = state.AvailableStoredEnergyWd + Epsilon >= requestedEnergyWd;
                state.MissingStoredEnergyWd = Mathf.Max(0f, requestedEnergyWd - state.AvailableStoredEnergyWd);
                state.CanDeduct = state.CanDeductFromStoredEnergy
                                  || requestedEnergyWd <= Epsilon
                                  || allowInfiniteOmniPowerGenerator && state.HasActiveInfiniteOmniPowerGenerator;

                return state;
            }

            /// <summary>
            /// 按指定顺序从电网扣除储能。
            /// 默认顺序为：智能无限电池、物质能量转化电池、Omni 电池、普通电池。
            /// 如果 allowInfiniteOmniPowerGenerator 为 true，激活的 Infinite 模式 Omni 发电机会覆盖扣电后的剩余缺口。
            /// </summary>
            public static bool TryDrainFromPowerNet(
                PowerNet powerNet,
                float amountWd,
                out OmniPowerDrainResult result,
                IList<OmniPowerBatteryDrainGroup> drainOrder = null,
                bool allowInfiniteOmniPowerGenerator = true)
            {
                if (amountWd < 0f)
                {
                    amountWd = 0f;
                }

                IList<OmniPowerBatteryDrainGroup> order = drainOrder ?? DefaultDrainOrderInternal;
                result = new OmniPowerDrainResult
                {
                    RequestedEnergyWd = amountWd,
                    RemainingEnergyWd = amountWd,
                    StateBefore = GetPowerNetStorageState(powerNet, amountWd, allowInfiniteOmniPowerGenerator)
                };

                if (amountWd <= Epsilon)
                {
                    result.Success = true;
                    result.RemainingEnergyWd = 0f;
                    result.StateAfter = result.StateBefore;
                    return true;
                }

                if (powerNet == null)
                {
                    result.StateAfter = result.StateBefore;
                    return false;
                }

                float availableInOrder = GetAvailableStoredEnergyForOrder(powerNet, order);
                bool canUseInfiniteGenerator = allowInfiniteOmniPowerGenerator
                                               && result.StateBefore.HasActiveInfiniteOmniPowerGenerator;

                if (availableInOrder + Epsilon < amountWd && !canUseInfiniteGenerator)
                {
                    result.StateAfter = result.StateBefore;
                    return false;
                }

                float remaining = amountWd;
                for (int i = 0; i < order.Count && remaining > Epsilon; i++)
                {
                    DrainGroup(powerNet, order[i], ref remaining, result);
                }

                if (remaining > Epsilon && canUseInfiniteGenerator)
                {
                    result.CoveredByInfiniteGeneratorWd = remaining;
                    remaining = 0f;
                }

                result.RemainingEnergyWd = Mathf.Max(0f, remaining);
                result.Success = result.RemainingEnergyWd <= Epsilon;
                result.StateAfter = GetPowerNetStorageState(powerNet, amountWd, allowInfiniteOmniPowerGenerator);
                return result.Success;
            }

            private static void CollectBatteryState(PowerNet powerNet, OmniPowerNetStorageState state)
            {
                List<CompPowerBattery> batteries = powerNet.batteryComps;
                if (batteries == null)
                {
                    return;
                }

                state.TotalBatteryCount = batteries.Count;

                for (int i = 0; i < batteries.Count; i++)
                {
                    CompPowerBattery battery = batteries[i];
                    if (battery == null)
                    {
                        continue;
                    }

                    OmniPowerStorageBucket bucket = GetBucket(state, GetBatteryGroup(battery));
                    bucket.BatteryCount++;

                    if (battery.StunnedByEMP)
                    {
                        bucket.EmpStunnedBatteryCount++;
                        continue;
                    }

                    float storedEnergy = GetAvailableStoredEnergy(battery);
                    if (storedEnergy > Epsilon)
                    {
                        bucket.AvailableBatteryCount++;
                    }

                    bucket.StoredEnergyWd = AddEnergy(bucket.StoredEnergyWd, storedEnergy);
                    bucket.MaxStoredEnergyWd = AddEnergy(bucket.MaxStoredEnergyWd, GetMaxStoredEnergy(battery));
                    state.AvailableStoredEnergyWd = AddEnergy(state.AvailableStoredEnergyWd, storedEnergy);
                }
            }

            private static void CollectOmniGeneratorState(PowerNet powerNet, OmniPowerNetStorageState state)
            {
                List<CompPowerTrader> powerComps = powerNet.powerComps;
                if (powerComps == null)
                {
                    return;
                }

                for (int i = 0; i < powerComps.Count; i++)
                {
                    CompOmniPowerGenerator generator = powerComps[i] as CompOmniPowerGenerator;
                    if (!IsActiveOmniGenerator(generator))
                    {
                        continue;
                    }

                    state.ActiveOmniPowerGeneratorCount++;
                    state.HasActiveOmniPowerGenerator = true;

                    if (generator.mode == OmniPowerMode.Infinite)
                    {
                        state.ActiveInfiniteOmniPowerGeneratorCount++;
                        state.HasActiveInfiniteOmniPowerGenerator = true;
                        state.ActiveOmniPowerGeneratorEnergyWdPerTick = float.PositiveInfinity;
                        continue;
                    }

                    if (!float.IsInfinity(state.ActiveOmniPowerGeneratorEnergyWdPerTick))
                    {
                        state.ActiveOmniPowerGeneratorEnergyWdPerTick = AddEnergy(
                            state.ActiveOmniPowerGeneratorEnergyWdPerTick,
                            Mathf.Max(0f, generator.EnergyOutputPerTick));
                    }
                }
            }

            private static float GetAvailableStoredEnergyForOrder(
                PowerNet powerNet,
                IList<OmniPowerBatteryDrainGroup> order)
            {
                if (powerNet?.batteryComps == null || order == null || order.Count == 0)
                {
                    return 0f;
                }

                int groupMask = 0;
                for (int i = 0; i < order.Count; i++)
                {
                    int groupValue = (int)order[i];
                    if (groupValue >= 0 && groupValue <= 3)
                    {
                        groupMask |= 1 << groupValue;
                    }
                }

                float available = 0f;
                List<CompPowerBattery> batteries = powerNet.batteryComps;
                for (int i = 0; i < batteries.Count; i++)
                {
                    CompPowerBattery battery = batteries[i];
                    if (battery == null || battery.StunnedByEMP)
                    {
                        continue;
                    }

                    OmniPowerBatteryDrainGroup group = GetBatteryGroup(battery);
                    if ((groupMask & (1 << (int)group)) == 0)
                    {
                        continue;
                    }

                    available = AddEnergy(available, GetAvailableStoredEnergy(battery));
                }

                return available;
            }

            private static void DrainGroup(
                PowerNet powerNet,
                OmniPowerBatteryDrainGroup group,
                ref float remaining,
                OmniPowerDrainResult result)
            {
                if (powerNet.batteryComps == null)
                {
                    return;
                }

                List<CompPowerBattery> batteries = powerNet.batteryComps;
                for (int i = 0; i < batteries.Count && remaining > Epsilon; i++)
                {
                    CompPowerBattery battery = batteries[i];
                    if (battery == null || battery.StunnedByEMP || GetBatteryGroup(battery) != group)
                    {
                        continue;
                    }

                    float available = GetAvailableStoredEnergy(battery);
                    float draw = Mathf.Min(available, remaining);
                    if (draw <= Epsilon)
                    {
                        continue;
                    }

                    battery.DrawPower(draw);
                    remaining -= draw;
                    result.DrawnFromBatteriesWd += draw;
                    result.Records.Add(new OmniPowerDrainRecord
                    {
                        Group = group,
                        Battery = battery,
                        DrawnEnergyWd = draw
                    });
                }
            }

            private static OmniPowerStorageBucket GetBucket(
                OmniPowerNetStorageState state,
                OmniPowerBatteryDrainGroup group)
            {
                for (int i = 0; i < state.Buckets.Count; i++)
                {
                    if (state.Buckets[i].Group == group)
                    {
                        return state.Buckets[i];
                    }
                }

                OmniPowerStorageBucket bucket = new OmniPowerStorageBucket { Group = group };
                state.Buckets.Add(bucket);
                return bucket;
            }

            private static OmniPowerBatteryDrainGroup GetBatteryGroup(CompPowerBattery battery)
            {
                if (battery is CompOmniCrafterSmartInfiniteBattery)
                {
                    return OmniPowerBatteryDrainGroup.SmartInfiniteBattery;
                }

                if (battery is CompMatterEnergyConverterBattery)
                {
                    return OmniPowerBatteryDrainGroup.MatterEnergyConverterBattery;
                }

                if (battery is CompOmniPowerBattery)
                {
                    return OmniPowerBatteryDrainGroup.OmniPowerBattery;
                }

                return OmniPowerBatteryDrainGroup.OrdinaryBattery;
            }

            private static bool IsActiveOmniGenerator(CompOmniPowerGenerator generator)
            {
                return generator != null
                       && generator.parent != null
                       && generator.parent.Spawned
                       && generator.PowerOn
                       && FlickUtility.WantsToBeOn(generator.parent)
                       && (generator.mode == OmniPowerMode.Infinite || generator.PowerOutput > Epsilon);
            }

            private static float GetAvailableStoredEnergy(CompPowerBattery battery)
            {
                float storedEnergy = battery.StoredEnergy;
                if (float.IsNaN(storedEnergy) || storedEnergy < 0f)
                {
                    return 0f;
                }

                return storedEnergy;
            }

            private static float GetMaxStoredEnergy(CompPowerBattery battery)
            {
                CompProperties_Battery props = battery.Props;
                if (props == null || float.IsNaN(props.storedEnergyMax) || props.storedEnergyMax < 0f)
                {
                    return 0f;
                }

                return props.storedEnergyMax;
            }

            private static float AddEnergy(float current, float value)
            {
                if (float.IsPositiveInfinity(current) || float.IsPositiveInfinity(value))
                {
                    return float.PositiveInfinity;
                }

                if (float.IsNaN(value) || value <= 0f)
                {
                    return current;
                }

                return current + value;
            }
        }
    }
}