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

        // 出风口风矢量标定。
        //
        // AO 的风速刻度是「米/秒」，量级由它自己的建筑给出：
        //   AtmosphereTuning.xml   MaxWindSpeed = 120、ExplosionMaxWindSpeed = 90
        //   Buildings_Fans.xml     小型风扇  ratedFreeAirFlowM3PerSecond=10  / outletAreaM2=1  → 10 m/s
        //                          archotech   ratedFreeAirFlowM3PerSecond=2250/ outletAreaM2=25 → 90 m/s
        //   Buildings_Turbojet.xml maxSpeed = 150、halfSpeed = 60
        // 也就是 0~120 才是「有风」的量级，1 m/s 在这个刻度上等于静止。
        // CCC 的 baseAirExhaust 是 CFM（WallAirVent = 100），按「每 CFM 0.1 m/s」折算，
        // 100 CFM 出风口 = 10 m/s，正好与 AO 最小风扇同档，风场图上可见。
        private const float WindSpeedPerAirFlowUnit = 0.1f;
        private const float MaxWindSpeed = 120f;
        private const float WindBlendPerTick = 0.1f;

        // 出风口热源的散布范围。
        //
        // 需求是「温度从出风口缓慢发散到整个房间」。只加热一格，热量会被求解器
        // 立刻抹平又补不满，观感上就是卡在某个温度不动；整房均匀加热又不是用户要的。
        // 所以按距离加权撒到出风口外圈若干格，靠 AO 自己的平流和扩散往外铺。
        private const int SpreadRadiusCells = 5;
        private const int MaxSpreadCells = 40;
        private const float SpreadFalloffPerCell = 1f;

        // 单步积分上限（秒）。
        //
        // AO 的基础步长是 baseIntervalTicks = 30，即 0.5 秒。但加载存档、切图、
        // 或者 AO 卡住一段时间后补推进时，lastSynchronizedSimulationTick 的差值
        // 会是一个巨大的数 —— 实测见过 dt = 6754 秒。用这么大的步长积分，一步就能
        // 把气袋质量抽掉一半、压力从 101325 Pa 打到几千帕，随后出气驱动恒为下限、
        // 进气被质量守恒一起锁死，面板表现为「总流速 0、变温效率 340000000%」。
        //
        // 截断而不是丢弃：丢掉会少算一截真实推进，截断只是把这部分推进按正常步长
        // 分摊到后续几步里，物理上等价于「求解器只按自己支持的步长走」。
        private const float MaxStepSeconds = 0.5f;

        // 回填给面板的流量做一阶低通的比例（每步向瞬时值靠拢多少）。
        // 0.25 约等于 4 步时间常数；配上 0.1~0.2 s 的步长，读数在 0.5 s 内跟手
        // 又不会把步长抖动传到面板上。
        private const float CurrentAirFlowBlend = 0.25f;

        // 交给 AO 的交付倍率。
        //
        // CCC 内部的气袋模型一律不动 —— 面板上的「总流速」是按 CCC 自己的
        // CubicMetresPerAirFlowUnit 反算出来的，改了内部标定，显示就会跟着失真。
        // 这里只在「把结果写成 AO 体积源 / 连续源」这一步放大。
        //
        // 三个旋钮的物理含义不一样，不要一起拉：
        //
        //   体积（AoVolumeScale）：注入与抽走的空气量。它进的是 AO 的散度方程，
        //       会直接改变房间气压。放到 100 倍会让房间持续被加压、抽气格强负压，
        //       表现是满屋乱风 + 温度反而被吹散。10 倍以内还算安全的试错区间。
        //
        //   热量（AoHeatScale）：直接写 TemperaturePerTick，不参与质量守恒，
        //       是最安全的杠杆。100 格房间、WallAirVent(100 CFM)、温差 10 ℃ 时
        //       本来就约 0.3 ℃/秒（与原版 TickRare 同速），通常不需要放大。
        //
        //   风速：由 WindSpeedPerAirFlowUnit 单独标定，已与 AO 最小风扇同档。
        //
        // 需要更强的观感时，先拉热量，再考虑体积，最后才是风。
        private const float AoVolumeScale = 1f;
        private const float AoHeatScale = 1f;

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
        // 每个出风口上次写到第几号槽位；改走公共连续源后不再需要预建 slots 表。
        private readonly Dictionary<Thing, int> windWritten = new Dictionary<Thing, int>();
        private readonly HashSet<Thing> liveWindOwners = new HashSet<Thing>();
        private readonly List<Thing> deadWindOwners = new List<Thing>();

        // 收集一个出风口当前真正能向哪几格送气。墙挂型一格，Surround 型八邻域一圈。
        private readonly List<IntVec3> ventScratch = new List<IntVec3>();

        // 出风口热源的分布格：从出风格向外 BFS 若干圈，权重按距离递减。
        // 需求是「温度从出风口缓慢发散到整个房间」，所以不能整房均匀加热，
        // 也不能只加热一格（一格会立刻被求解器摊平又补不满）。
        private readonly List<IntVec3> spreadCells = new List<IntVec3>();
        private readonly List<float> spreadWeights = new List<float>();
        private readonly Dictionary<int, int> spreadVisited = new Dictionary<int, int>();
        private readonly List<int> spreadQueue = new List<int>();

        // 管道格数缓存：CountPipeCells 原来每模拟步对每条 net 全量扫一遍 pipes，
        // 总代价 O(net 数 × 管道数)。CCC 的网络重建会置 manager.IsDirty，
        // 这里在 dirty、pipes 列表引用变化或数量变化时清空缓存。
        private readonly Dictionary<int, int> pipeCellCache = new Dictionary<int, int>();
        private List<CompAirFlow> cachedPipesList;
        private int cachedPipesCount = -1;

        // 运行期诊断。
        //
        // CCC 面板上的「总流速 / 变温效率 / 空气流动效率」全部由 AirFlowNetTick 从气袋模型算出，
        // 与 AO 是否真的收到体积源和风矢量没有任何反馈关系 —— 出风口显示 100% 并不能证明
        // AO 那边收到了东西。所以这里定期把「本 mod 真正交给 AO 的量」打到日志里，
        // 用来区分「气袋没出气」和「AO 吞掉了注入」。
        private int diagCountdown = 1;
        private int diagNets;
        private int diagLiveVents;
        private float diagExhaustM3;
        private int diagInjectedCells;
        private float diagInjectedVolumes;
        private float diagWindSpeed;

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

            // 见 MaxStepSeconds 的注释：加载存档/切图后的第一次推进可能是几千秒。
            if (dt > MaxStepSeconds)
            {
                dt = MaxStepSeconds;
            }

            List<AirFlowNet> nets = ReadNets();
            if (nets == null)
            {
                return;
            }

            List<CompAirFlow> pipes = ReadPipes();

            aliveScratch.Clear();
            liveWindOwners.Clear();

            diagNets = 0;
            diagLiveVents = 0;
            diagExhaustM3 = 0f;
            diagInjectedCells = 0;
            diagInjectedVolumes = 0f;
            diagWindSpeed = 0f;

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

            if (--diagCountdown <= 0)
            {
                diagCountdown = 60;
                // Log.Message(string.Concat(
                //     "[AOPatch/CCC] dt=", dt.ToString("F2"),
                //     " 回路=", diagNets.ToString(),
                //     " 出气=", diagExhaustM3.ToString("F4"), " m3/步",
                //     " 可用出风口=", diagLiveVents.ToString(),
                //     " 注入格=", diagInjectedCells.ToString(),
                //     " 注入体积=", diagInjectedVolumes.ToString("F4"),
                //     " 风速=", diagWindSpeed.ToString("F2"), " m/s"));
            }
        }

        /// <summary>CCC 的网络每帧都被重算，cachedNets 在重建帧里是旧的，直接跳过。</summary>
        private List<AirFlowNet> ReadNets()
        {
            AirFlowNetManager manager = CentralizedClimateControlUtility.GetNetManager(map);
            if (manager == null || manager.IsDirty || NetsField == null)
            {
                // 网络正在重建：管道列表与 GridID 都可能变，格数缓存必须作废。
                ClearPipeCellCache();
                return null;
            }
            return NetsField.GetValue(manager) as List<AirFlowNet>;
        }

        private List<CompAirFlow> ReadPipes()
        {
            AirFlowNetManager manager = CentralizedClimateControlUtility.GetNetManager(map);
            if (manager == null || PipesField == null)
            {
                ClearPipeCellCache();
                return null;
            }
            List<CompAirFlow> pipes = PipesField.GetValue(manager) as List<CompAirFlow>;
            int count = pipes != null ? pipes.Count : 0;
            if (!object.ReferenceEquals(pipes, cachedPipesList) || count != cachedPipesCount)
            {
                ClearPipeCellCache();
                cachedPipesList = pipes;
                cachedPipesCount = count;
            }
            return pipes;
        }

        private void ClearPipeCellCache()
        {
            pipeCellCache.Clear();
            cachedPipesList = null;
            cachedPipesCount = -1;
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

            int cached;
            if (pipeCellCache.TryGetValue(gridId, out cached))
            {
                return cached;
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
            pipeCellCache[gridId] = count;
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

                    IntVec3 sampleCell;
                    float celsius;
                    if (!TrySampleIntake(producer, out sampleCell, out celsius))
                    {
                        continue;
                    }

                    intakeSumKelvin += celsius + 273.15f;
                    ++intakeSamples;
                    actualIntakeAirFlow += producer.Props.baseAirFlow;

                    float oxygen;
                    intakeOxygenSum += AoBridge.TryGetOxygenPercent(map, sampleCell, out oxygen)
                        ? oxygen
                        : loop.OxygenPercent;

                    float vapor;
                    intakeVaporSum += AoBridge.TryGetAbsoluteVapor(map, sampleCell, out vapor)
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
                    // 抽气口必须落在风机外圈的可通行格上。2x2 大风机用
                    // 「Position + 朝向一格」会落回自身占位（Impassable），
                    // AO 在那个格子上抽气等于对着自己抽。
                    IntVec3 mouth;
                    float mouthCelsius;
                    if (!TrySampleIntake(producer, out mouth, out mouthCelsius))
                    {
                        continue;
                    }
                    AoBridge.AddVolumeSource(map, mouth, -share * AoVolumeScale / 5f, 0f, 0f);
                }
            }

            // 凝结水的落地位置就是出风口本身：冷风从哪儿吹出来，水就从哪儿滴下来。
            // 没有可用出风口时保持 null，这一轮的凝结量继续攒在 loop.CondensateKg 里。
            Thing condensateSource = null;

            ++diagNets;
            if (net.Consumers != null && exhaustCubicMetres > 1e-6f)
            {
                diagExhaustM3 += exhaustCubicMetres;
                // 先数清楚有几个出风口真的能出气。全被堵住的出风口不该分到流量，
                // 否则气会白白消失在墙里，回路压力凭空下降。
                int liveVents = 0;
                for (int i = 0; i < net.Consumers.Count; i++)
                {
                    CompAirFlowConsumer probe = net.Consumers[i];
                    if (probe == null || probe.parent == null || probe.parent.Destroyed)
                    {
                        continue;
                    }
                    CollectVentCells(probe, ventScratch);
                    if (ventScratch.Count > 0)
                    {
                        ++liveVents;
                    }
                }

                if (liveVents > 0)
                {
                    diagLiveVents += liveVents;
                    float share = exhaustCubicMetres / liveVents;
                    for (int i = 0; i < net.Consumers.Count; i++)
                    {
                        CompAirFlowConsumer consumer = net.Consumers[i];
                        if (consumer == null || consumer.parent == null || consumer.parent.Destroyed)
                        {
                            continue;
                        }

                        CollectVentCells(consumer, ventScratch);
                        if (ventScratch.Count == 0)
                        {
                            continue;
                        }

                        // 一个出风口可能同时向多格送气（Surround 八邻域）。
                        // 体积、氧、热、湿按格数平摊，总通量不变。
                        float perCellM3 = share / ventScratch.Count;
                        // 交付倍率同时作用于体积、水汽，保证「注入的气」内部比例一致。
                        float deliveredM3 = perCellM3 * AoVolumeScale;
                        float cellVolumes = deliveredM3 / 5f;
                        for (int k = 0; k < ventScratch.Count; k++)
                        {
                            IntVec3 vent = ventScratch[k];
                            AoBridge.AddVolumeSourceAtTemperature(
                                map,
                                vent,
                                cellVolumes,
                                loop.OxygenPercent,
                                loop.TemperatureK - 273.15f);

                            ++diagInjectedCells;
                            diagInjectedVolumes += cellVolumes;

                            // 水汽不走体积源：AO 的 MixReleasedGas 只处理氧与温度。
                            // 把回路浓度与房间浓度的差按注入体积摊到该格，等价于两团气完全混合。
                            // 只有在回路水汽已标定过时才搬，否则默认 0 会凭空吸干房间。
                            if (loop.VaporValid)
                            {
                                float roomVapor;
                                if (AoBridge.TryGetAbsoluteVapor(map, vent, out roomVapor))
                                {
                                    float vaporDelta = deliveredM3 * (loop.VaporContent - roomVapor) /
                                                       (5f + deliveredM3);
                                    if (vaporDelta != 0f)
                                    {
                                        AoBridge.AddMoistureDelta(map, vent, vaporDelta);
                                    }
                                }
                            }

                            // 冷凝水按「房间这边的空气被冷却到送风温度」算，不按气袋算。
                            //
                            // 物理依据：冷风把这格房间空气降到 supplyCelsius，
                            // 超出该温度饱和量的水必然析出，量就是浓度差。
                            // 所以产水量取决于房间有多潮，而不是管道里存了多少水 ——
                            // 房间干燥时一滴都不会有，房间闷湿时才会哗哗出水。
                            if (condensateSource != null && AoBridge.WaterAvailable)
                            {
                                float roomVaporForWater;
                                if (AoBridge.TryGetAbsoluteVapor(map, vent, out roomVaporForWater))
                                {
                                    float supplyCelsiusForWater = loop.TemperatureK - 273.15f;
                                    float supplyCapacity = AirLoop.VaporCapacity(supplyCelsiusForWater);
                                    float excess = roomVaporForWater - supplyCapacity;
                                    if (excess > 0f)
                                    {
                                        // deliveredM3 是这一步注入的体积；AO 的 1 格 = 5 m³。
                                        float cellFraction = deliveredM3 / 5f;
                                        // 水不能凭空出现：析出多少，就从房间里扣掉多少水汽。
                                        // 这一步才是真正的「除湿」，只产水不扣湿等于白送。
                                        AoBridge.AddMoistureDelta(map, vent, -excess * cellFraction);
                                        loop.CondensateKg += excess * cellFraction /
                                                             AirLoop.VaporPerKg(supplyCelsiusForWater);
                                    }
                                }
                            }
                        }

                        // 该出风口是有效送风口，凝结水就落在它脚下。
                        condensateSource = consumer.parent;

                        ApplyWind(consumer, ventScratch, net, loop);
                    }
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

                    // 面板读的是 CCC 的慢变量：原版每 TickRare（4.17 s）才更新一次，
                    // 而这里每 0.1~0.2 s 就写一次瞬时值。AO 的自适应步长本来就在
                    // 0.1 / 0.2 s 之间跳，直接赋值会让「总流速」看起来不断跳动。
                    // 做一阶低通，让读数回到原版那种稳定观感。
                    producer.CurrentAirFlow = producer.CurrentAirFlow <= 0f
                        ? airFlow
                        : Mathf.Lerp(producer.CurrentAirFlow, airFlow, CurrentAirFlowBlend);
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

            // 7. 凝结水出水。
            //
            // 水落在本轮有风送出的出风口脚下 —— 谁吹的风，谁滴水。
            // 没有可用出风口的这一轮不出水，凝结量继续攒着。
            // AO 那边每个 AO_Water 是 0.25 kg，EmitWater 会自己按这个粒度放。
            if (loop.CondensateKg >= 0.25f)
            {
                if (condensateSource != null && AoBridge.WaterAvailable && condensateSource.Spawned)
                {
                    loop.CondensateKg = AoBridge.EmitWater(condensateSource, loop.CondensateKg);
                }
                else if (loop.CondensateKg > 4f)
                {
                    // 出水口不可用时不要无限囤积，留一点零头就好。
                    loop.CondensateKg = 4f;
                }
            }
        }

        /// <summary>
        /// 取一台风机的进气采样格与进气温度。
        ///
        /// 与原版 Building_IntakeFan.TickRare 同一口径：用 def.Size 展开整圈外邻
        /// （GenAdj.CellsAdjacent8Way 不含建筑自身占位），取所有可读格的平均温度。
        /// 不能用「Position + 朝向」取单格：多格风机（LargeIntakeFan 是 2x2）的
        /// Position 是左下角，那一格仍属于建筑自身，读到的是建筑脚下的空气。
        /// </summary>
        private bool TrySampleIntake(CompAirFlowProducer producer, out IntVec3 cell, out float celsius)
        {
            cell = producer.parent.Position;
            celsius = 0f;

            int samples = 0;
            float sum = 0f;
            foreach (IntVec3 candidate in GenAdj.CellsAdjacent8Way(
                         producer.parent.Position, producer.parent.Rotation, producer.parent.def.Size))
            {
                if (!candidate.InBounds(map))
                {
                    continue;
                }

                float value;
                if (!AoBridge.TryGetTemperature(map, candidate, out value))
                {
                    continue;
                }

                if (samples == 0)
                {
                    cell = candidate;
                }

                sum += value;
                ++samples;
            }

            if (samples == 0)
            {
                // 外圈一格都读不到时退回建筑自身格，至少不让回路凭空断气。
                return AoBridge.TryGetTemperature(map, producer.parent.Position, out celsius);
            }

            celsius = sum / samples;
            return true;
        }

        /// <summary>重建风机状态。原版 TickRare 被拦截后，这几个字段没人再写。</summary>
        private void RefreshProducerState(CompAirFlowProducer producer)
        {
            CompPowerTrader power = producer.parent.GetComp<CompPowerTrader>();
            producer.IsPoweredOff = power != null && !power.PowerOn;
            producer.IsBrokenDown = producer.parent.IsBrokenDown();

            // 与原版 Building_IntakeFan.TickRare 同一判据：整圈外邻里任一格不可通行
            // 即视为堵塞。这里必须带上 def.Size —— 大入风口是 2x2，
            // 「Position + 朝向一格」落在自身占位内，而风机自身 passability = Impassable，
            // 会让它永久显示「设备入风堵塞」，并连带把全网进气量压成 0。
            bool blocked = false;
            foreach (IntVec3 candidate in GenAdj.CellsAdjacent8Way(
                         producer.parent.Position, producer.parent.Rotation, producer.parent.def.Size))
            {
                if (!candidate.InBounds(map))
                {
                    continue;
                }

                if (candidate.Impassable(map))
                {
                    blocked = true;
                    break;
                }
            }

            producer.IsBlocked = blocked;
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
        /// 给一个出风口写持续风矢量 + 径向递减的局部热源。
        ///
        /// 风走 AO 的 SourceSlots：体积源只写散度，投影法解出的是各向同性辐散，没有方向；
        /// SourceSlots 的 MaintainTargetWind 会把该格速度逐步拉向 TargetWind。
        ///
        /// 热也走同一个 SlotRates.TemperaturePerTick，但按「出风口最热、往外递减」分布：
        /// 总热量取自原版整房公式（roomDelta × 房间格数 ÷ 250），再按 1/(1+d) 权重
        /// 摊到出风口附近 BFS 出来的若干格上。这样总热量与原版一致，
        /// 空间上却是从出风口向外蔓延 —— AO 的平流（10 m/s 送风）会先把它吹成一条，
        /// 再靠扩散铺满房间，而不是瞬间整房同温。
        /// </summary>
        private void ApplyWind(CompAirFlowConsumer consumer, List<IntVec3> cells, AirFlowNet net, AirLoop loop)
        {
            Thing owner = consumer.parent;
            if (owner == null || owner.Destroyed)
            {
                return;
            }

            if (!AoBridge.ContinuousSourceAvailable)
            {
                return;
            }

            liveWindOwners.Add(owner);

            // 没有可出风的格，或断电、关闭、故障：把残留的风清零，
            // 否则 AO 会一直按上一次的目标速度吹。
            if (cells.Count == 0)
            {
                ClearWind(owner);
                return;
            }

            ThingWithComps holder = owner as ThingWithComps;
            CompPowerTrader power = holder != null ? holder.GetComp<CompPowerTrader>() : null;
            CompFlickable flick = holder != null ? holder.GetComp<CompFlickable>() : null;
            bool powered = power == null || power.PowerOn;
            bool switchedOn = flick == null || flick.SwitchIsOn;
            bool active = powered && switchedOn && !owner.IsBrokenDown();
            if (!active)
            {
                ClearWind(owner);
                return;
            }

            float airFlow = consumer.Props.baseAirExhaust *
                            Mathf.Max(0.01f, CentralizedClimateControlUtility.AirExhaustMultiplier);
            float speed = Mathf.Min(MaxWindSpeed, airFlow * WindSpeedPerAirFlowUnit);

            // 全周出风时总动量守恒：每格速度按 1/sqrt(N) 摊薄，N·v² 与单向时相同。
            // 单向时 N=1，退化成原来的整速。
            float perCellSpeed = speed / Mathf.Sqrt(cells.Count);
            diagWindSpeed = Mathf.Max(diagWindSpeed, perCellSpeed);

            // 出风口热源：总热量按原版整房公式取，但只落在出风口附近的几圈格子上。
            //
            // 原版 Building_AirVent.TickRare 是
            //   roomOrAdjacent.Temperature += GenTemperature.ControlTemperatureTempChange(口位, map, energy, 送风温度)
            // 每 250 tick 一次，等于给房间每一格加 roomDelta。AO 的 RoomTemperatureWriteHook
            // 会把这种写入摊到每一格，结果是整房同一瞬间一起升温 —— 不是我们要的观感。
            //
            // 这里保留同一个"总热量"口径（roomDelta × 房间格数），但摊到 250 tick 上，
            // 再按 1/(1+d) 权重撒在出风口外圈的格子。AO 的送风会先把热吹成一条，
            // 再靠扩散铺满房间，观感就是"从出风口缓慢蔓延"。
            float totalHeatPerTick = 0f;
            Room ventRoom = cells[0].GetRoom(map);
            if (ventRoom != null && !ventRoom.UsesOutdoorTemperature)
            {
                float supplyCelsius = loop.TemperatureK - 273.15f;

                // 「送风温度 - 格温」的符号必须单独乘进来。
                //
                // 原版 Building_AirVent.TickRare：
                //   float f  = ConvertedTemperature - GetTemperature(口位);
                //   float n1 = Mathf.Abs(f); if (n1 > 10f) n1 = 10f;
                //   float n2 = 1f; if (f < 0f) n2 = -1f;          // ← 符号
                //   float n5 = n1 * 0.25f * (ExhaustAirFlow/100f) * FlowEff² * thermalOk
                //              * 4.1666665f * 12f * n2;
                // 而 GenTemperature.ControlTemperatureTempChange 是靠 energyLimit 的正负
                // 选「加热」还是「制冷」分支的：
                //   return energyLimit <= 0f ? Mathf.Min(Mathf.Max(a, b), 0f)
                //                            : Mathf.Max(Mathf.Min(a, b), 0f);
                // 漏掉这个符号，制冷请求就被送进加热分支，算出来恒为 0 —— 只会热、不会冷。
                float diff = supplyCelsius - ventRoom.Temperature;
                float magnitude = Mathf.Min(Mathf.Abs(diff), 10f);
                if (diff < 0f)
                {
                    magnitude = -magnitude;
                }

                float flowEfficiency = Mathf.Clamp01(net.FlowEfficiency);
                float thermalOk = net.ThermalEfficiency > 0f ? 1f : 0f;
                float energy = magnitude * 0.25f * (airFlow / 100f) *
                               flowEfficiency * flowEfficiency * thermalOk *
                               4.1666665f * 12f;
                float roomDelta = GenTemperature.ControlTemperatureTempChange(
                    cells[0], map, energy, supplyCelsius);

                // roomDelta 是"整房每一格变这么多度"，总「格·度」= roomDelta × 格数。
                // 除以 250 把 TickRare 的一次性动作摊成每 tick 的连续源。
                // 制冷时 roomDelta 为负，这里同样要允许负值通过。
                totalHeatPerTick = roomDelta * Mathf.Max(1f, ventRoom.CellCount) / 250f * AoHeatScale;
            }

            CollectSpreadCells(cells, ventRoom, spreadCells, spreadWeights);

            IntVec3 center = owner.Position;

            int slot = 0;
            for (int i = 0; i < spreadCells.Count; i++)
            {
                IntVec3 cell = spreadCells[i];

                SlotRates rates = default(SlotRates);

                // 只有出风格本身给风；外圈格子只承担热量，风由平流自己带。
                if (cells.IndexOf(cell) >= 0)
                {
                    // 方向从建筑中心指向该格。墙挂型只有一格，等价于原来的正面朝向。
                    Vector2 direction = new Vector2(cell.x - center.x, cell.z - center.z);
                    float length = direction.magnitude;
                    bool hasDirection = length > 1e-4f;
                    if (hasDirection)
                    {
                        direction = direction / length;
                    }

                    rates.MaintainTargetWind = hasDirection && perCellSpeed > 0f;
                    rates.TargetWind = hasDirection ? direction * perCellSpeed : Vector2.zero;
                    rates.TargetWindBlendPerTick = WindBlendPerTick;
                }

                // 制冷时 totalHeatPerTick 为负，不能用 > 0f 过滤，否则只热不冷。
                if (totalHeatPerTick != 0f && i < spreadWeights.Count)
                {
                    rates.TemperaturePerTick = totalHeatPerTick * spreadWeights[i];
                }

                AoBridge.SetContinuousSource(owner.Map, owner, WindKey(owner, slot), cell, rates);
                ++slot;
            }

            // 出风格或扩散格变少时清掉多余槽位。
            int lastWritten;
            if (windWritten.TryGetValue(owner, out lastWritten))
            {
                for (int i = slot; i < lastWritten; i++)
                {
                    AoBridge.ZeroContinuousSourcesWithPrefix(owner.Map, owner, WindKey(owner, i));
                }
            }
            windWritten[owner] = slot;
        }

        /// <summary>每个出风口一个连续源，key 带本 mod 前缀与槽位号。</summary>
        private static string WindKey(Thing owner, int slot)
        {
            return "JAP.Wind." + owner.thingIDNumber + "." + slot;
        }

        /// <summary>清掉该出风口上次写过的全部风源（断电/关闭/无可出风格时）。</summary>
        private void ClearWind(Thing owner)
        {
            int written;
            if (!windWritten.TryGetValue(owner, out written) || written <= 0)
            {
                return;
            }
            Map map = owner.Map;
            if (map != null)
            {
                for (int i = 0; i < written; i++)
                {
                    AoBridge.ZeroContinuousSourcesWithPrefix(map, owner, WindKey(owner, i));
                }
            }
            windWritten[owner] = 0;
        }

        /// <summary>
        /// 收集一个出风口真正向哪几格送气。
        /// 墙挂型只有正面一格；Surround 型按 CCC 自己的 PlaceWorker 约定是八邻域全周。
        /// 原版 Building_AirVent.TickRare 只处理正面一格，与 Surround 的贴图和放置检查不一致，
        /// 这里按建筑本身的视觉语义补齐。
        /// </summary>
        private void CollectVentCells(CompAirFlowConsumer consumer, List<IntVec3> cells)
        {
            cells.Clear();

            Thing owner = consumer != null ? consumer.parent : null;
            if (owner == null || owner.Destroyed)
            {
                return;
            }

            if (IsSurroundVent(owner.def))
            {
                foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(owner.Position, owner.Rotation, owner.def.Size))
                {
                    if (cell.InBounds(map) && !cell.Impassable(map))
                    {
                        cells.Add(cell);
                    }
                }
                return;
            }

            IntVec3 vent = owner.Position + IntVec3Utility.RotatedBy(IntVec3.North, owner.Rotation);
            if (vent.InBounds(map) && !vent.Impassable(map))
            {
                cells.Add(vent);
            }
        }

        /// <summary>
        /// 从出风格出发 BFS，收集出风口附近若干圈内可通行、同房间的格子，并给出归一化权重。
        ///
        /// 权重 = 1 / (1 + d)，d 是到最近出风格的距离。出风格本身最高，往外递减，
        /// 归一化后总和为 1，于是总热量不变、只是按距离摊开，而不是整房同时升温。
        ///
        /// 只沿同一房间扩散：热不该顺着门口漏进走廊，也不该漏到室外。
        /// </summary>
        private void CollectSpreadCells(List<IntVec3> vents, Room room,
            List<IntVec3> cells, List<float> weights)
        {
            cells.Clear();
            weights.Clear();
            spreadVisited.Clear();
            spreadQueue.Clear();

            if (vents == null || vents.Count == 0)
            {
                return;
            }

            for (int i = 0; i < vents.Count; i++)
            {
                IntVec3 vent = vents[i];
                if (!vent.InBounds(map))
                {
                    continue;
                }

                int index = map.cellIndices.CellToIndex(vent);
                if (spreadVisited.ContainsKey(index))
                {
                    continue;
                }

                spreadVisited[index] = 0;
                spreadQueue.Add(index);
            }

            float weightSum = 0f;
            int head = 0;
            while (head < spreadQueue.Count && cells.Count < MaxSpreadCells)
            {
                int index = spreadQueue[head];
                ++head;
                int distance = spreadVisited[index];
                IntVec3 cell = map.cellIndices.IndexToCell(index);

                cells.Add(cell);
                float weight = 1f / (1f + distance * SpreadFalloffPerCell);
                weights.Add(weight);
                weightSum += weight;

                if (distance >= SpreadRadiusCells)
                {
                    continue;
                }

                for (int d = 0; d < GenAdj.AdjacentCells.Length; d++)
                {
                    IntVec3 next = cell + GenAdj.AdjacentCells[d];
                    if (!next.InBounds(map) || next.Impassable(map))
                    {
                        continue;
                    }

                    // 只在同一房间内扩散；room 为 null（室外型出风口）时不限。
                    if (room != null && next.GetRoom(map) != room)
                    {
                        continue;
                    }

                    int nextIndex = map.cellIndices.CellToIndex(next);
                    if (spreadVisited.ContainsKey(nextIndex))
                    {
                        continue;
                    }

                    spreadVisited[nextIndex] = distance + 1;
                    spreadQueue.Add(nextIndex);
                }
            }

            if (weightSum > 1e-6f)
            {
                for (int i = 0; i < weights.Count; i++)
                {
                    weights[i] /= weightSum;
                }
            }
        }

        /// <summary>
        /// 靠 PlaceWorker 而不是 defName 识别环绕型出风口。
        /// SurroundAirVent 与 SurroundAirVentWide 都用 PlaceWorker_SurroundAirVent，
        /// 墙挂型用 PlaceWorker_NeedsWall + PlaceWorker_AirVent。
        /// </summary>
        private static bool IsSurroundVent(ThingDef def)
        {
            return def != null &&
                   def.placeWorkers != null &&
                   def.placeWorkers.Contains(typeof(PlaceWorker_SurroundAirVent));
        }

        /// <summary>出风口被拆或改向后，注销它残留的连续源。</summary>
        private void PruneDeadWindOwners()
        {
            // 不能用「数量相等」早退：拆一个、加一个时数量不变但集合不同，
            // 漏掉的 owner 会让 AO 侧的风一直吹。
            deadWindOwners.Clear();
            foreach (KeyValuePair<Thing, int> pair in windWritten)
            {
                if (!liveWindOwners.Contains(pair.Key))
                {
                    deadWindOwners.Add(pair.Key);
                }
            }

            for (int i = 0; i < deadWindOwners.Count; i++)
            {
                Thing owner = deadWindOwners[i];
                // 先摘账本再注销：注销走反射，一旦抛异常，账本若还留着这个死 owner，
                // 它会被每 tick 重新选中，变成无限重复的报错。
                windWritten.Remove(owner);
                if (owner != null && owner.Map != null)
                {
                    AoBridge.UnregisterContinuousSources(owner.Map, owner);
                }
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
