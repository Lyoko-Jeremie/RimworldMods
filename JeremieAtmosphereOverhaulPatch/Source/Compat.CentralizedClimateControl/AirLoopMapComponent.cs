using System.Collections.Generic;
using System.Reflection;
using CentralizedClimateControl;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.CentralizedClimateControl
{
    /// <summary>
    /// 每条 CCC 气流回路一个集总气袋，按 AO 的模拟步推进。
    ///
    /// 为什么要有这个东西：CCC 的 AirFlowNet 每次 Tick 都从零重算，不保存任何存量，
    /// 温度取全网算术平均。它没有质量、没有压力、没有输运延迟，所以无法表达
    /// 「风机压不过背压」「管道里存着上一轮冷气」「关掉一半出风口就憋压」这些行为。
    ///
    /// 这个组件的职责边界（本 mod 是 AO 的扩展层）：
    ///   - 只维护 AO 没有的管道存量；
    ///   - 每次 AO 步进把气袋状态翻译成 AO 的体积源；
    ///   - 不检查 AO 是否失败、不检查暂停、不在非 AO 地图上做物理兜底。
    /// </summary>
    public class AirLoopMapComponent : MapComponent
    {
        private const float PipeVolumePerCell = 0.6f;
        private const float DeviceVolume = 1.5f;
        private const float MinimumVolume = 3f;
        private const float StandardPressurePa = 101325f;

        // 沿程损失第一档：每格管道把阻抗抬高 2%，最多 6 倍。
        private const float FrictionPerPipeCell = 0.02f;
        private const float MaxFrictionFactor = 6f;

        // 出风口风矢量标定。CCC 的 baseAirExhaust 是 CFM 量级，按格面积折算只有
        // 0.02 m/s，肉眼完全察觉不到；这里按「每 CFM 0.01 m/s」放大到可见量级：
        // 50 CFM → 0.5 m/s，300 CFM → 3 m/s，600 CFM → 6 m/s。
        private const float WindSpeedPerAirFlowUnit = 0.01f;
        private const float MaxWindSpeed = 8f;
        private const float WindBlendPerTick = 0.1f;

        private static readonly FieldInfo NetsField =
            typeof(AirFlowNetManager).GetField("cachedNets", BindingFlags.Instance | BindingFlags.NonPublic);

        // CCC 的 AirFlowNet.Connectors 从来没有被填过（validateBuilding 只加 Producers/Consumers/TempControls），
        // 所以管道格数只能从管理器自己的 cachedPipes 里按 GridID 统计。
        private static readonly FieldInfo PipesField =
            typeof(AirFlowNetManager).GetField("cachedPipes", BindingFlags.Instance | BindingFlags.NonPublic);

        private Dictionary<long, AirLoop> loops = new Dictionary<long, AirLoop>();
        private readonly HashSet<long> aliveScratch = new HashSet<long>();
        private readonly List<long> deadScratch = new List<long>();

        // 每个出风口一组 AO 连续源槽位，只用来写风矢量。
        // 槽位名是 prefix + slot，不按 owner 区分，所以每个 owner 必须有独立 prefix。
        private readonly Dictionary<Thing, object> windSlots = new Dictionary<Thing, object>();
        private readonly HashSet<Thing> liveWindOwners = new HashSet<Thing>();
        private readonly List<Thing> deadWindOwners = new List<Thing>();

        public AirLoopMapComponent(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref loops, "airLoops", LookMode.Value, LookMode.Deep);
            if (loops == null)
            {
                loops = new Dictionary<long, AirLoop>();
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();

            // 读档时 AirFlowNet 还没有重建（CompAirFlowPipe 的 ExposeData 早于本组件），
            // 所以这里只补质量、不重建拓扑；拓扑在下一帧 StepAll 里自然建立。
            foreach (KeyValuePair<long, AirLoop> pair in loops)
            {
                AirLoop loop = pair.Value;
                if (loop == null)
                {
                    continue;
                }
                // 字典键才是权威指纹，字段只用于显示与存档自检。
                loop.Key = pair.Key;
                if (loop.MassKg <= 0.01f)
                {
                    loop.ResetMassForVolume();
                }
            }
        }

        /// <summary>推进全部回路。dt 是 AO 本步实际推进的秒数。</summary>
        public void StepAll(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            List<AirFlowNet> nets = ReadNets();
            if (nets == null)
            {
                return;
            }

            List<CompAirFlow> pipes = ReadPipes();

            aliveScratch.Clear();
            liveWindOwners.Clear();

            for (int i = 0; i < nets.Count; i++)
            {
                AirFlowNet net = nets[i];
                if (net == null)
                {
                    continue;
                }

                long key = ComputeKey(net);
                if (key == 0L)
                {
                    continue;
                }

                aliveScratch.Add(key);

                AirLoop loop;
                if (!loops.TryGetValue(key, out loop))
                {
                    loop = CreateLoop(key, net);
                    loops[key] = loop;
                }

                EnsureAtmosphere(loop, net);
                RefreshLoopShape(loop, net, pipes);
                StepLoop(loop, net, dt);
            }

            PruneDeadLoops();
            PruneDeadWindOwners();
        }

        /// <summary>CCC 的网络每帧都被重算，cachedNets 在重建帧里是旧的，直接跳过。</summary>
        private List<AirFlowNet> ReadNets()
        {
            AirFlowNetManager manager = CentralizedClimateControlUtility.GetNetManager(map);
            if (manager == null || manager.IsDirty || NetsField == null)
            {
                return null;
            }
            return NetsField.GetValue(manager) as List<AirFlowNet>;
        }

        private List<CompAirFlow> ReadPipes()
        {
            AirFlowNetManager manager = CentralizedClimateControlUtility.GetNetManager(map);
            if (manager == null || PipesField == null)
            {
                return null;
            }
            return PipesField.GetValue(manager) as List<CompAirFlow>;
        }

        /// <summary>统计属于某个网络的管道格数。设备不计入管道容积，另按台数折算。</summary>
        private int CountPipeCells(AirFlowNet net, List<CompAirFlow> pipes)
        {
            if (pipes == null)
            {
                return 0;
            }
            int gridId = net.GridID;
            if (gridId < 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < pipes.Count; i++)
            {
                CompAirFlow pipe = pipes[i];
                if (pipe == null || pipe.parent == null || pipe.parent.Destroyed)
                {
                    continue;
                }
                if (pipe.GridID != gridId || !(pipe is CompAirFlowPipe))
                {
                    continue;
                }
                count += Mathf.Max(1, pipe.parent.def.size.x * pipe.parent.def.size.z);
            }
            return count;
        }

        private long ComputeKey(AirFlowNet net)
        {
            int minimum = int.MaxValue;
            AccumulateMinimumCell(net.Connectors, ref minimum);
            AccumulateMinimumCell(net.Producers, ref minimum);
            AccumulateMinimumCell(net.Consumers, ref minimum);
            AccumulateMinimumCell(net.TempControls, ref minimum);

            if (minimum == int.MaxValue)
            {
                return 0L;
            }

            return ((long)(int)net.FlowType << 40) | (long)minimum;
        }

        private void AccumulateMinimumCell<T>(List<T> comps, ref int minimum) where T : CompAirFlow
        {
            if (comps == null)
            {
                return;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                CompAirFlow comp = comps[i];
                if (comp == null || comp.parent == null)
                {
                    continue;
                }
                int index = map.cellIndices.CellToIndex(comp.parent.Position);
                if (index < minimum)
                {
                    minimum = index;
                }
            }
        }

        private AirLoop CreateLoop(long key, AirFlowNet net)
        {
            AirLoop loop = new AirLoop();
            loop.Key = key;
            loop.FlowType = (int)net.FlowType;
            loop.VolumeM3 = MinimumVolume;
            loop.ResetMassForVolume();
            InitializeAtmosphere(loop, net);
            return loop;
        }

        /// <summary>新回路从现场继承氧与水汽，避免第一口气是「纯干氧」。</summary>
        private void InitializeAtmosphere(AirLoop loop, AirFlowNet net)
        {
            IntVec3 sample;
            if (!TryPickSampleCell(net, out sample))
            {
                return;
            }

            float oxygen;
            if (AoBridge.TryGetOxygenPercent(map, sample, out oxygen))
            {
                loop.OxygenPercent = Mathf.Clamp(oxygen, 0f, 100f);
            }

            float vapor;
            if (AoBridge.TryGetAbsoluteVapor(map, sample, out vapor))
            {
                loop.VaporContent = vapor;
                loop.VaporValid = true;
            }
        }

        /// <summary>
        /// 懒标定：水汽只在第一次真正读到时才可信。
        /// 读档后 VaporValid 为 false，这里补一次，避免用 0 去「吸干」房间湿度。
        /// </summary>
        private void EnsureAtmosphere(AirLoop loop, AirFlowNet net)
        {
            if (loop.VaporValid)
            {
                return;
            }
            InitializeAtmosphere(loop, net);
        }

        private bool TryPickSampleCell(AirFlowNet net, out IntVec3 cell)
        {
            if (net.Consumers != null)
            {
                for (int i = 0; i < net.Consumers.Count; i++)
                {
                    CompAirFlowConsumer consumer = net.Consumers[i];
                    if (consumer != null && consumer.parent != null && !consumer.parent.Destroyed)
                    {
                        cell = consumer.parent.Position +
                               IntVec3Utility.RotatedBy(IntVec3.North, consumer.parent.Rotation);
                        return true;
                    }
                }
            }

            if (net.Producers != null)
            {
                for (int i = 0; i < net.Producers.Count; i++)
                {
                    CompAirFlowProducer producer = net.Producers[i];
                    if (producer != null && producer.parent != null && !producer.parent.Destroyed)
                    {
                        cell = producer.parent.Position;
                        return true;
                    }
                }
            }

            cell = IntVec3.Invalid;
            return false;
        }

        /// <summary>容积与额定流量随拓扑变化重算；质量与温度保留，作为「旧气」延续。</summary>
        private void RefreshLoopShape(AirLoop loop, AirFlowNet net, List<CompAirFlow> pipes)
        {
            int pipeCells = CountPipeCells(net, pipes);
            int producerCount = net.Producers != null ? net.Producers.Count : 0;
            int consumerCount = net.Consumers != null ? net.Consumers.Count : 0;
            int tempControlCount = net.TempControls != null ? net.TempControls.Count : 0;

            float volume = pipeCells * PipeVolumePerCell + (producerCount + consumerCount + tempControlCount) * DeviceVolume;
            loop.VolumeM3 = Mathf.Max(MinimumVolume, volume);
            loop.ProducerCount = producerCount;
            loop.ConsumerCount = consumerCount;
            loop.TempControlCount = tempControlCount;

            float intakeAirFlow = 0f;
            if (net.Producers != null)
            {
                for (int i = 0; i < net.Producers.Count; i++)
                {
                    CompAirFlowProducer producer = net.Producers[i];
                    if (producer != null)
                    {
                        intakeAirFlow += producer.Props.baseAirFlow;
                    }
                }
            }

            float exhaustAirFlow = 0f;
            if (net.Consumers != null)
            {
                for (int i = 0; i < net.Consumers.Count; i++)
                {
                    CompAirFlowConsumer consumer = net.Consumers[i];
                    if (consumer != null)
                    {
                        exhaustAirFlow += consumer.Props.baseAirExhaust;
                    }
                }
            }

            float multiplier = Mathf.Max(0.01f, CentralizedClimateControlUtility.AirFlowMultiplier);
            float exhaustMultiplier = Mathf.Max(0.01f, CentralizedClimateControlUtility.AirExhaustMultiplier);

            loop.RatedIntakeKgPerSec = intakeAirFlow * multiplier * AirLoop.CubicMetresPerAirFlowUnit * AirLoop.AirDensityKgPerM3;
            loop.RatedExhaustKgPerSec = exhaustAirFlow * exhaustMultiplier * AirLoop.CubicMetresPerAirFlowUnit * AirLoop.AirDensityKgPerM3;

            // 沿程损失第一档：把管道格数折成阻抗。同时抬高额定压差、压低风机扬程，
            // 于是长管网在同样压差下流量更小，短管网几乎不受影响。
            float friction = Mathf.Min(MaxFrictionFactor, 1f + pipeCells * FrictionPerPipeCell);
            loop.EffectivePressurePa = loop.RatedPressurePa * friction;
            loop.EffectiveFanMaxRisePa = loop.FanMaxRisePa / friction;
        }

        private void StepLoop(AirLoop loop, AirFlowNet net, float dt)
        {
            // 1. 进气：取所有可用风机的平均进气温度。设备状态由本组件维护，
            //    因为原版 Building_IntakeFan.TickRare 已被拦截，不再写这些字段。
            float intakeSumKelvin = 0f;
            int intakeSamples = 0;
            float actualIntakeAirFlow = 0f;
            float intakeOxygenSum = 0f;
            float intakeVaporSum = 0f;

            if (net.Producers != null)
            {
                for (int i = 0; i < net.Producers.Count; i++)
                {
                    CompAirFlowProducer producer = net.Producers[i];
                    if (producer == null || producer.parent == null || producer.parent.Destroyed)
                    {
                        continue;
                    }

                    RefreshProducerState(producer);
                    if (!producer.IsActive())
                    {
                        continue;
                    }

                    IntVec3 cell = producer.parent.Position;
                    IntVec3 mouth = cell + IntVec3Utility.RotatedBy(IntVec3.North, producer.parent.Rotation);
                    float celsius;
                    if (!AoBridge.TryGetTemperature(map, mouth, out celsius) &&
                        !AoBridge.TryGetTemperature(map, cell, out celsius))
                    {
                        continue;
                    }

                    intakeSumKelvin += celsius + 273.15f;
                    ++intakeSamples;
                    actualIntakeAirFlow += producer.Props.baseAirFlow;

                    float oxygen;
                    intakeOxygenSum += AoBridge.TryGetOxygenPercent(map, mouth, out oxygen)
                        ? oxygen
                        : loop.OxygenPercent;

                    float vapor;
                    intakeVaporSum += AoBridge.TryGetAbsoluteVapor(map, mouth, out vapor)
                        ? vapor
                        : loop.VaporContent;
                }
            }

            float intakeKelvin = intakeSamples > 0
                ? intakeSumKelvin / intakeSamples
                : loop.TemperatureK;
            float intakeOxygen = intakeSamples > 0 ? intakeOxygenSum / intakeSamples : loop.OxygenPercent;
            float intakeVapor = intakeSamples > 0 ? intakeVaporSum / intakeSamples : loop.VaporContent;

            // 只有真正可用的风机才计入进气能力：全部被挡或断电时不应凭空进气。
            float availableIntakeKgPerSec = actualIntakeAirFlow *
                                            Mathf.Max(0.01f, CentralizedClimateControlUtility.AirFlowMultiplier) *
                                            AirLoop.CubicMetresPerAirFlowUnit * AirLoop.AirDensityKgPerM3;

            // 2. 温控：换算成一阶惯性增益。原版 DeltaTemperature += energyPerSecond * eff * (Δ/96)，
            //    每 250 tick 一次；这里改成按秒连续，时间常数保持不变。
            float gainPerSecond = 0f;
            float targetKelvin = loop.TemperatureK;
            float targetSumKelvin = 0f;
            int targetSamples = 0;
            if (net.TempControls != null)
            {
                for (int i = 0; i < net.TempControls.Count; i++)
                {
                    CompAirFlowTempControl control = net.TempControls[i];
                    if (control == null || control.parent == null || control.parent.Destroyed)
                    {
                        continue;
                    }

                    RefreshTempControlState(control);
                    if (!control.IsActive())
                    {
                        continue;
                    }

                    CompTempControl vanilla = control.parent.GetComp<CompTempControl>();
                    if (vanilla == null)
                    {
                        continue;
                    }

                    float efficiency = Mathf.Clamp01(net.ThermalEfficiency);
                    gainPerSecond += vanilla.Props.energyPerSecond * efficiency / 96f * 5f;
                    targetSumKelvin += vanilla.targetTemperature + 273.15f;
                    ++targetSamples;
                }
            }
            if (targetSamples > 0)
            {
                targetKelvin = targetSumKelvin / targetSamples;
            }

            // 3. 取房间气压：优先用出风口所在格，退回到进风口，再退回到标准大气压。
            float roomPressurePa = ResolveRoomPressure(net, loop);

            // 4. 推进气袋。
            LoopStep step = loop.Step(
                dt,
                availableIntakeKgPerSec,
                intakeKelvin,
                intakeOxygen,
                intakeVapor,
                gainPerSecond,
                targetKelvin,
                roomPressurePa);

            // 5. 翻译成 AO 体积源：进风口抽气，出风口注入设定温度的气团。
            float intakeCubicMetres = step.IntakeMassKg / AirLoop.AirDensityKgPerM3;
            float exhaustCubicMetres = step.ExhaustMassKg / AirLoop.AirDensityKgPerM3;

            if (net.Producers != null && intakeCubicMetres > 1e-6f)
            {
                float share = intakeCubicMetres / Mathf.Max(1, net.Producers.Count);
                for (int i = 0; i < net.Producers.Count; i++)
                {
                    CompAirFlowProducer producer = net.Producers[i];
                    if (producer == null || producer.parent == null || producer.parent.Destroyed)
                    {
                        continue;
                    }
                    IntVec3 mouth = producer.parent.Position +
                                    IntVec3Utility.RotatedBy(IntVec3.North, producer.parent.Rotation);
                    AoBridge.AddVolumeSource(map, mouth, -share / 5f, 0f, 0f);
                }
            }

            if (net.Consumers != null && exhaustCubicMetres > 1e-6f)
            {
                float share = exhaustCubicMetres / Mathf.Max(1, net.Consumers.Count);
                for (int i = 0; i < net.Consumers.Count; i++)
                {
                    CompAirFlowConsumer consumer = net.Consumers[i];
                    if (consumer == null || consumer.parent == null || consumer.parent.Destroyed)
                    {
                        continue;
                    }
                    IntVec3 vent = consumer.parent.Position +
                                   IntVec3Utility.RotatedBy(IntVec3.North, consumer.parent.Rotation);
                    AoBridge.AddVolumeSourceAtTemperature(
                        map,
                        vent,
                        share / 5f,
                        loop.OxygenPercent,
                        loop.TemperatureK - 273.15f);

                    // 水汽不走体积源：AO 的 MixReleasedGas 只处理氧与温度。
                    // 把回路浓度与房间浓度的差按注入体积摊到该格，等价于两团气完全混合。
                    // 只有在回路水汽已标定过时才搬，否则默认 0 会凭空吸干房间。
                    if (loop.VaporValid)
                    {
                        float roomVapor;
                        if (AoBridge.TryGetAbsoluteVapor(map, vent, out roomVapor))
                        {
                            float vaporDelta = share * (loop.VaporContent - roomVapor) / (5f + share);
                            if (vaporDelta != 0f)
                            {
                                AoBridge.AddMoistureDelta(map, vent, vaporDelta);
                            }
                        }
                    }

                    ApplyWind(consumer, vent);
                }
            }

            // 6. 回填原版用于显示的字段。这些字段不再有任何物理作用，只给 inspect 面板看。
            if (net.Producers != null && intakeSamples > 0 && dt > 0f)
            {
                float airFlow = step.IntakeMassKg / dt / (AirLoop.CubicMetresPerAirFlowUnit * AirLoop.AirDensityKgPerM3);
                for (int i = 0; i < net.Producers.Count; i++)
                {
                    CompAirFlowProducer producer = net.Producers[i];
                    if (producer == null)
                    {
                        continue;
                    }
                    producer.IntakeTemperature = intakeKelvin - 273.15f;
                    producer.CurrentAirFlow = airFlow;
                }
            }

            if (net.TempControls != null)
            {
                for (int i = 0; i < net.TempControls.Count; i++)
                {
                    CompAirFlowTempControl control = net.TempControls[i];
                    if (control == null)
                    {
                        continue;
                    }
                    control.IntakeTemperature = intakeKelvin - 273.15f;
                    control.ConvertedTemperature = loop.TemperatureK - 273.15f;
                }
            }
        }

        /// <summary>重建风机状态。原版 TickRare 被拦截后，这几个字段没人再写。</summary>
        private void RefreshProducerState(CompAirFlowProducer producer)
        {
            CompPowerTrader power = producer.parent.GetComp<CompPowerTrader>();
            producer.IsPoweredOff = power != null && !power.PowerOn;
            producer.IsBrokenDown = producer.parent.IsBrokenDown();

            IntVec3 mouth = producer.parent.Position +
                            IntVec3Utility.RotatedBy(IntVec3.North, producer.parent.Rotation);
            producer.IsBlocked = !mouth.InBounds(map) || mouth.Impassable(map);
        }

        private void RefreshTempControlState(CompAirFlowTempControl control)
        {
            CompPowerTrader power = control.parent.GetComp<CompPowerTrader>();
            control.IsPoweredOff = power != null && !power.PowerOn;
            control.IsBrokenDown = control.parent.IsBrokenDown();
            control.IsBlocked = false;
        }

        private float ResolveRoomPressure(AirFlowNet net, AirLoop loop)
        {
            if (net.Consumers != null)
            {
                for (int i = 0; i < net.Consumers.Count; i++)
                {
                    CompAirFlowConsumer consumer = net.Consumers[i];
                    if (consumer == null || consumer.parent == null || consumer.parent.Destroyed)
                    {
                        continue;
                    }
                    IntVec3 vent = consumer.parent.Position +
                                   IntVec3Utility.RotatedBy(IntVec3.North, consumer.parent.Rotation);
                    float pascals;
                    if (AoBridge.TryGetStaticPressure(map, vent, out pascals))
                    {
                        return pascals;
                    }
                }
            }

            if (net.Producers != null)
            {
                for (int i = 0; i < net.Producers.Count; i++)
                {
                    CompAirFlowProducer producer = net.Producers[i];
                    if (producer == null || producer.parent == null || producer.parent.Destroyed)
                    {
                        continue;
                    }
                    float pascals;
                    if (AoBridge.TryGetStaticPressure(map, producer.parent.Position, out pascals))
                    {
                        return pascals;
                    }
                }
            }

            return StandardPressurePa;
        }

        /// <summary>
        /// 给一个出风口写持续风矢量。
        ///
        /// 走 AO 的 SourceSlots 通路而不是 AddVolumeSource：体积源只写散度，
        /// 投影法解出的是各向同性辐散，没有方向；SourceSlots 的 MaintainTargetWind
        /// 会把该格速度逐步拉向 TargetWind，这才是「送风口」该有的行为。
        ///
        /// 温度、氧、水汽仍走 AddVolumeSourceAtTemperature，这里只写风，
        /// 所以 TemperaturePerTick 保持 0，不会重复加热。
        /// </summary>
        private void ApplyWind(CompAirFlowConsumer consumer, IntVec3 vent)
        {
            Thing owner = consumer.parent;
            if (owner == null || owner.Destroyed)
            {
                return;
            }

            object slots;
            if (!windSlots.TryGetValue(owner, out slots))
            {
                slots = AoBridge.NewSlots("JAP.Wind." + owner.thingIDNumber + ".");
                if (slots == null)
                {
                    return;
                }
                windSlots[owner] = slots;
            }

            liveWindOwners.Add(owner);

            ThingWithComps holder = owner as ThingWithComps;
            CompPowerTrader power = holder != null ? holder.GetComp<CompPowerTrader>() : null;
            CompFlickable flick = holder != null ? holder.GetComp<CompFlickable>() : null;
            bool powered = power == null || power.PowerOn;
            bool switchedOn = flick == null || flick.SwitchIsOn;
            bool blocked = !vent.InBounds(map) || vent.Impassable(map);
            bool active = powered && switchedOn && !owner.IsBrokenDown() && !blocked;

            // 方向就是出风口朝向：建筑旋转后的 North，与 AO 温控出风口一致。
            IntVec3 offset = IntVec3Utility.RotatedBy(IntVec3.North, owner.Rotation);
            Vector2 direction = new Vector2(offset.x, offset.z);
            float length = direction.magnitude;
            direction = length > 1e-4f ? direction / length : Vector2.zero;

            float airFlow = consumer.Props.baseAirExhaust *
                            Mathf.Max(0.01f, CentralizedClimateControlUtility.AirExhaustMultiplier);
            float speed = active ? Mathf.Min(MaxWindSpeed, airFlow * WindSpeedPerAirFlowUnit) : 0f;

            SlotRates rates = default(SlotRates);
            rates.MaintainTargetWind = active && speed > 0f;
            rates.TargetWind = direction * speed;
            rates.TargetWindBlendPerTick = WindBlendPerTick;
            AoBridge.SetSlot(slots, owner, 0, vent, rates);
        }

        /// <summary>出风口被拆或改向后，注销它残留的连续源。</summary>
        private void PruneDeadWindOwners()
        {
            // 不能用「数量相等」早退：拆一个、加一个时数量不变但集合不同，
            // 漏掉的 owner 会让 AO 侧的风一直吹。
            deadWindOwners.Clear();
            foreach (KeyValuePair<Thing, object> pair in windSlots)
            {
                if (!liveWindOwners.Contains(pair.Key))
                {
                    deadWindOwners.Add(pair.Key);
                }
            }

            for (int i = 0; i < deadWindOwners.Count; i++)
            {
                Thing owner = deadWindOwners[i];
                object slots;
                if (windSlots.TryGetValue(owner, out slots))
                {
                    AoBridge.RemoveAll(slots, owner, map);
                }
                windSlots.Remove(owner);
            }
        }

        private void PruneDeadLoops()
        {
            if (loops.Count == aliveScratch.Count)
            {
                return;
            }

            deadScratch.Clear();
            foreach (KeyValuePair<long, AirLoop> pair in loops)
            {
                if (!aliveScratch.Contains(pair.Key))
                {
                    deadScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < deadScratch.Count; i++)
            {
                loops.Remove(deadScratch[i]);
            }
        }
    }
}
