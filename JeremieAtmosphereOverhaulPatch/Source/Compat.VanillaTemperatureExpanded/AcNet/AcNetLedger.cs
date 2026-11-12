using System;
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
            public float Drive;   // 负荷率 |Qi|/QFull，与温差相关
        }

        private readonly List<UnitEntry> _entries = new List<UnitEntry>(8);
        private readonly List<CompResourceTrader> _compressors = new List<CompResourceTrader>(4);

        /// <summary>出风格诊断：记录已打印过的机器，每台只打一次。</summary>
        private static readonly HashSet<int> _outputLoggedIds = new HashSet<int>();

        public int LastRunTick = -99999;
        public float LastEfficiency = 1f;

        // 最近一次账本的只读快照，供 inspect 面板使用（单位：台当量 = 单台满额室内机的输出）。
        public int UnitCount { get; private set; }
        public int CompressorCount { get; private set; }
        public float UnitReference { get; private set; }
        public float LastDemand { get; private set; }
        public float LastCapacity { get; private set; }
        public float LastRecovered { get; private set; }
        public float LastNet { get; private set; }
        public float LastCompressorUnits { get; private set; }
        /// <summary>室外机实际出力比例（= 电费依据），0..1。</summary>
        public float LastLoadRatio { get; private set; }

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
            UnitCount = 0;
            CompressorCount = 0;
            UnitReference = 0f;
            LastDemand = 0f;
            LastCapacity = 0f;
            LastRecovered = 0f;
            LastNet = 0f;
            LastCompressorUnits = 0f;
            LastLoadRatio = 0f;

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
                RefreshControllerIcons(net, true);
                return;
            }

            // 控制器的 ResourceOn 默认是 false（只有从存档加载时 Scribe 缺省才为 true），
            // 而 PipeSystemTick 已被本 mod 短路，再没有别人会把它打开。
            // 控制器不参与账本的开关机，只要 CanBeOn 就保持开启，否则 inspect 会一直显示
            // 「缺少压缩机 / 缺少中控器」，且 ShutDownAll 之后永远不会恢复。
            controller.resourceComp.ResourceOn = true;

            float networkTarget = controller.TargetNetworkTemperature;

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

                // 逐台读自己的目标温度，而不是网络统一值。
                // 非独立机在控制器调温时已被广播写入 targetTemperature，
                // 独立机（CompAcTempControl.IndependentTemp）保留玩家单独设的值。
                // 用统一值会让「一台制热、一台制冷」被强行掰成同一方向，
                // 于是 a 或 b 恒为 0：网内回收始终 0，且只有一种方向能工作。
                float target = control.TargetTemperature;
                if (target <= -99998f)
                {
                    target = networkTarget;
                }

                IntVec3 cell = OutputCell(unit);
                // AO 的连续源允许写在任意地图内格；建筑自身格也可写，不能再按 Impassable 拦。
                if (!cell.InBounds(map))
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

                // 反馈用房间平均温度，而不是出风格本身的温度。
                // 出风格就是我们刚写入热量的那一格，几秒内就会被抬到目标附近，
                // 于是 |target - cell| 掉到 1% 左右、房间却升不上去；改成房间平均后
                // drive 会稳定在「离目标还有几度」的真实差距上。
                // AO 把 RoomTempTracker.Temperature 的 getter 换成了 GetAverageAuthoritativeTemperature，
                // 所以 room.Temperature 读到的就是 AO 的权威房间温度。
                float feedback = cellTemperature;
                Room room = RegionAndRoomQuery.RoomAt(cell, map);
                if (room != null && !room.UsesOutdoorTemperature)
                {
                    feedback = room.Temperature;
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

                float drive = Mathf.Clamp01(Mathf.Abs(target - feedback) / 1f);
                // 与原版同符号：eps 为负，目标低于室温时乘 +1 得到负值（制冷），
                // 目标高于室温时乘 -1 得到正值（制热），于是 ACUnit 天然是双向机。
                float qi = eps * (dtTicks / 60f)
                           * (target < feedback ? 1f : -1f)
                           * drive * heatFactor;

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
                entry.Drive = drive;
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

            // inspect 面板快照：统一换算成「台当量」，即单台满额室内机的输出。
            float unitRef = qRef > 0f ? qRef : 0f;
            UnitReference = unitRef;
            UnitCount = _entries.Count;
            CompressorCount = _compressors.Count;
            LastDemand = a + b;
            LastCapacity = capacity;
            LastRecovered = Mathf.Min(a, b);
            LastNet = qNet;
            float unitsSum = 0f;
            for (int i = 0; i < _compressors.Count; i++)
            {
                unitsSum += -_compressors[i].Props.consumptionPerTick;
            }
            LastCompressorUnits = unitsSum;
            LastLoadRatio = loadRatio;

            // VTE 的 UpdateOverlayHandle 在 ResourceOn 时直接 return，而我们让控制器常开，
            // 所以必须在这里自己刷新「缺少室外机」图标，否则它一旦亮过就永久卡住。
            RefreshControllerIcons(net, s <= 0f);

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

        /// <summary>刷新所有中控器的错误图标；missingCompressors 为 true 时显示「缺少室外机」。</summary>
        private static void RefreshControllerIcons(AcPipeNet net, bool missingCompressors)
        {
            List<Building_AcControlUnit> controllers = net.ControllerList;
            for (int i = 0; i < controllers.Count; i++)
            {
                AcNetOverlay.Refresh(controllers[i], missingCompressors);
            }
        }

        /// <summary>查询某台室内机最近一步的负荷率，供 inspect 面板使用。</summary>
        public bool TryGetUnitDrive(Building_AcUnit unit, out float drive)
        {
            drive = 0f;
            if (unit == null)
            {
                return false;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Unit, unit))
                {
                    drive = _entries[i].Drive;
                    return true;
                }
            }

            return false;
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

        /// <summary>
        /// 实际出风格（按建筑的空间形态，而不是 offsetNorth）：
        /// - 壁挂机（isAttachment / Placeworker_AttachedToWall，VTE_ACUnitWall）是室内的贴墙附件，
        ///   本体所在格就在室内，作用格就是自身格；
        /// - 墙体机（canPlaceOverWall，VTE_ACUnit）本身就是墙，占据墙格，
        ///   作用格是贴图所面对的房间那一格 = Position + RotatedBy(North, Rotation)。
        /// </summary>
        private static IntVec3 OutputCell(Building_AcUnit unit)
        {
            bool attached = IsAttachedToWall(unit);
            IntVec3 cell = attached
                ? unit.Position
                : unit.Position + IntVec3Utility.RotatedBy(IntVec3.North, unit.Rotation);

            if (_outputLoggedIds.Count < 16 && _outputLoggedIds.Add(unit.thingIDNumber))
            {
                Log.Message("[AOPatch/VTE] 出风格：def=" + unit.def.defName
                            + " pos=" + unit.Position
                            + " rot=" + unit.Rotation.AsInt
                            + " 贴墙=" + attached
                            + " 采用=" + cell);
            }

            return cell;
        }

        /// <summary>壁挂判定：placeWorkers 含 Placeworker_AttachedToWall，或 Def 标了 isAttachment。</summary>
        private static bool IsAttachedToWall(Building_AcUnit unit)
        {
            ThingDef def = unit.def;
            // BuildableDef.placeWorkers 是 List<System.Type>，不是实例列表。
            List<Type> workerTypes = def.placeWorkers;
            if (workerTypes != null)
            {
                for (int i = 0; i < workerTypes.Count; i++)
                {
                    if (workerTypes[i] == typeof(Placeworker_AttachedToWall))
                    {
                        return true;
                    }
                }
            }
            return def.building != null && def.building.isAttachment;
        }
    }
}
