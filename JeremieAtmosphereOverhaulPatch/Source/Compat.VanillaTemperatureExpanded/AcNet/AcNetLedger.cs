using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using PipeSystem;
using RimWorld;
using UnityEngine;
using VanillaTemperatureExpanded;
using VanillaTemperatureExpanded.Buildings;
using VanillaTemperatureExpanded.Comps;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded.AcNet
{
    /// <summary>
    /// 一个 AcPipeNet 的模拟步账本（随 AO 的模拟步推进，默认 30 tick 一步）。
    ///
    /// VTE 原本是「每台室内机各自和房间换热」的单机模型，冷凝热凭空消失；
    /// 这里把它重构成三管式 VRF：先汇总整张网络的冷凝侧 a 与蒸发侧 b，
    /// 网内对冲 Q_rec = min(a,b)，只有净差 Q_signed = a-b 需要室外机承担。
    /// 满足率 s = min(1, C/|Q_signed|)，所有室内机同比例削减，室外机只处理净差。
    ///
    /// 接管点：
    /// - Building_AcUnit.TickRare 被 Prefix 短路，由本账本写入 AO 连续源；
    /// - AcPipeNet.PipeSystemTick 被 Prefix 短路，ResourceOn / Efficiency 全由本账本决定；
    /// - AO 的 CompTempControlAtmosphere.CompTickRare 对 VTE 空调被 Prefix 短路，避免覆盖槽位。
    /// </summary>
    internal sealed class AcNetLedger
    {
        /// <summary>参考周期（250 tick），只用于把「可支持几台室内机」换算成热容量。</summary>
        public const int ReferenceIntervalTicks = 250;

        /// <summary>低于此值的净差视为完全平衡。</summary>
        private const float MinNetThreshold = 0.01f;

        /// <summary>VTE 原版的高温停机阈值。</summary>
        private const float MaxOutletCelsius = 120f;

        /// <summary>没有室内机时用于把压缩机台数换算成热容量的默认参考值。</summary>
        private const float DefaultRefCapacity = 87.5f;

        private struct UnitEntry
        {
            public Building_AcUnit Unit;
            public CompResourceTrader Trader;
            public CompPowerTrader Power;
            public IntVec3 Cell;
            public float Qi;      // 带符号的需求，K·格 / 本次模拟步
            public float QFull;   // 满额 |Qi|，K·格 / 本次模拟步
        }

        private readonly List<UnitEntry> _entries = new List<UnitEntry>(8);
        private readonly List<CompResourceTrader> _compressors = new List<CompResourceTrader>(4);

        private static FieldInfo _outputOffsetField;
        private static bool _offsetResolved;

        public int LastRunTick = -99999;
        public float LastEfficiency = 1f;

        /// <summary>
        /// 跑一次账本。由 AO 模拟步回调调用，dtTicks 是本次模拟实际推进的 tick 数
        /// （AO 未跑完时会累加成 12/18/24/30，不能假定固定值）。
        /// </summary>
        public void Run(AcPipeNet net, int dtTicks)
        {
            if (dtTicks <= 0)
            {
                return;
            }

            int tick = Find.TickManager.TicksGame;
            LastRunTick = tick;
            _entries.Clear();
            _compressors.Clear();

            Map map = net.map;
            if (map == null)
            {
                return;
            }

            // 1. 控制器：至少一个可运行即可，多控制器不区分。
            Building_AcControlUnit controller = FindController(net);
            if (controller == null)
            {
                ShutDownAll(net);
                net.Efficiency = 0f;
                LastEfficiency = 0f;
                return;
            }

            float target = controller.TargetNetworkTemperature;

            // 2. 收集室内机需求。反向（制热）与正向（制冷）在同一个循环里分开累加。
            float a = 0f;
            float b = 0f;
            float qRef = 0f;

            List<CompResourceTrader> receivers = net.receivers;
            for (int i = 0; i < receivers.Count; i++)
            {
                CompResourceTrader trader = receivers[i];
                CompResourceTrader_AC ac = trader as CompResourceTrader_AC;
                if (ac == null)
                {
                    continue;
                }

                Building_AcUnit unit = trader.parent as Building_AcUnit;
                if (unit == null || !unit.Spawned)
                {
                    continue;
                }

                CompPowerTrader power = unit.compPowerTrader;
                bool canRun = trader.CanBeOn() && power != null && power.PowerOn;
                trader.ResourceOn = canRun;
                if (!canRun)
                {
                    continue;
                }

                CompTempControl control = unit.compTempControl;
                if (control == null || control.Props == null)
                {
                    continue;
                }

                IntVec3 cell = OutputCell(unit);
                if (!cell.InBounds(map) || GenGrid.Impassable(cell, map))
                {
                    trader.ResourceOn = false;
                    continue;
                }

                float cellTemperature;
                if (!AoBridge.TryGetTemperature(map, cell, out cellTemperature))
                {
                    trader.ResourceOn = false;
                    continue;
                }

                float eps = control.Props.energyPerSecond;
                if (eps == 0f)
                {
                    continue;
                }

                float heatFactor = AoBridge.HeatFactor(unit.def, 1f);
                float qFull = Mathf.Abs(eps) * (dtTicks / 60f) * heatFactor;
                if (qFull <= 0f)
                {
                    continue;
                }
                qRef = qFull;

                float drive = Mathf.Clamp01(Mathf.Abs(target - cellTemperature) / 1f);
                // 与原版同符号：eps 为负，目标低于格温时乘 +1 得到负值（制冷），
                // 目标高于格温时乘 -1 得到正值（制热），于是 ACUnit 天然是双向机。
                float qi = eps * (dtTicks / 60f)
                           * (target < cellTemperature ? 1f : -1f)
                           * drive * heatFactor;

                if (cellTemperature > MaxOutletCelsius)
                {
                    qi = 0f;
                }

                if (qi > 0f)
                {
                    a += qi;
                }
                else
                {
                    b += -qi;
                }

                UnitEntry entry = new UnitEntry();
                entry.Unit = unit;
                entry.Trader = trader;
                entry.Power = power;
                entry.Cell = cell;
                entry.Qi = qi;
                entry.QFull = qFull;
                _entries.Add(entry);
            }

            // 3. 压缩机容量：VTE 的 2/6 是「可支持几台满额室内机」的台数单位，
            //    必须乘 Q_ref 才能和本次模拟步的 K·格 净差比较。
            float refCapacity = qRef > 0f ? qRef : DefaultRefCapacity * (dtTicks / (float)ReferenceIntervalTicks);
            float capacity = 0f;
            List<CompResourceTrader> producers = net.producers;
            for (int i = 0; i < producers.Count; i++)
            {
                CompResourceTrader trader = producers[i];
                if (!(trader is CompResourceTrader_Compressor))
                {
                    continue;
                }

                if (!trader.CanBeOn())
                {
                    trader.ResourceOn = false;
                    continue;
                }

                float units = -trader.Props.consumptionPerTick;
                if (units <= 0f)
                {
                    continue;
                }

                trader.ResourceOn = true;
                capacity += units * refCapacity;
                _compressors.Add(trader);
            }

            // 4. 网络：净差与满足率。
            float qSigned = a - b;
            float qNet = Mathf.Abs(qSigned);
            if (qNet < MinNetThreshold)
            {
                qSigned = 0f;
                qNet = 0f;
            }

            float s;
            if (qNet == 0f)
            {
                s = 1f;
            }
            else if (capacity <= 0f)
            {
                // 没有可用压缩机：强制关断，室内机仍按需求耗电（见第 6 步）。
                s = 0f;
                for (int i = 0; i < _entries.Count; i++)
                {
                    _entries[i].Trader.ResourceOn = false;
                }
            }
            else
            {
                s = Mathf.Min(1f, capacity / qNet);
            }

            // 5. 执行：每台室内机按同比例 s 写 AO 连续源。
            for (int i = 0; i < _entries.Count; i++)
            {
                UnitEntry entry = _entries[i];
                object slots = AoBridge.GetSlots(entry.Unit);
                if (slots == null)
                {
                    continue;
                }

                float qOut = entry.Qi * s;
                if (qOut == 0f || entry.Trader.ResourceOn == false)
                {
                    AoBridge.ZeroFrom(slots, entry.Unit, 0);
                    continue;
                }

                IntVec3 direction = entry.Cell - entry.Unit.Position;
                SlotRates rates = new SlotRates();
                rates.TemperaturePerTick = qOut / dtTicks;
                rates.MaintainTargetWind = true;
                rates.TargetWind = new Vector2(direction.x, direction.z)
                                   * AoBridge.OutletWindSpeed(entry.Unit.def, 15f);
                rates.TargetWindBlendPerTick = 0.1f;
                AoBridge.SetSlot(slots, entry.Unit, 0, entry.Cell, rates);
            }

            // 6. 室外机：净制热从室外吸热（负值），净制冷向室外排热（正值）。
            if (qNet > 0f && _compressors.Count > 0)
            {
                float push = -qSigned * s / _compressors.Count;
                for (int i = 0; i < _compressors.Count; i++)
                {
                    Thing parent = _compressors[i].parent;
                    if (parent == null || !parent.Spawned)
                    {
                        continue;
                    }

                    // 制热时室外机是蒸发器，要从周围空气抽热。若不加限制，大负荷会把
                    // 压缩机周围的五格一路抽到 -200℃，AO 的水汽随之凝结成雾，
                    // 表现为「间歇喷出低温气体」。这里按每步最多降温 8℃、绝对下限 -30℃ 限幅。
                    float unitPush = push;
                    if (unitPush < 0f)
                    {
                        float cellTemperature;
                        if (AoBridge.TryGetTemperature(map, parent.Position, out cellTemperature))
                        {
                            float outdoor = map.mapTemperature.OutdoorTemp;
                            float baseline = cellTemperature < outdoor ? cellTemperature : outdoor;
                            // 8℃ / 250 tick 的降温速率上限，按本次 dt 折算。
                            float maxDrop = 8f * (dtTicks / (float)ReferenceIntervalTicks);
                            float floor = Mathf.Max(-30f, baseline - maxDrop);
                            float allowed = 5f * (floor - cellTemperature);
                            if (unitPush < allowed)
                            {
                                unitPush = allowed;
                            }
                            if (unitPush > 0f)
                            {
                                unitPush = 0f;
                            }
                        }
                    }

                    if (unitPush != 0f)
                    {
                        AoBridge.PushHeat(map, parent.Position, unitPush);
                    }
                }
            }

            // 7. 电费：室内机按需求热负荷计，室外机按实际承担比例计。
            for (int i = 0; i < _entries.Count; i++)
            {
                UnitEntry entry = _entries[i];
                if (entry.Power == null)
                {
                    continue;
                }

                float ratio = entry.QFull > 0f ? Mathf.Abs(entry.Qi) / entry.QFull : 0f;
                entry.Power.PowerOutput = -entry.Power.Props.PowerConsumption * ratio;
            }

            float loadRatio = capacity > 0f ? Mathf.Min(1f, qNet / capacity) : 0f;
            for (int i = 0; i < _compressors.Count; i++)
            {
                Thing parent = _compressors[i].parent;
                CompPowerTrader power = parent != null ? parent.TryGetComp<CompPowerTrader>() : null;
                if (power != null)
                {
                    power.PowerOutput = -power.Props.PowerConsumption * loadRatio;
                }
            }

            net.Efficiency = s;
            LastEfficiency = s;

            // 临时诊断：每 2000 tick 打一行，确认账本确实跑到了、且各个量不是 0。
            if (tick % 2000 < 30)
            {
                string first = "-";
                if (_entries.Count > 0)
                {
                    UnitEntry e = _entries[0];
                    first = string.Format("qi={0:F2} qFull={1:F2} cell={2}", e.Qi, e.QFull, e.Cell);
                }
                Log.Message(string.Format(
                    "[AOPatch/VTE] 账本 net={0} 室内机={1} 压缩机={2} receivers={3} producers={4} a={5:F2} b={6:F2} cap={7:F1} s={8:F3} 首台[{9}]",
                    net.GetHashCode(), _entries.Count, _compressors.Count, net.receivers.Count, net.producers.Count,
                    a, b, capacity, s, first));
            }
        }

        private static Building_AcControlUnit FindController(AcPipeNet net)
        {
            List<Building_AcControlUnit> controllers = net.ControllerList;
            for (int i = 0; i < controllers.Count; i++)
            {
                Building_AcControlUnit controller = controllers[i];
                if (controller != null && controller.Spawned
                    && controller.resourceComp != null && controller.resourceComp.CanBeOn())
                {
                    return controller;
                }
            }
            return null;
        }

        private static void ShutDownAll(AcPipeNet net)
        {
            List<CompResourceTrader> receivers = net.receivers;
            for (int i = 0; i < receivers.Count; i++)
            {
                CompResourceTrader trader = receivers[i];
                if (trader == null)
                {
                    continue;
                }

                trader.ResourceOn = false;
                if (trader.parent != null)
                {
                    object slots = AoBridge.GetSlots(trader.parent);
                    if (slots != null)
                    {
                        AoBridge.ZeroFrom(slots, trader.parent, 0);
                    }
                }
            }

            List<CompResourceTrader> producers = net.producers;
            for (int i = 0; i < producers.Count; i++)
            {
                if (producers[i] != null)
                {
                    producers[i].ResourceOn = false;
                }
            }
        }

        /// <summary>墙机出风格诊断：每局只打印一次，便于确认 Rotation 与 offset 的实际关系。</summary>
        private static bool _wallCellLogged;

        /// <summary>
        /// 实际出风格。
        ///
        /// 普通壁挂机（VTE_ACUnit）的 offset 是 North，取「机身正前方」。
        /// 墙机（VTE_ACUnitWall）的 offset 是 (0,0,0)：原版靠 GetRoom(自身格) 把温度摊到整个
        /// 房间，写哪一格都无所谓；AO 是格级模拟，必须落在房间那一侧。
        /// Placeworker_AttachedToWall 要求 loc + CardinalDirections[rot] 是 FillCategory.Full
        /// 的墙，也就是 Rotation 指向身后的墙，所以正面是 RotatedBy(South, Rotation)。
        /// </summary>
        private static IntVec3 OutputCell(Building_AcUnit unit)
        {
            IntVec3 offset = OutputOffset(unit);
            Map map = unit.Map;
            IntVec3 cell = unit.Position + IntVec3Utility.RotatedBy(offset, unit.Rotation);

            if (offset != IntVec3.Zero)
            {
                return cell;
            }

            IntVec3 front = unit.Position + IntVec3Utility.RotatedBy(IntVec3.South, unit.Rotation);
            IntVec3 result = front.InBounds(map) && !GenGrid.Impassable(front, map) ? front : cell;

            if (!_wallCellLogged)
            {
                _wallCellLogged = true;
                Log.Message("[AOPatch/VTE] 墙机出风格诊断：pos=" + unit.Position
                            + " rot=" + unit.Rotation.AsInt
                            + " offset=" + offset
                            + " 自身格=" + cell
                            + " 正面候选=" + front
                            + " 采用=" + result
                            + " def=" + unit.def.defName);
            }

            return result;
        }

        /// <summary>
        /// 出风格偏移。Building_AcUnit.outputOffsetNorth 是 private，
        /// 但同一个值就存在 Def 的 TemperatureOutputPositionModExtension 上，直接读扩展即可。
        /// </summary>
        private static IntVec3 OutputOffset(Building_AcUnit unit)
        {
            TemperatureOutputPositionModExtension extension =
                unit.def.GetModExtension<TemperatureOutputPositionModExtension>();
            if (extension != null)
            {
                return extension.offsetNorth;
            }

            if (!_offsetResolved)
            {
                _offsetResolved = true;
                _outputOffsetField = AccessTools.Field(typeof(Building_AcUnit), "outputOffsetNorth");
            }

            if (_outputOffsetField != null)
            {
                object value = _outputOffsetField.GetValue(unit);
                if (value is IntVec3)
                {
                    return (IntVec3)value;
                }
            }
            return IntVec3.North;
        }
    }
}
