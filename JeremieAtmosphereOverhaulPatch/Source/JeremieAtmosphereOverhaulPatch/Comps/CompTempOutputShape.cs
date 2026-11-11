using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Comps
{
    /// <summary>
    /// 温控建筑的出风口形状。
    /// 原版每个温控子类自己在 TickRare 里挑格子再 Room.Temperature += ；
    /// AO 接管后统一退化为「只写 parent.Position 一格」，多格建筑因此丢了一半出风口。
    /// 本组件把「挑哪些格、每格给多少」从子类手里收回来，写进 AO 连续源。
    /// </summary>
    public enum TempOutputShape
    {
        /// <summary>1×1：加热写自身，制冷写 pos+South（冷）与 pos+North（热）。</summary>
        Single = 0,

        /// <summary>2×1 加热器：加热 pos+North 与 pos+North+East。</summary>
        TwoCellHeater = 1,

        /// <summary>2×1 制冷机：冷侧 pos+South / pos+South+East，热侧 pos+North / pos+North+East。</summary>
        TwoCellCooler = 2,

        /// <summary>贴墙 1×1：按 wallOffsetNorth 旋转后写单格。VTE 的挂壁加热器靠扩展把偏移设成零。</summary>
        WallOffset = 3,
    }

    public class CompProperties_TempOutputShape : CompProperties
    {
        public TempOutputShape shape = TempOutputShape.Single;

        /// <summary>与 AO_FacilityExtension.heatFactor 默认值一致；一般不要在 XML 里改。</summary>
        public float heatFactor = 1f;

        /// <summary>与 AO_FacilityExtension.outletToleranceK 默认值一致。</summary>
        public float outletToleranceK = 2f;

        /// <summary>制冷时排到热侧的比例，对齐 VTE Building_TwoCellCooler 的 1.25 倍。</summary>
        public float hotReturnFactor = 1.25f;

        /// <summary>WallOffset 形状在建筑朝北时的出风偏移。</summary>
        public IntVec3 wallOffsetNorth = IntVec3.North;

        public CompProperties_TempOutputShape()
        {
            compClass = typeof(CompTempOutputShape);
        }
    }

    /// <summary>
    /// 把温控建筑的输出改写为 AO 连续源。
    ///
    /// 调用链：AO 的 TempControlSubclassPatches 给每个子类的 TickRare 打 Prefix，
    /// Prefix 调 FacilityPatchUtility.RunTempControl；本 mod 在 RunTempControl 上再打 Prefix，
    /// 用自己的格子布局写槽位后令 __result = false（AO 语义：已被接管），
    /// 于是 AO 原实现被跳过，而子类原方法仍被 RoomTemperatureWriteHook 抑制。
    /// 全程只有一条热源通道，不会叠加。
    /// </summary>
    public class CompTempOutputShape : ThingComp
    {
        private readonly List<IntVec3> _coldCells = new List<IntVec3>(4);
        private readonly List<IntVec3> _hotCells = new List<IntVec3>(4);

        private static Type _vteExtType;
        private static FieldInfo _vteOffsetField;
        private static bool _vteResolved;

        public CompProperties_TempOutputShape Props
        {
            get { return (CompProperties_TempOutputShape)props; }
        }

        /// <summary>
        /// 尝试接管一次温控 tick。返回 true 表示已写入 AO 连续源，调用方应跳过 AO 原实现。
        /// 返回 false 时调用方必须回退，不能静默吞掉热量。
        /// </summary>
        public bool Apply()
        {
            Building_TempControl building = parent as Building_TempControl;
            if (building == null)
            {
                return false;
            }
            if (!AoBridge.Available)
            {
                return false;
            }

            CompTempControl control = building.compTempControl;
            if (control == null || control.Props == null)
            {
                return false;
            }

            object slots = AoBridge.GetSlots(building);
            if (slots == null)
            {
                // 建筑没被 AO 注入温控组件：没有槽位可写，交回 AO 自己的路径。
                return false;
            }

            float heatFactor = AoBridge.HeatFactor(building.def, Props.heatFactor);
            float tolerance = AoBridge.OutletTolerance(building.def, Props.outletToleranceK);
            float fullRate = Mathf.Abs(control.Props.energyPerSecond) * heatFactor / 60f;
            if (fullRate <= 0f)
            {
                return false;
            }

            float drive;
            if (control.Props.energyPerSecond < 0f)
            {
                drive = ApplyCooler(building, control, slots, fullRate, tolerance);
            }
            else
            {
                drive = ApplyHeater(building, control, slots, fullRate, tolerance);
            }

            if (drive < 0f)
            {
                // 没有任何输出格能读到 AO 温度场：本图没有 AO 引擎，或输出格全部越界。
                // 交回 AO / 原版实现，不能静默吞掉热量。
                return false;
            }

            // 与 AO Run 同语义：有驱动量就算高功率档，不是等达到满额。
            control.operatingAtHighPower = drive > 0f;

            CompPowerTrader power = building.compPowerTrader;
            if (power != null && power.PowerOn)
            {
                power.PowerOutput = drive > 0f
                    ? -power.Props.PowerConsumption
                    : -power.Props.PowerConsumption * control.Props.lowPowerConsumptionFactor;
            }
            return true;
        }

        // ---------------------------------------------------------------- 加热

        /// <summary>返回本 tick 的最大驱动量 0..1，用于电源档位与高功率标记。</summary>
        private float ApplyHeater(
            Building_TempControl building,
            CompTempControl control,
            object slots,
            float fullRate,
            float tolerance)
        {
            CollectHeaterCells(building);

            int count = _coldCells.Count;
            if (count == 0)
            {
                AoBridge.ZeroFrom(slots, parent, 0);
                return -1f;
            }

            Map map = building.Map;
            float aim = control.TargetTemperature + tolerance;
            float perCell = fullRate / count;
            float maxDrive = 0f;
            int written = 0;

            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = _coldCells[i];
                float temperature;
                if (!AoBridge.TryGetTemperature(map, cell, out temperature))
                {
                    continue;
                }

                float drive = Mathf.Clamp01((aim - temperature) / 1f);
                float efficiency = temperature <= 200f
                    ? 1f
                    : Mathf.Clamp01((300f - temperature) / 100f);

                if (drive > maxDrive)
                {
                    maxDrive = drive;
                }

                SlotRates rates = new SlotRates();
                rates.TemperaturePerTick = drive * efficiency * perCell;
                AoBridge.SetSlot(slots, parent, written, cell, rates);
                written++;
            }

            AoBridge.ZeroFrom(slots, parent, written);
            if (written == 0)
            {
                // 所有输出格都读不到 AO 温度：本图没有 AO 引擎。
                return -1f;
            }
            return maxDrive;
        }

        // ---------------------------------------------------------------- 制冷

        /// <summary>返回本 tick 的最大驱动量 0..1。</summary>
        private float ApplyCooler(
            Building_TempControl building,
            CompTempControl control,
            object slots,
            float fullRate,
            float tolerance)
        {
            CollectCoolerCells(building);

            int coldCount = _coldCells.Count;
            int hotCount = _hotCells.Count;
            if (coldCount == 0)
            {
                AoBridge.ZeroFrom(slots, parent, 0);
                return -1f;
            }

            Map map = building.Map;

            float coldSum = 0f;
            int coldSamples = 0;
            for (int i = 0; i < coldCount; i++)
            {
                float t;
                if (AoBridge.TryGetTemperature(map, _coldCells[i], out t))
                {
                    coldSum += t;
                    coldSamples++;
                }
            }
            if (coldSamples == 0)
            {
                AoBridge.ZeroFrom(slots, parent, 0);
                return -1f;
            }
            float coldAvg = coldSum / coldSamples;

            float hotAvg = coldAvg;
            if (hotCount > 0)
            {
                float hotSum = 0f;
                int hotSamples = 0;
                for (int i = 0; i < hotCount; i++)
                {
                    float t;
                    if (AoBridge.TryGetTemperature(map, _hotCells[i], out t))
                    {
                        hotSum += t;
                        hotSamples++;
                    }
                }
                if (hotSamples > 0)
                {
                    hotAvg = hotSum / hotSamples;
                }
            }

            // 与 AO RunCooler 同构：热侧越难散热、冷侧越接近绝对零度，效率越低。
            float efficiency = 1f / (1f + Mathf.Max(0f, Mathf.Max(hotAvg - coldAvg, hotAvg - 40f)) / 65f);
            efficiency *= Mathf.Clamp01((coldAvg + 273.15f) / 150f);

            float aim = control.TargetTemperature - tolerance;
            float drive = Mathf.Clamp01((coldAvg - aim) / 1f);
            if (drive <= 0f)
            {
                AoBridge.ZeroFrom(slots, parent, 0);
                return 0f;
            }

            float coldPerCell = -drive * efficiency * fullRate / coldCount;
            float hotPerCell = hotCount > 0
                ? drive * efficiency * fullRate * Props.hotReturnFactor / hotCount
                : 0f;

            int written = 0;
            for (int i = 0; i < coldCount; i++)
            {
                SlotRates rates = new SlotRates();
                rates.TemperaturePerTick = coldPerCell;
                AoBridge.SetSlot(slots, parent, written, _coldCells[i], rates);
                written++;
            }
            for (int i = 0; i < hotCount; i++)
            {
                SlotRates rates = new SlotRates();
                rates.TemperaturePerTick = hotPerCell;
                AoBridge.SetSlot(slots, parent, written, _hotCells[i], rates);
                written++;
            }

            AoBridge.ZeroFrom(slots, parent, written);
            return drive;
        }

        // ---------------------------------------------------------------- 格子布局

        private void CollectHeaterCells(Building_TempControl building)
        {
            _coldCells.Clear();
            IntVec3 position = building.Position;

            switch (Props.shape)
            {
                case TempOutputShape.TwoCellHeater:
                {
                    IntVec3 north = Rotated(IntVec3.North, building.Rotation);
                    IntVec3 east = Rotated(IntVec3.East, building.Rotation);
                    _coldCells.Add(position + north);
                    _coldCells.Add(position + north + east);
                    break;
                }
                case TempOutputShape.WallOffset:
                {
                    IntVec3 offset = Rotated(ReadWallOffsetNorth(building), building.Rotation);
                    _coldCells.Add(position + offset);
                    break;
                }
                default:
                {
                    _coldCells.Add(position);
                    break;
                }
            }
        }

        private void CollectCoolerCells(Building_TempControl building)
        {
            _coldCells.Clear();
            _hotCells.Clear();
            IntVec3 position = building.Position;

            switch (Props.shape)
            {
                case TempOutputShape.TwoCellCooler:
                {
                    IntVec3 north = Rotated(IntVec3.North, building.Rotation);
                    IntVec3 south = Rotated(IntVec3.South, building.Rotation);
                    IntVec3 east = Rotated(IntVec3.East, building.Rotation);
                    _coldCells.Add(position + south);
                    _coldCells.Add(position + south + east);
                    _hotCells.Add(position + north);
                    _hotCells.Add(position + north + east);
                    break;
                }
                default:
                {
                    _coldCells.Add(position + Rotated(IntVec3.South, building.Rotation));
                    _hotCells.Add(position + Rotated(IntVec3.North, building.Rotation));
                    break;
                }
            }
        }

        private static IntVec3 Rotated(IntVec3 offset, Rot4 rotation)
        {
            return IntVec3Utility.RotatedBy(offset, rotation);
        }

        /// <summary>
        /// 读 VTE 的 TemperatureOutputPositionModExtension.offsetNorth。
        /// 反射而非直接引用，宿主工程因此不需要引用 VTE 程序集。
        /// </summary>
        private static IntVec3 ReadWallOffsetNorth(Building_TempControl building)
        {
            if (!_vteResolved)
            {
                _vteResolved = true;
                _vteExtType = AccessTools.TypeByName(
                    "VanillaTemperatureExpanded.TemperatureOutputPositionModExtension");
                if (_vteExtType != null)
                {
                    _vteOffsetField = AccessTools.Field(_vteExtType, "offsetNorth");
                }
            }

            if (_vteExtType == null || _vteOffsetField == null || building.def == null)
            {
                return IntVec3.North;
            }

            List<DefModExtension> extensions = building.def.modExtensions;
            if (extensions == null)
            {
                return IntVec3.North;
            }

            for (int i = 0; i < extensions.Count; i++)
            {
                DefModExtension extension = extensions[i];
                if (extension != null && extension.GetType() == _vteExtType)
                {
                    object value = _vteOffsetField.GetValue(extension);
                    if (value is IntVec3)
                    {
                        return (IntVec3)value;
                    }
                }
            }
            return IntVec3.North;
        }
    }
}
