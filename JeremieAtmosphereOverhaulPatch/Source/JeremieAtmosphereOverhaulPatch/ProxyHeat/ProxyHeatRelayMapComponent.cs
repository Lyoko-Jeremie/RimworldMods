using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>
    /// ProxyHeat 替代机制的运行时核心。
    ///
    /// 职责只有两件：
    /// 1. 每模拟周期（Postfix <c>AtmosphereMapComponent.PrepareTemperatureSources</c>）
    ///    把「范围效果」折算成温度增量，写进 AO 自己的
    ///    <c>pendingTemperatureDeltas</c>（空气）与 <c>engine.GroundTemperature</c>（地温）。
    /// 2. 保存逐建筑的「室内也生效」开关。
    ///
    /// **不做任何物理**：不积分、不扩散、不处理屋顶/气体耦合。
    /// 这些全部交给 AO 的 <c>ApplyGroundEnergy</c> / <c>ApplyTemperatureDeltas</c>。
    ///
    /// 线程安全：Postfix 执行时 AO 已释放 engineLock，且模拟 Task 尚未启动
    /// （<c>AtmosphereMapComponent.cs:2971</c> 在方法返回后才 StartNew），
    /// 所以这里对 pending 数组的写入没有竞态。
    /// </summary>
    public sealed class ProxyHeatRelayMapComponent : MapComponent
    {
        /// <summary>死区（℃）。温差小于它时线性减速，避免冲过头。</summary>
        private const float Deadband = 1f;

        /// <summary>环形基线的内边界比例。</summary>
        private const float RingInnerRatio = 0.75f;

        private const int RingCellMinimum = 4;

        /// <summary>喷流气柱高度（米）。参考 AO 自己的 CompPairedJet 默认值 5。</summary>
        private const float JetColumnHeight = 5f;

        /// <summary>
        /// 喷流注册的有效期。-1 表示永不过期，只在建筑被拆除时由 AO 的
        /// <c>PrepareAirMovers</c> 移除。
        ///
        /// 不能用正数：AO 的 <c>RegisterAirMover</c> 在参数变化时会先调
        /// <c>UpdateAirMover</c>，而后者会把 <c>LastTick</c> 重置为当前 tick，
        /// 导致本周期 <c>CaptureAirMover</c> 算出的 <c>ActiveTicks == 0</c>、不产生流量。
        /// 出风温度每周期都在漂移，若每周期都触发更新，喷流就永远没有风。
        /// </summary>
        private const float JetActiveTicks = -1f;

        /// <summary>
        /// 环形风源（ring TargetWind）的 key 前缀。与喷流（aopjet:）分开，
        /// 便于开关时只清本通道的源，不影响同一 owner 的其它连续源。
        /// </summary>
        private const string RingKeyPrefix = "aopring:";

        /// <summary>
        /// 环上每格目标风速相对喷流速度的系数。
        /// 喷流速度是「一条射线的出口风速」，环上每格面积更小，取一半避免风墙过强。
        /// </summary>
        private const float RingSpeedFactor = 0.5f;

        /// <summary>每 tick 把该格速度拉向径向目标的比例。AO 温控出风同款。</summary>
        private const float RingBlendPerTick = 0.1f;

        /// <summary>体积环每格每步的默认注入量（格体积），设置里可调；量级参考 Jets 的约 1.8 格体积。</summary>
        private const float DefaultVolumePerCell = 1.5f;

        /// <summary>体积链沿径向再推进的格数（链式注入，模拟 Jet 的路径体积分配）。</summary>
        private const int VolumeChainDepth = 2;

        /// <summary>体积链每前进一格的衰减系数。</summary>
        private const float VolumeChainFalloff = 0.6f;

        private readonly Dictionary<object, ProxyHeatSourceState> _states =
            new Dictionary<object, ProxyHeatSourceState>();

        /// <summary>逐建筑的「室内也生效」开关。只存显式开启的，未列出 = 关。</summary>
        private List<Thing> _indoorOn = new List<Thing>();

        /// <summary>
        /// <see cref="_indoorOn"/> 的查询索引。<c>Inject</c> 每个源每周期都要问一次
        /// 「这个源是否室内也生效」，List.Contains 是 O(_indoorOn.Count)，
        /// 在源循环里累积成 O(源数 × 开关数)。写成 HashSet 后查询 O(1)。
        /// 列表仍保留，因为 Scribe 的存档格式按 List 存。
        /// </summary>
        private readonly HashSet<Thing> _indoorOnSet = new HashSet<Thing>();

        /// <summary>逐建筑的通道开关（ProxyHeatMode 位掩码）。未列出 = 用设置里的默认值。</summary>
        private List<Thing> _modeThings = new List<Thing>();
        private List<int> _modeMasks = new List<int>();

        /// <summary>_modeThings/_modeMasks 的运行时索引，避免每次 gizmo 绘制都线性查找。</summary>
        private readonly Dictionary<Thing, int> _modeCache = new Dictionary<Thing, int>();

        // 空气与地暖各自一套格级聚合缓冲：同一格可能只被某个通道命中，
        // 不能像全局开关那样在第二遍统一决定写哪。
        private float[] _sumAir;
        private float[] _maxAir;
        private float[] _baselineAir;
        private float[] _capAir;
        private float[] _floorAir;
        private int[] _stampAir;
        private bool[] _coolAir;
        /// <summary>该格上命中空气通道的最强「℃/秒」设置，逐建筑强度用。</summary>
        private float[] _strengthAir;
        private readonly List<int> _touchedAir = new List<int>();

        private float[] _sumSoil;
        private float[] _maxSoil;
        private float[] _baselineSoil;
        private float[] _capSoil;
        private float[] _floorSoil;
        private int[] _stampSoil;
        private bool[] _coolSoil;
        /// <summary>该格上命中地暖通道的最强「℃/秒」设置。</summary>
        private float[] _strengthSoil;
        private readonly List<int> _touchedSoil = new List<int>();

        // 走公共 AddTemperatureDeltas 的批量缓冲（空气通道）。
        // 地温通道暂时仍直写 engine.GroundTemperature：AO 没有批量地温入口。
        private int[] _publicIndices;
        private float[] _publicDeltas;

        private readonly List<object> _deadKeys = new List<object>();

        /// <summary>
        /// 全量源名单。
        ///
        /// 不能用 ProxyHeat 的 <c>compTemperatures</c>：那个列表只收 active 的源，而 active
        /// 只有 <c>TempTick</c> 会维护。我们关掉 enableProxyHeat 后 TempTick 不再运行，
        /// 断电状态下生成的建筑会永久停在 active=false，既进不了 compTemperatures，
        /// 也就永远没人能把 active 修回来（Rebuild 见 active=false 直接 return）。
        /// 所以自己维护一份全量名单，每周期先修 active 再取照射区。
        /// </summary>
        private readonly List<object> _knownSources = new List<object>();

        /// <summary>诊断日志开关：前若干次注入 + 每 600 tick 打一条状态摘要。</summary>
        private int _injectCalls;
        private int _lastDiagTick;
        private int _toggleCalls;

        /// <summary>喷流端口诊断：前 8 条 + 每 600 tick 一条，避免每 tick 刷屏。</summary>
        private int _jetDiagCount;
        private int _lastJetDiagTick;

        /// <summary>环形风源诊断节流：前 8 条 + 每 600 tick 一条。</summary>
        private int _ringDiagCount;
        private int _lastRingDiagTick;

        /// <summary>端口构建诊断只打一次。</summary>
        private bool _portDiagDone;

        /// <summary>
        /// AO 侧喷流诊断字段缓存。只读反射，仅用于排查日志，不参与任何模拟逻辑。
        /// AO 的 PublishJetDiagnosis 把每个 inlet 的判定结果写进私有字典，
        /// 借此区分「端口不开放 / 无回流路径 / 轴向被挡」三种失败。
        /// </summary>
        private static bool _aoJetDiagResolved;
        private static System.Type _aoComponentType;
        private static System.Reflection.FieldInfo _aoJetDiagField;
        private static System.Reflection.FieldInfo _aoPairsField;
        private static System.Reflection.FieldInfo _aoClosedField;
        private static System.Reflection.FieldInfo _aoNoReturnField;
        private static System.Reflection.FieldInfo _aoBlockedField;
        private static System.Reflection.FieldInfo _aoRequestedField;
        private static System.Reflection.FieldInfo _aoAcceptedField;

        /// <summary>
        /// 逐建筑的参数覆盖。只存「玩家显式调过」的建筑，未列出 = 用设置页的默认值。
        /// 三条 List 按下标对齐（建筑 / 强度 ℃每秒 / 喷流速度 m每秒），
        /// <see cref="_tuningCache"/> 存「建筑 → 下标」。
        /// </summary>
        private List<Thing> _tunedThings = new List<Thing>();
        private List<float> _tunedStrength = new List<float>();
        private List<float> _tunedJetSpeed = new List<float>();
        private readonly Dictionary<Thing, int> _tuningCache = new Dictionary<Thing, int>();

        private int _stampCounter;
        private int _size;
        private int _width;
        private int _lastInjectTick;
        private int _lastCleanupTick;

        private object _aoComponent;

        public ProxyHeatRelayMapComponent(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _indoorOn, "japProxyHeatIndoors", LookMode.Reference);
            Scribe_Collections.Look(ref _modeThings, "japProxyHeatModeThings", LookMode.Reference);
            Scribe_Collections.Look(ref _modeMasks, "japProxyHeatModeMasks", LookMode.Value);
            Scribe_Collections.Look(ref _tunedThings, "japProxyHeatTunedThings", LookMode.Reference);
            Scribe_Collections.Look(ref _tunedStrength, "japProxyHeatTunedStrength", LookMode.Value);
            Scribe_Collections.Look(ref _tunedJetSpeed, "japProxyHeatTunedJetSpeed", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (_indoorOn == null)
                {
                    _indoorOn = new List<Thing>();
                }
                if (_modeThings == null)
                {
                    _modeThings = new List<Thing>();
                }
                if (_modeMasks == null)
                {
                    _modeMasks = new List<int>();
                }
                if (_tunedThings == null)
                {
                    _tunedThings = new List<Thing>();
                }
                if (_tunedStrength == null)
                {
                    _tunedStrength = new List<float>();
                }
                if (_tunedJetSpeed == null)
                {
                    _tunedJetSpeed = new List<float>();
                }
                RebuildIndoorCache();
                RebuildModeCache();
                RebuildTuningCache();
            }
        }

        /// <summary>读档后重建「室内也生效」索引；列表里可能有加载失败的 null。</summary>
        private void RebuildIndoorCache()
        {
            _indoorOnSet.Clear();
            for (int i = 0; i < _indoorOn.Count; i++)
            {
                Thing thing = _indoorOn[i];
                if (thing != null)
                {
                    _indoorOnSet.Add(thing);
                }
            }
        }

        /// <summary>读档后重建掩码索引；两条列表长度不一致时按较短的对齐，防止老存档错位。</summary>
        private void RebuildModeCache()
        {
            _modeCache.Clear();
            int count = _modeThings.Count < _modeMasks.Count ? _modeThings.Count : _modeMasks.Count;
            for (int i = 0; i < count; i++)
            {
                Thing thing = _modeThings[i];
                if (thing == null || _modeCache.ContainsKey(thing))
                {
                    continue;
                }
                // 缓存的是 _modeMasks 的下标，不是掩码本身；掩码可能随后被改写。
                _modeCache[thing] = i;
            }
        }

        /// <summary>读档后重建逐建筑参数索引。三条列表可能不等长，按最短的对齐。</summary>
        private void RebuildTuningCache()
        {
            _tuningCache.Clear();
            int count = _tunedThings.Count;
            if (_tunedStrength.Count < count)
            {
                count = _tunedStrength.Count;
            }
            if (_tunedJetSpeed.Count < count)
            {
                count = _tunedJetSpeed.Count;
            }
            for (int i = 0; i < count; i++)
            {
                Thing thing = _tunedThings[i];
                if (thing == null || _tuningCache.ContainsKey(thing))
                {
                    continue;
                }
                _tuningCache[thing] = i;
            }
        }

        // ------------------------------------------------------------ 逐建筑参数

        /// <summary>取某建筑的范围强度（℃/秒）。没有显式记录时用设置页默认值。</summary>
        public float GetStrength(Thing parent)
        {
            int index;
            if (parent != null && _tuningCache.TryGetValue(parent, out index)
                && index >= 0 && index < _tunedStrength.Count)
            {
                return _tunedStrength[index];
            }
            AOPatchSettings settings = AOPatchMod.Settings;
            return settings != null ? settings.RangeStrengthCelsiusPerSecond : 8f;
        }

        public void SetStrength(Thing parent, float value)
        {
            int index = EnsureTuningEntry(parent);
            if (index >= 0)
            {
                _tunedStrength[index] = value;
            }
        }

        /// <summary>
        /// 按建筑半径推算的初始风速（米/秒）。半径越大需要的出口速度越高，
        /// 否则气流推不到半径边缘；小半径（篝火）只需要很小的风。
        /// 这只是初值，玩家仍可在 gizmo 里逐建筑改。
        /// </summary>
        public static float AutoJetSpeed(float radius)
        {
            if (radius <= 0f)
            {
                return 40f;
            }
            // 半径 3 → 20，半径 26 → 39，半径 60 → 90，与 AO 自己 CompPairedJet 的 90 上限贴近。
            float speed = radius * 1.5f;
            if (speed < 20f)
            {
                speed = 20f;
            }
            if (speed > 150f)
            {
                speed = 150f;
            }
            return speed;
        }

        /// <summary>按建筑反查它的源状态。只在 gizmo 这类低频路径上用，别放进每周期循环。</summary>
        private bool TryGetStateFor(Thing parent, out ProxyHeatSourceState state)
        {
            state = null;
            if (parent == null)
            {
                return false;
            }
            foreach (KeyValuePair<object, ProxyHeatSourceState> pair in _states)
            {
                if (pair.Value.Parent == parent)
                {
                    state = pair.Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>取某建筑的喷流速度（米/秒）。没有显式记录时按该建筑的半径推算。</summary>
        public float GetJetSpeed(Thing parent, float radius)
        {
            int index;
            if (parent != null && _tuningCache.TryGetValue(parent, out index)
                && index >= 0 && index < _tunedJetSpeed.Count)
            {
                return _tunedJetSpeed[index];
            }
            return AutoJetSpeed(radius);
        }

        /// <summary>取某建筑的喷流速度，半径从它自己的源状态里取；没有状态时用 26 估算。</summary>
        public float GetJetSpeed(Thing parent)
        {
            ProxyHeatSourceState state;
            float radius = 26f;
            if (parent != null && TryGetStateFor(parent, out state) && state.Radius > 0f)
            {
                radius = state.Radius;
            }
            return GetJetSpeed(parent, radius);
        }

        public void SetJetSpeed(Thing parent, float value)
        {
            int index = EnsureTuningEntry(parent);
            if (index >= 0)
            {
                _tunedJetSpeed[index] = value;
            }
        }

        /// <summary>确保某建筑在三条调参列表里有一行，返回其下标。</summary>
        private int EnsureTuningEntry(Thing parent)
        {
            if (parent == null)
            {
                return -1;
            }
            int index;
            if (_tuningCache.TryGetValue(parent, out index))
            {
                if (index >= 0 && index < _tunedStrength.Count && index < _tunedJetSpeed.Count)
                {
                    return index;
                }
                // 缓存与列表不同步（极端情况下被裁剪过），重建后再试一次。
                RebuildTuningCache();
                if (_tuningCache.TryGetValue(parent, out index)
                    && index >= 0 && index < _tunedStrength.Count && index < _tunedJetSpeed.Count)
                {
                    return index;
                }
            }

            _tunedThings.Add(parent);
            _tunedStrength.Add(GetStrength(parent));
            _tunedJetSpeed.Add(GetJetSpeed(parent));
            index = _tunedThings.Count - 1;
            _tuningCache[parent] = index;
            return index;
        }

        /// <summary>清理已拆除建筑的调参行。</summary>
        private void TrimDeadTuningEntries()
        {
            int i = _tunedThings.Count - 1;
            bool removed = false;
            for (; i >= 0; i--)
            {
                Thing thing = _tunedThings[i];
                if (thing == null || thing.Destroyed)
                {
                    _tunedThings.RemoveAt(i);
                    _tunedStrength.RemoveAt(i);
                    _tunedJetSpeed.RemoveAt(i);
                    removed = true;
                }
                else
                {
                    break;
                }
            }
            if (removed)
            {
                RebuildTuningCache();
            }
        }

        // ------------------------------------------------------------ 逐建筑开关

        /// <summary>读某建筑当前的通道位掩码。没有显式记录时返回设置里的默认值。</summary>
        public ProxyHeatMode GetModes(Thing parent)
        {
            if (parent == null)
            {
                return ProxyHeatMode.None;
            }
            int index;
            if (_modeCache.TryGetValue(parent, out index)
                && index >= 0 && index < _modeMasks.Count)
            {
                return (ProxyHeatMode)_modeMasks[index];
            }
            return DefaultModes();
        }

        /// <summary>设置里的默认通道，只用于「还没有显式记录」的建筑。</summary>
        private static ProxyHeatMode DefaultModes()
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null)
            {
                return ProxyHeatMode.Air;
            }
            ProxyHeatMode modes = ProxyHeatMode.None;
            if (settings.AirModeEnabled)
            {
                modes |= ProxyHeatMode.Air;
            }
            if (settings.GroundModeEnabled)
            {
                modes |= ProxyHeatMode.Soil;
            }
            if (settings.JetModeEnabled)
            {
                modes |= ProxyHeatMode.Jet;
            }
            return modes;
        }

        public bool HasMode(Thing parent, ProxyHeatMode mode)
        {
            return (GetModes(parent) & mode) != 0;
        }

        /// <summary>切换某建筑的一个通道。返回切换后该通道是否开启。</summary>
        public bool ToggleMode(Thing parent, ProxyHeatMode mode)
        {
            if (parent == null)
            {
                return false;
            }
            ProxyHeatMode current = GetModes(parent);
            ProxyHeatMode next = (current & mode) != 0 ? current & ~mode : current | mode;

            int index;
            if (_modeCache.TryGetValue(parent, out index))
            {
                _modeMasks[index] = (int)next;
            }
            else
            {
                _modeThings.Add(parent);
                _modeMasks.Add((int)next);
                _modeCache[parent] = _modeMasks.Count - 1;
            }

            _toggleCalls++;
            if (_toggleCalls <= 20)
            {
                Diag("toggle " + (parent.def != null ? parent.def.defName : "?")
                     + " " + mode + " -> " + next + " (recorded=" + _modeThings.Count + ")");
            }
            return (next & mode) != 0;
        }

        public bool IncludesIndoors(Thing parent)
        {
            return parent != null && _indoorOnSet.Contains(parent);
        }

        /// <summary>切换某建筑的「室内也生效」。返回切换后的状态。</summary>
        public bool ToggleIndoors(Thing parent)
        {
            if (parent == null)
            {
                return false;
            }
            if (_indoorOnSet.Contains(parent))
            {
                _indoorOn.Remove(parent);
                _indoorOnSet.Remove(parent);
                return false;
            }
            _indoorOn.Add(parent);
            _indoorOnSet.Add(parent);
            return true;
        }

        /// <summary>首次见到某源时，按设置里的默认值决定是否进室内。</summary>
        private void ApplyDefaultIndoors(ProxyHeatSourceState state)
        {
            if (state.Initialized)
            {
                return;
            }
            state.Initialized = true;
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings != null && settings.DefaultIndoorEffect && !_indoorOnSet.Contains(state.Parent))
            {
                _indoorOn.Add(state.Parent);
                _indoorOnSet.Add(state.Parent);
            }
        }

        // ------------------------------------------------------------ 照射区同步

        /// <summary>
        /// 由 <c>CompTemperatureSource.RecalculateAffectedCells</c> 的 Postfix 调用。
        /// 重建环形基线采样格。此时 <c>AffectedCells</c> 已经填好。
        /// </summary>
        public void RefreshRing(object comp)
        {
            ProxyHeatSourceState state;
            if (!_states.TryGetValue(comp, out state))
            {
                return;
            }
            RefreshRing(state, comp);
        }

        private void RefreshRing(ProxyHeatSourceState state, object comp)
        {
            HashSet<IntVec3> cells = ProxyHeatBridge.GetAffectedCells(comp);
            state.Cells = cells;
            state.RingCells.Clear();
            state.Jets.Clear();
            // 端口集重建后旧的签名全部失效，下一次 InjectJets 必须重新注册。
            state.JetSignature = null;
            // 环形风源同样依赖建筑几何/朝向，照射区重建后必须重新构建。
            // 注意：不要在这里把 RingRegistered 置 false——关掉喷流时需要靠它
            // 判断「旧 key 是否需要清」，否则 RefreshRing 后再关开关会残留风源。
            state.WindRingCells.Clear();
            state.RingLayers = 0;
            if (cells == null || cells.Count == 0 || state.Parent == null)
            {
                return;
            }

            object props = ProxyHeatBridge.GetProps(comp);
            float radius = ProxyHeatBridge.GetRadius(props);
            float inner = radius * RingInnerRatio;
            CellRect occupied = state.Parent.OccupiedRect();
            // 多格建筑用中心而不是左下角，否则极角会整体偏一个建筑尺寸。
            IntVec3 origin = occupied.CenterCell;
            state.Radius = radius;

            foreach (IntVec3 cell in cells)
            {
                if (occupied.Contains(cell))
                {
                    continue;
                }
                int dx = cell.x - origin.x;
                int dz = cell.z - origin.z;
                if (dx < 0)
                {
                    dx = -dx;
                }
                if (dz < 0)
                {
                    dz = -dz;
                }
                int chebyshev = dx > dz ? dx : dz;
                if (chebyshev < inner || chebyshev > radius + 0.5f)
                {
                    continue;
                }
                state.RingCells.Add(cell);
            }

            // 端口来源要用 AO 的 ObstacleMask 校验出口是否开放；AO 未就绪时退化为几何判断。
            float[] obstacle = null;
            int width = 0;
            object aoComponent = AtmosphereFieldBridge.FindComponent(state.Parent.Map);
            if (aoComponent != null)
            {
                object engine = AtmosphereFieldBridge.GetEngine(aoComponent);
                if (engine != null)
                {
                    obstacle = AtmosphereFieldBridge.GetObstacleMask(engine);
                    width = AtmosphereFieldBridge.GetWidth(engine);
                }
            }
            int layers = 1;
            AOPatchSettings cfg = AOPatchMod.Settings;
            if (cfg != null)
            {
                layers = cfg.JetLayers;
            }
            state.Layers = layers;
            BuildJetPorts(state, radius, origin, occupied, state.Parent.Map, layers, obstacle, width);
            DiagPortBuild(state, occupied, obstacle, width);
        }

        /// <summary>
        /// 诊断：端口构建时 AO 障碍数组与占用区的真实状态。
        /// 用来判断「出口贴边 1 格」是不是 obstacle 为 null（跳过障碍检查）导致的。
        /// </summary>
        private void DiagPortBuild(ProxyHeatSourceState state, CellRect occupied, float[] obstacle, int width)
        {
            if (_portDiagDone)
            {
                return;
            }
            _portDiagDone = true;
            StringBuilder sb = new StringBuilder();
            sb.Append("ports-build layers=").Append(state.Layers)
              .Append(" obst=").Append(obstacle == null ? "null" : "len=" + obstacle.Length)
              .Append(" width=").Append(width)
              .Append(" cells=").Append(state.Cells != null ? state.Cells.Count : 0)
              .Append(" rect=").Append(occupied)
              .Append(" jets=").Append(state.Jets.Count);
            if (state.Jets.Count > 0)
            {
                JetPort p = state.Jets[0];
                sb.Append(" first=").Append(p.Inlet).Append("->").Append(p.Outlet);
                if (obstacle != null && width > 0)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        IntVec3 c = new IntVec3(p.Inlet.x, 0, p.Inlet.z - k);
                        int i = c.z * width + c.x;
                        if (i >= 0 && i < obstacle.Length)
                        {
                            sb.Append(" m").Append(k).Append('=').Append(obstacle[i].ToString("F2"));
                        }
                    }
                }
            }
            Diag(sb.ToString());
        }

        /// <summary>
        /// 由 ProxyHeat 的照射区构造喷流端口。
        ///
        /// 端口来源必须落在 <c>state.Cells</c>（AffectedCells）里：那张表已经过 FloodFill 连通性
        /// 和 GenSight 可见性筛选，被墙、山体挡住的格根本不在集合里。所以「格是否真的存在」由
        /// ProxyHeat 决定，我们只在存在的格里挑端口——被遮挡的方向自然不会生成喷口。
        ///
        /// 层数由设置给（1~3）。第 k 层的候选是「建筑外扩 k+1 圈的几何周长 ∩ AffectedCells」，
        /// 因此第 2 层紧贴第 1 层；除最后一层外，出口对准下一圈的同一方向，形成接力。
        /// </summary>
        private static void BuildJetPorts(
            ProxyHeatSourceState state,
            float radius,
            IntVec3 origin,
            CellRect occupied,
            Map map,
            int layers,
            float[] obstacle,
            int width)
        {
            state.Jets.Clear();
            if (radius <= 0f || map == null || state.Cells == null || state.Cells.Count == 0)
            {
                return;
            }
            if (layers < 1)
            {
                layers = 1;
            }
            if (layers > 3)
            {
                layers = 3;
            }

            // 最外层出口放到半径的一半；超过 40 格后收益递减，压住上限。
            float maxDist = Mathf.Clamp(radius * 0.5f, 3f, 40f);

            for (int layer = 0; layer < layers; layer++)
            {
                int ring = layer + 1;
                CellRect rect = occupied.ExpandedBy(ring);
                int perimeter = rect.Width * 2 + rect.Height * 2 - 4;
                // 周长 > 32 的巨型建筑抽稀成每 2 格一条，避免半径 100 的塔注册上百条。
                int stride = perimeter > 32 ? 2 : 1;

                // 中间层把出口对准下一圈（紧贴），最后一层放到 maxDist。
                float dist = layer + 1 < layers ? ring + 1 : maxDist;

                int index = 0;
                foreach (IntVec3 cell in EnumeratePerimeter(rect))
                {
                    if (index++ % stride != 0)
                    {
                        continue;
                    }
                    if (occupied.Contains(cell) || !cell.InBounds(map))
                    {
                        continue;
                    }
                    // 核心：端口必须真实存在于 ProxyHeat 的照射区。
                    if (!state.Cells.Contains(cell))
                    {
                        continue;
                    }

                    IntVec3 outlet;
                    float dirX;
                    float dirZ;
                    if (!TryFindOutlet(origin, cell, dist, map, occupied, obstacle, width, out outlet, out dirX, out dirZ))
                    {
                        continue;
                    }

                    state.Jets.Add(new JetPort
                    {
                        Inlet = cell,
                        Outlet = outlet,
                        DirX = dirX,
                        DirY = dirZ,
                    });
                }
            }
        }

        /// <summary>
        /// 从 inlet 沿「建筑中心 → inlet」的射线往外走，找第一个合法且开放的格作为出口。
        /// 一路全是墙或占用就返回 false，这条喷口不注册——省得 AO 那边注册完再静默清零。
        /// </summary>
        private static bool TryFindOutlet(
            IntVec3 origin,
            IntVec3 inlet,
            float dist,
            Map map,
            CellRect occupied,
            float[] obstacle,
            int width,
            out IntVec3 outlet,
            out float dirX,
            out float dirZ)
        {
            outlet = IntVec3.Invalid;
            dirX = 0f;
            dirZ = 0f;

            float vx = inlet.x - origin.x;
            float vz = inlet.z - origin.z;
            float len = Mathf.Sqrt(vx * vx + vz * vz);
            if (len <= 0.0001f)
            {
                return false;
            }
            vx /= len;
            vz /= len;

            int steps = Mathf.Max(1, Mathf.RoundToInt(dist));
            IntVec3 previous = inlet;
            for (int step = 1; step <= steps; step++)
            {
                IntVec3 candidate = new IntVec3(
                    Mathf.RoundToInt(origin.x + vx * (len + step)),
                    0,
                    Mathf.RoundToInt(origin.z + vz * (len + step)));
                if (candidate == previous)
                {
                    continue;
                }
                previous = candidate;
                if (!candidate.InBounds(map) || occupied.Contains(candidate))
                {
                    continue;
                }
                if (obstacle != null)
                {
                    int i = candidate.z * width + candidate.x;
                    if (i < 0 || i >= obstacle.Length || obstacle[i] <= 0f)
                    {
                        continue;
                    }
                }

                float dx = candidate.x - inlet.x;
                float dz = candidate.z - inlet.z;
                float dlen = Mathf.Sqrt(dx * dx + dz * dz);
                if (dlen <= 0.0001f)
                {
                    continue;
                }
                outlet = candidate;
                dirX = dx / dlen;
                dirZ = dz / dlen;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 按顺序枚举一个矩形的周长格：上边、下边、左边（去角）、右边（去角）。
        /// 顺序固定，供抽稀时按序取模，保证疏密均匀而不是随机丢格。
        /// </summary>
        private static IEnumerable<IntVec3> EnumeratePerimeter(CellRect rect)
        {
            int minX = rect.minX;
            int maxX = rect.maxX;
            int minZ = rect.minZ;
            int maxZ = rect.maxZ;
            for (int x = minX; x <= maxX; x++)
            {
                yield return new IntVec3(x, 0, minZ);
            }
            for (int x = minX; x <= maxX; x++)
            {
                yield return new IntVec3(x, 0, maxZ);
            }
            for (int z = minZ + 1; z <= maxZ - 1; z++)
            {
                yield return new IntVec3(minX, 0, z);
            }
            for (int z = minZ + 1; z <= maxZ - 1; z++)
            {
                yield return new IntVec3(maxX, 0, z);
            }
        }



        // ------------------------------------------------------------ 源登记

        /// <summary>
        /// 由 <c>CompTemperatureSource.PostSpawnSetup</c> 的 Postfix 调用。
        /// 新建筑（含读档重建）生成时把它的 comp 登记进全量名单。
        /// </summary>
        public void RegisterSource(object comp)
        {
            if (comp == null || _knownSources.Contains(comp))
            {
                return;
            }
            _knownSources.Add(comp);
            Thing parent = ProxyHeatBridge.GetParent(comp);
            if (parent == null)
            {
                return;
            }
            // 首次登记就修一次 active 并重建照射区，免得等到下一个模拟周期。
            if (RefreshActive(comp, parent))
            {
                ProxyHeatBridge.MarkDirty(comp);
            }
        }

        /// <summary>
        /// 兜底：PostSpawnSetup 补丁没挂上（或源在补丁安装前就已存在）时，
        /// 从 ProxyHeat 自己的两个列表里把能看到的源捞进全量名单。
        /// 注意 compTemperaturesToTick 只含有 power/fuel/resource/flickable 的建筑，
        /// 纯静态源（PassiveCooler、SteamGeyser 之类）只能靠 PostSpawnSetup 登记。
        /// </summary>
        private void SeedKnownSources(object manager)
        {
            SeedFromList(ProxyHeatBridge.GetActiveComps(manager));
            SeedFromList(ProxyHeatBridge.GetTickComps(manager));
        }

        private void SeedFromList(System.Collections.IList list)
        {
            if (list == null)
            {
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                object comp = list[i];
                if (comp != null && !_knownSources.Contains(comp))
                {
                    _knownSources.Add(comp);
                }
            }
        }

        private void PruneKnownSources()
        {
            for (int i = _knownSources.Count - 1; i >= 0; i--)
            {
                ThingComp comp = _knownSources[i] as ThingComp;
                if (comp == null || comp.parent == null || comp.parent.Destroyed || !comp.parent.Spawned)
                {
                    _knownSources.RemoveAt(i);
                }
            }
        }

        private void Diag(string message)
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.ProxyHeatDebugLog)
            {
                return;
            }
            Log.Message("[AOPatch/ProxyHeat] " + message);
        }

        /// <summary>只在开档后前几次注入输出，避免提前 return 的分支刷屏。</summary>
        private void DiagOnce(string message)
        {
            if (_injectCalls <= 5)
            {
                Diag(message);
            }
        }

        // ------------------------------------------------------------ 注入

        /// <summary>
        /// 由 Postfix <c>AtmosphereMapComponent.PrepareTemperatureSources</c> 调用。
        /// 每个模拟周期一次。
        /// </summary>
        public void Inject(int elapsedTicks = 0)
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.MasterEnabled || !settings.ProxyHeatRelayEnabled)
            {
                // 模块关掉时必须清掉已注册的喷流：它们 activeTicks = -1，不会自己过期。
                StopAllJets();
                DiagOnce("settings=null 或模块已关闭");
                return;
            }
            if (!ProxyHeatBridge.Available || !AtmosphereFieldBridge.Available)
            {
                DiagOnce("bridge unavailable: ProxyHeat=" + ProxyHeatBridge.Available
                         + " AO=" + AtmosphereFieldBridge.Available
                         + " (" + ProxyHeatBridge.Failure + " / " + AtmosphereFieldBridge.Failure + ")");
                return;
            }
            // 三模式现在是逐建筑的。全局开关只作为「还没有显式记录的建筑」的默认值，
            // 因此这里不能因为默认值全关就整体退出——玩家可能已经给某台机器单独开了。

            // ProxyHeat 的读值层必须保持关闭；玩家用其它途径改回时在这里兜住。
            if (ProxyHeatBridge.GetEnableProxyHeat())
            {
                ProxyHeatBridge.SetEnableProxyHeat(false);
            }

            _injectCalls++;

            int layers = settings.JetLayers;
            if (layers < 1)
            {
                layers = 1;
            }
            if (layers > 3)
            {
                layers = 3;
            }

            object manager = ProxyHeatBridge.FindManager(map);
            if (manager == null)
            {
                DiagOnce("manager=null（找不到 ProxyHeatManager）");
                return;
            }

            PruneKnownSources();
            if (_knownSources.Count == 0)
            {
                SeedKnownSources(manager);
            }
            if (_knownSources.Count == 0)
            {
                DiagOnce("sources=0（compTemperatures / compTemperaturesToTick 都为空，且 PostSpawnSetup 未登记）");
                return;
            }

            if (_aoComponent == null)
            {
                _aoComponent = AtmosphereFieldBridge.FindComponent(map);
                if (_aoComponent == null)
                {
                    DiagOnce("AtmosphereMapComponent=null");
                    return;
                }
            }

            object engine = AtmosphereFieldBridge.GetEngine(_aoComponent);
            if (engine == null)
            {
                DiagOnce("engine=null");
                return;
            }
            int size = AtmosphereFieldBridge.GetSize(engine);
            int width = AtmosphereFieldBridge.GetWidth(engine);
            if (size <= 0 || width <= 0)
            {
                DiagOnce("engine 尺寸异常 size=" + size + " width=" + width);
                return;
            }

            float[] temperature = AtmosphereFieldBridge.GetTemperatureRead(engine);
            // 空气通道已改走公共 AddTemperatureDeltas，pending 只作为旧回退，
            // 不再要求它存在（AO 重构内部字段时空气通道仍能工作）。
            float[] pending = AtmosphereFieldBridge.GetPendingDeltas(_aoComponent);
            float[] ground = AtmosphereFieldBridge.GetGroundTemperature(engine);
            float[] obstacle = AtmosphereFieldBridge.GetObstacleMask(engine);
            if (temperature == null)
            {
                DiagOnce("temperature=null");
                return;
            }

            EnsureBuffers(size, width);

            int now = Find.TickManager.TicksGame;
            if (_lastInjectTick == 0)
            {
                // 首次（含读档后）只记时间点，不注入，避免把「开档至今」的整段时间一次性补上。
                _lastInjectTick = now;
                return;
            }
            int dt = elapsedTicks > 0 ? elapsedTicks : now - _lastInjectTick;
            if (dt <= 0)
            {
                return;
            }
            // 读档、长暂停、调试快进之后 dt 可能非常大，限幅到 10 秒。
            if (dt > 600)
            {
                dt = 600;
            }
            _lastInjectTick = now;

            if (now - _lastCleanupTick > 600)
            {
                _lastCleanupTick = now;
                CleanupStates();
            }

            _stampCounter++;
            if (_stampCounter == int.MaxValue)
            {
                System.Array.Clear(_stampAir, 0, _stampAir.Length);
                System.Array.Clear(_stampSoil, 0, _stampSoil.Length);
                _stampCounter = 1;
            }
            int stamp = _stampCounter;
            _touchedAir.Clear();
            _touchedSoil.Clear();
            int activeCount = 0;
            int modeOnCount = 0;
            bool soilAvailable = ground != null;

            // ---- 第一遍：把每个源的目标温差累加到格
            for (int i = 0; i < _knownSources.Count; i++)
            {
                object comp = _knownSources[i];
                if (comp == null)
                {
                    continue;
                }
                Thing parent = ProxyHeatBridge.GetParent(comp);
                if (parent == null || parent.Destroyed || !parent.Spawned)
                {
                    continue;
                }

                // 必须先修 active 再判断：断电/断燃料/关开关造成的失活由我们同步回来，
                // enableProxyHeat=false 时 ProxyHeat 的 TempTick 不会替我们做。
                if (!RefreshActive(comp, parent))
                {
                    continue;
                }
                activeCount++;

                ProxyHeatSourceState state = GetOrCreateState(comp, parent);
                if (state.Cells == null || state.Cells.Count == 0 || state.Layers != layers)
                {
                    // 层数是玩家可调的，变了就重建端口（不影响照射区本身）。
                    RefreshRing(state, comp);
                    if (state.Cells == null || state.Cells.Count == 0)
                    {
                        continue;
                    }
                }

                RefreshOutcome(state, comp, temperature, obstacle, width, size);
                if (state.Outcome == 0f)
                {
                    continue;
                }

                // 逐建筑通道：同一格可能只被空气命中、只被地暖命中，或两个都要。
                //
                // 喷流只负责「风」：它的热量改由空气通道的面注入提供，这样热量均匀铺满
                // 整个照射区，不会走 AO 的 JetTransport 四向 path 交换（那会把热量画成十字）。
                // 所以「空气或喷流任一开启」都走空气通道，且同一源只累加一次。
                ProxyHeatMode modes = GetModes(parent);
                bool useAir = (modes & ProxyHeatMode.Air) != 0;
                bool useJet = (modes & ProxyHeatMode.Jet) != 0;
                bool useHeat = useAir || useJet;
                bool useSoil = (modes & ProxyHeatMode.Soil) != 0 && soilAvailable;
                if (!useHeat && !useSoil)
                {
                    continue;
                }
                modeOnCount++;

                bool indoors = _indoorOnSet.Contains(parent);
                float strength = GetStrength(parent);
                foreach (IntVec3 cell in state.Cells)
                {
                    if (!indoors && !cell.UsesOutdoorTemperature(map))
                    {
                        continue;
                    }
                    int index = cell.z * width + cell.x;
                    if (index < 0 || index >= size)
                    {
                        continue;
                    }
                    if (obstacle != null && obstacle[index] <= 0f)
                    {
                        continue;
                    }

                    if (useHeat)
                    {
                        Accumulate(
                            _sumAir, _maxAir, _baselineAir, _capAir, _floorAir, _coolAir,
                            _stampAir, _strengthAir, _touchedAir, index, stamp, state, strength);
                    }
                    if (useSoil)
                    {
                        Accumulate(
                            _sumSoil, _maxSoil, _baselineSoil, _capSoil, _floorSoil, _coolSoil,
                            _stampSoil, _strengthSoil, _touchedSoil, index, stamp, state, strength);
                    }
                }
            }

            // ---- 第二遍：按通道把目标温度折算成速率，分别写进 AO 的待应用数组
            float outdoor = map.mapTemperature.OutdoorTemp;
            bool stacking = settings.AllowTemperatureStacking;
            bool cooling = settings.AllowActiveCooling;
            float maxCool = settings.MaxCoolPerTick;

            int airCount;
            float airWritten = WriteChannel(
                _sumAir, _maxAir, _baselineAir, _capAir, _floorAir, _coolAir, _strengthAir, _touchedAir,
                pending, temperature, outdoor, stacking, cooling, maxCool, dt,
                _publicIndices, _publicDeltas, out airCount);
            if (airCount > 0)
            {
                // 走 AO 的公共批量入口：一次锁内累加整批，不再逐格碰内部数组。
                AoBridge.AddTemperatureDeltas(map, _publicIndices, _publicDeltas, airCount);
            }

            float soilWritten = 0f;
            if (soilAvailable)
            {
                int soilCount;
                soilWritten = WriteChannel(
                    _sumSoil, _maxSoil, _baselineSoil, _capSoil, _floorSoil, _coolSoil, _strengthSoil,
                    _touchedSoil, ground, temperature, outdoor, stacking, cooling, maxCool, dt,
                    null, null, out soilCount);
            }

            // 风驱动方式：0=环形 TargetWind（钉速度），1=体积环（注入空气，压力驱动），2=喷流（迁移前的 JetTransport）。
            // 体积环复用同一套环格几何，但走 AO 的散度通道，才能把风送到远场。
            int windDrive = AOPatchMod.Settings != null ? AOPatchMod.Settings.WindDriveMode : 0;
            if (windDrive == 1)
            {
                InjectWindVolumeRing(_knownSources, outdoor, obstacle, width);
            }
            else if (windDrive == 2 && AtmosphereFieldBridge.AirMoverAvailable)
            {
                InjectJets(_knownSources, outdoor, obstacle, temperature, width);
            }
            else if (AoBridge.ContinuousSourceAvailable)
            {
                InjectWindRing(_knownSources, outdoor, obstacle, width);
            }

            TrimDeadIndoorEntries();
            TrimDeadModeEntries();
            TrimDeadTuningEntries();

            if (_injectCalls <= 5 || now - _lastDiagTick > 600)
            {
                _lastDiagTick = now;
                Diag("sources=" + _knownSources.Count +
                     " active=" + activeCount +
                     " modeOn=" + modeOnCount +
                     " airTouched=" + _touchedAir.Count +
                     " soilTouched=" + _touchedSoil.Count +
                     " airWrite=" + airWritten.ToString("F2") +
                     " soilWrite=" + soilWritten.ToString("F2") +
                     " dt=" + dt +
                     " jet=" + AtmosphereFieldBridge.AirMoverAvailable +
                     " first=" + FirstSourcesSummary());
            }
        }

        /// <summary>把一个源的温差累加到某通道的格缓冲里。</summary>
        private static void Accumulate(
            float[] sum, float[] max, float[] baselineMax, float[] cap, float[] floor, bool[] cool,
            int[] stampArr, float[] strengthArr, List<int> touched, int index, int stamp,
            ProxyHeatSourceState state, float strength)
        {
            if (stampArr[index] != stamp)
            {
                stampArr[index] = stamp;
                touched.Add(index);
                sum[index] = 0f;
                max[index] = float.MinValue;
                baselineMax[index] = float.MinValue;
                cap[index] = float.NaN;
                floor[index] = float.NaN;
                cool[index] = false;
                strengthArr[index] = 0f;
            }

            sum[index] += state.Outcome;
            if (state.Outcome > max[index])
            {
                max[index] = state.Outcome;
            }
            if (state.Baseline > baselineMax[index])
            {
                baselineMax[index] = state.Baseline;
            }
            // 同一格被多个源覆盖时，用最强的那个源的速率，避免「弱源拖慢强源」。
            if (strength > strengthArr[index])
            {
                strengthArr[index] = strength;
            }
            if (state.Cap.HasValue)
            {
                float value = state.Cap.Value;
                if (float.IsNaN(cap[index]) || value < cap[index])
                {
                    cap[index] = value;
                }
            }
            if (state.Floor.HasValue)
            {
                float value = state.Floor.Value;
                if (float.IsNaN(floor[index]) || value > floor[index])
                {
                    floor[index] = value;
                }
            }
            if (state.IsCooler)
            {
                cool[index] = true;
            }
        }

        /// <summary>把某通道的格缓冲折算成增量写进 AO 数组，返回本通道写入总量。</summary>
        private static float WriteChannel(
            float[] sum, float[] max, float[] baselineMax, float[] cap, float[] floor, bool[] cool,
            float[] strengthArr, List<int> touched, float[] destination, float[] temperature,
            float outdoor, bool stacking, bool cooling, float maxCool, int dt,
            int[] collectIndices, float[] collectDeltas, out int collectCount)
        {
            float written = 0f;
            collectCount = 0;
            for (int i = 0; i < touched.Count; i++)
            {
                int index = touched[i];
                float current = temperature[index];

                float baseline = baselineMax[index];
                if (float.IsNaN(baseline))
                {
                    baseline = outdoor;
                }

                float target = baseline + (stacking ? sum[index] : max[index]);
                if (!float.IsNaN(cap[index]) && target > cap[index])
                {
                    target = cap[index];
                }
                if (!float.IsNaN(floor[index]) && target < floor[index])
                {
                    target = floor[index];
                }

                // 逐建筑强度：该格取命中它的最强源的 ℃/秒，再换算成每 tick。
                float ratePerTick = strengthArr[index] / 60f;
                float diff = target - current;
                float q;
                if (diff >= 0f)
                {
                    if (diff > Deadband)
                    {
                        diff = Deadband;
                    }
                    q = diff * ratePerTick;
                }
                else
                {
                    if (!cooling || !cool[index])
                    {
                        continue;
                    }
                    float negative = diff / Deadband;
                    if (negative < -maxCool)
                    {
                        negative = -maxCool;
                    }
                    q = negative;
                }
                if (q == 0f)
                {
                    continue;
                }

                float amount = q * dt;
                if (collectIndices != null)
                {
                    collectIndices[collectCount] = index;
                    collectDeltas[collectCount] = amount;
                    collectCount++;
                }
                else
                {
                    destination[index] += amount;
                }
                written += amount;
            }
            return written;
        }

        /// <summary>诊断用：列出前 3 个源的 defName / active / outcome / 照射格数。</summary>
        private string FirstSourcesSummary()
        {
            if (_knownSources.Count == 0)
            {
                return "-";
            }
            StringBuilder sb = new StringBuilder();
            int n = _knownSources.Count < 3 ? _knownSources.Count : 3;
            for (int i = 0; i < n; i++)
            {
                ThingComp member = _knownSources[i] as ThingComp;
                if (member == null || member.parent == null)
                {
                    sb.Append(" [null]");
                    continue;
                }
                ProxyHeatSourceState s;
                float outcome = 0f;
                int cells = -1;
                if (_states.TryGetValue(_knownSources[i], out s))
                {
                    outcome = s.Outcome;
                    cells = s.Cells == null ? -1 : s.Cells.Count;
                }
                sb.Append(" [").Append(member.parent.def.defName)
                  .Append(" act=").Append(ProxyHeatBridge.GetActive(_knownSources[i]) ? 1 : 0)
                  .Append(" mode=").Append((int)GetModes(member.parent))
                  .Append(" out=").Append(outcome.ToString("F0"))
                  .Append(" cells=").Append(cells)
                  .Append(']');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 喷流通道：沿建筑外缘一圈（必要时可叠层）逐格注册 AO 原生 AirMover。
        ///
        /// 方向取「从建筑中心指向该格」的真实径向，不跟建筑朝向走 —— 喷流是热源
        /// 向四周散开，不是定向吹风。风的强度、压升全部交给 AO 的模拟。
        ///
        /// 热量**不**从这里出：出风温度被设成出口格当前温度，让 JetTransport 的
        /// Mix/Exchange 没有温差可传，加热只由空气通道的面注入完成。这样既保留
        /// 风，又避免四条平行 path 把热量画成十字。
        ///
        /// AO 的 <c>RegisterAirMover</c> 按 owner+key 就地更新，所以每个模拟周期
        /// 重复调用是安全的。但**不能**依赖「不注册就自动过期」：我们用
        /// <c>activeTicks = -1</c>（永不过期），关掉开关时必须显式注销。
        /// </summary>
        private void InjectJets(System.Collections.IList comps, float outdoor, float[] obstacle, float[] temperature, int width)
        {
            if (!AtmosphereFieldBridge.AirMoverAvailable)
            {
                return;
            }
            if (AOPatchMod.Settings == null)
            {
                return;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                object comp = comps[i];
                if (comp == null)
                {
                    continue;
                }

                ProxyHeatSourceState state;
                if (!_states.TryGetValue(comp, out state) || state.Parent == null)
                {
                    continue;
                }

                Thing parent = state.Parent;
                if (parent.Destroyed || !parent.Spawned)
                {
                    // 建筑没了，AO 的 PrepareAirMovers 会自己清掉，这里不用管。
                    continue;
                }

                // 逐建筑：只有开了喷流通道、仍然活跃、且目标温度高于户外的源才喷。
                ProxyHeatMode modes = GetModes(parent);
                bool jetOn = (modes & ProxyHeatMode.Jet) != 0;
                bool wantJet =
                    ProxyHeatBridge.GetActive(comp) &&
                    jetOn &&
                    state.Outcome != 0f &&
                    state.Jets.Count > 0 &&
                    state.Baseline + state.Outcome > outdoor;

                // 速度只算一次：原来 wantJet 里一次、下面注册前又一次。
                float speed = 0f;
                if (wantJet)
                {
                    speed = GetJetSpeed(parent, state.Radius);
                    wantJet = speed > 0f;
                }

                // 诊断：只对显式开了喷流通道的建筑打印端口几何，避免全图源刷屏。
                // 前 8 条 + 每 600 tick 一条，用来区分「端口没建全」与「方向被判成对角」。
                if (jetOn)
                {
                    int now = Find.TickManager.TicksGame;
                    if (_jetDiagCount < 8 || now - _lastJetDiagTick > 600)
                    {
                        DiagJets(parent, state, speed, outdoor, wantJet);
                    }
                }

                if (!wantJet)
                {
                    StopJets(state);
                    continue;
                }

                float target = state.Baseline + state.Outcome;

                // 出口集在 RecalculateAffectedCells 的 Postfix 里按建筑外围一圈的
                // 周长格建好（可选叠层），这里只做注册，不再算几何。
                List<JetPort> jets = state.Jets;
                int signatureLength = jets.Count * 4;
                float[] signature = state.JetSignature;
                if (signature == null || signature.Length != signatureLength)
                {
                    signature = new float[signatureLength];
                    state.JetSignature = signature;
                }
                int mapWidth = width > 0 ? width : map.Size.x;
                for (int d = 0; d < jets.Count; d++)
                {
                    JetPort port = jets[d];
                    if (!port.Inlet.InBounds(map) || !port.Outlet.InBounds(map))
                    {
                        continue;
                    }
                    // 出口必须落在开放格上：AO 会在 ApplyJetTransportUnsafe 里用
                    // HasOpenPath 把走不通的喷流静默清零，与其注册完再被丢，不如这里跳过。
                    int outletIndex = port.Outlet.z * width + port.Outlet.x;
                    if (outletIndex < 0 || outletIndex >= obstacle.Length || obstacle[outletIndex] <= 0f)
                    {
                        continue;
                    }

                    // 出风温度取「出口格当前温度」，量化到 5℃ 台阶。
                    //
                    // 取当前温度 → JetTransport 里 outlet 的 Mix、以及沿 path 的 Exchange
                    // 都没有温差可传，热量不会沿 AO 的四向 path 画成十字；真正的加热由
                    // 空气通道的逐格面注入完成。
                    //
                    // 必须量化：RegisterAirMover 发现 Settings 有任何字段变化就会
                    // UpdateAirMover，而它会把 LastTick 重置到当前 tick，使本周期
                    // ActiveTicks 为 0、整条喷流不产生流量。温度场每周期都在小幅漂移，
                    // 不量化就会每周期都触发更新、永远没有风。
                    float outletTemp = (temperature != null && outletIndex < temperature.Length)
                        ? temperature[outletIndex]
                        : target;
                    float jetFloor = Mathf.Round(outletTemp / 5f) * 5f;

                    // 只在签名变化时注册。AO 的 RegisterAirMover 按 owner+key 线性扫
                    // airMovers，每周期每端口都调会退化成 O(端口数 × 已注册数)。
                    int sig = d * 4;
                    int inletCell = port.Inlet.z * mapWidth + port.Inlet.x;
                    int outletCell = port.Outlet.z * mapWidth + port.Outlet.x;
                    if (state.JetRegistered
                        && signature[sig] == speed
                        && signature[sig + 1] == jetFloor
                        && signature[sig + 2] == inletCell
                        && signature[sig + 3] == outletCell)
                    {
                        continue;
                    }

                    var p = new AtmosphereFieldBridge.AirMoverParams
                    {
                        CubicMetresPerTick = speed / 60f,
                        AirColumnHeightMetres = JetColumnHeight,
                        InletDirectionX = -port.DirX,
                        InletDirectionY = -port.DirY,
                        OutletDirectionX = port.DirX,
                        OutletDirectionY = port.DirY,
                        MaxPressureRisePascals = 1000000f,
                        ConditionExhaust = true,
                        ExhaustSpeed = speed,
                        ExhaustTemperatureFloor = jetFloor,
                        ExhaustPressureRatio = 1.5f,
                        InletPressureRatio = 0.5f,
                        ExhaustOxygenPercent = 21f,
                        WaterKgPerTick = 0f,
                    };

                    if (AtmosphereFieldBridge.RegisterAirMover(
                        map, parent,
                        "aopjet:" + parent.thingIDNumber + ":" + d,
                        port.Inlet, port.Outlet, p, JetActiveTicks))
                    {
                        signature[sig] = speed;
                        signature[sig + 1] = jetFloor;
                        signature[sig + 2] = inletCell;
                        signature[sig + 3] = outletCell;
                    }
                }

                state.JetRegistered = true;
            }
        }

        /// <summary>
        /// 诊断：列出某个已开喷流通道的建筑的端口几何与判定结果。
        /// 由 <see cref="InjectJets"/> 节流调用（前 8 条 + 每 600 tick 一条）。
        /// </summary>
        private void DiagJets(Thing parent, ProxyHeatSourceState state, float speed, float outdoor, bool wantJet)
        {
            _jetDiagCount++;
            _lastJetDiagTick = Find.TickManager.TicksGame;
            StringBuilder sb = new StringBuilder();
            sb.Append("jets ").Append(parent.def != null ? parent.def.defName : "?")
              .Append(" want=").Append(wantJet ? 1 : 0)
              .Append(" speed=").Append(speed.ToString("F2"))
              .Append(" ports=").Append(state.Jets != null ? state.Jets.Count : 0)
              .Append(" cells=").Append(state.Cells != null ? state.Cells.Count : 0)
              .Append(" radius=").Append(state.Radius)
              .Append(" base=").Append(state.Baseline.ToString("F1"))
              .Append(" out=").Append(state.Outcome.ToString("F1"))
              .Append(" outd=").Append(outdoor.ToString("F1"))
              .Append(" pos=").Append(parent.Position)
              .Append(" rot=").Append(parent.Rotation);
            List<JetPort> jets = state.Jets;
            if (jets != null && jets.Count > 0)
            {
                int n = jets.Count < 8 ? jets.Count : 8;
                for (int i = 0; i < n; i++)
                {
                    JetPort p = jets[i];
                    sb.Append(" [").Append(i).Append(':').Append(p.Inlet).Append("->").Append(p.Outlet)
                      .Append(" d=(").Append(p.DirX.ToString("F2")).Append(',').Append(p.DirY.ToString("F2")).Append(")]");
                }
                if (jets.Count > n)
                {
                    sb.Append(" +").Append(jets.Count - n);
                }
            }
            AppendAoJetDiagnosis(sb, parent, state);
            AppendJetDirectionSummary(sb, state);
            AppendJetWindSample(sb, parent, state);
            Diag(sb.ToString());
        }

        /// <summary>
        /// 诊断：沿各端口的出口方向采样风速，按 8 方向桶求平均。
        /// 直接回答「哪个方向真的有风」，避免再从代码推断。
        /// 采样点取 outlet 沿 Direction 的 1/4/7/10 格；走公共 TryGetWind（含插值），主线程安全。
        /// </summary>
        private static void AppendJetWindSample(StringBuilder sb, Thing parent, ProxyHeatSourceState state)
        {
            List<JetPort> jets = state.Jets;
            if (jets == null || jets.Count == 0 || parent == null || parent.Map == null)
            {
                return;
            }

            Map map = parent.Map;
            float[] sum = new float[8];
            int[] count = new int[8];
            string[] names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
            for (int i = 0; i < jets.Count; i++)
            {
                JetPort p = jets[i];
                float ang = Mathf.Atan2(p.DirY, p.DirX) * Mathf.Rad2Deg;
                int bucket = Mathf.RoundToInt((ang + 360f) % 360f / 45f) % 8;
                float total = 0f;
                int hits = 0;
                for (int step = 1; step <= 10; step += 3)
                {
                    IntVec3 cell = new IntVec3(
                        p.Outlet.x + Mathf.RoundToInt(p.DirX * step),
                        0,
                        p.Outlet.z + Mathf.RoundToInt(p.DirY * step));
                    if (!cell.InBounds(map))
                    {
                        continue;
                    }
                    Vector2 wind;
                    if (AtmosphereFieldBridge.TryGetWind(map, cell, out wind))
                    {
                        total += wind.magnitude;
                        hits++;
                    }
                }
                if (hits > 0)
                {
                    sum[bucket] += total / hits;
                    count[bucket]++;
                }
            }

            sb.Append(" wind[");
            for (int i = 0; i < 8; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(names[i]).Append('=');
                sb.Append(count[i] > 0 ? (sum[i] / count[i]).ToString("F1") : "-");
            }
            sb.Append(']');
        }

        /// <summary>
        /// 诊断：统计端口方向分布（8 桶）与出口距离，并列出较远的若干端口。
        /// 用来判断「只朝四角」是端口方向分布造成的，还是渲染层的问题。
        /// </summary>
        private static void AppendJetDirectionSummary(StringBuilder sb, ProxyHeatSourceState state)
        {
            List<JetPort> jets = state.Jets;
            if (jets == null || jets.Count == 0)
            {
                return;
            }

            int[] buckets = new int[8];
            string[] names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
            float distMin = float.MaxValue;
            float distMax = 0f;
            float distSum = 0f;
            int farListed = 0;
            sb.Append(" dirs[");
            for (int i = 0; i < jets.Count; i++)
            {
                JetPort p = jets[i];
                float ang = Mathf.Atan2(p.DirY, p.DirX) * Mathf.Rad2Deg;
                int bucket = Mathf.RoundToInt((ang + 360f) % 360f / 45f) % 8;
                buckets[bucket]++;

                float dx = p.Outlet.x - p.Inlet.x;
                float dz = p.Outlet.z - p.Inlet.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < distMin)
                {
                    distMin = d;
                }
                if (d > distMax)
                {
                    distMax = d;
                }
                distSum += d;

                if (d >= 10f && farListed < 8)
                {
                    if (farListed > 0)
                    {
                        sb.Append(' ');
                    }
                    sb.Append('[').Append(p.Inlet).Append("->").Append(p.Outlet)
                      .Append(" d=").Append(d.ToString("F1")).Append(']');
                    farListed++;
                }
            }
            for (int i = 0; i < 8; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(names[i]).Append('=').Append(buckets[i]);
            }
            sb.Append("] dist[min=").Append(distMin.ToString("F1"))
              .Append(" max=").Append(distMax.ToString("F1"))
              .Append(" avg=").Append((distSum / jets.Count).ToString("F1"))
              .Append(']');
        }

        /// <summary>
        /// 诊断：把 AO 侧 <c>jetDiagnosisPublished</c> 里本建筑各端口的判定结果读出来。
        /// 区分三种失败：端口不开放(closed)、无回流路径(noReturn)、轴向被挡(blocked)。
        /// </summary>
        private static void AppendAoJetDiagnosis(StringBuilder sb, Thing parent, ProxyHeatSourceState state)
        {
            if (!ResolveAoJetDiag() || parent == null || parent.Map == null || state.Jets == null || state.Jets.Count == 0)
            {
                return;
            }

            object component = parent.Map.GetComponent(_aoComponentType);
            if (component == null)
            {
                return;
            }

            object raw = _aoJetDiagField.GetValue(component);
            System.Collections.IDictionary dict = raw as System.Collections.IDictionary;
            if (dict == null)
            {
                return;
            }

            int closed = 0, noReturn = 0, blocked = 0, ok = 0, seen = 0, listed = 0;
            float requested = 0f, accepted = 0f;
            CellIndices indices = parent.Map.cellIndices;
            for (int i = 0; i < state.Jets.Count; i++)
            {
                JetPort port = state.Jets[i];
                int inletIndex = indices.CellToIndex(port.Inlet);
                if (!dict.Contains(inletIndex))
                {
                    continue;
                }

                object boxed = dict[inletIndex];
                int p = (int)_aoPairsField.GetValue(boxed);
                int c = (int)_aoClosedField.GetValue(boxed);
                int n = (int)_aoNoReturnField.GetValue(boxed);
                int b = (int)_aoBlockedField.GetValue(boxed);
                requested += (float)_aoRequestedField.GetValue(boxed);
                accepted += (float)_aoAcceptedField.GetValue(boxed);
                seen++;
                if (c > 0)
                {
                    closed++;
                }
                if (n > 0)
                {
                    noReturn++;
                }
                if (b > 0)
                {
                    blocked++;
                }
                if (p > 0 && c == 0 && n == 0 && b == 0)
                {
                    ok++;
                }
                if ((c > 0 || n > 0 || b > 0) && listed < 6)
                {
                    sb.Append(" [").Append(port.Inlet).Append(" c=").Append(c)
                      .Append(" n=").Append(n).Append(" b=").Append(b).Append(" p=").Append(p).Append(']');
                    listed++;
                }
            }

            sb.Append(" ao[seen=").Append(seen).Append(" ok=").Append(ok)
              .Append(" closed=").Append(closed).Append(" noRet=").Append(noReturn)
              .Append(" blocked=").Append(blocked)
              .Append(" req=").Append(requested.ToString("F1"))
              .Append(" acc=").Append(accepted.ToString("F1"))
              .Append(" f=").Append((requested > 0f ? accepted / requested : 0f).ToString("F2"))
              .Append(']');
        }

        private static bool ResolveAoJetDiag()
        {
            if (_aoJetDiagResolved)
            {
                return _aoJetDiagField != null;
            }

            _aoJetDiagResolved = true;
            try
            {
                _aoComponentType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
                if (_aoComponentType == null)
                {
                    return false;
                }

                _aoJetDiagField = AccessTools.Field(_aoComponentType, "jetDiagnosisPublished");
                if (_aoJetDiagField == null)
                {
                    return false;
                }

                System.Type diagType = _aoJetDiagField.FieldType.GetGenericArguments()[1];
                _aoPairsField = AccessTools.Field(diagType, "Pairs");
                _aoClosedField = AccessTools.Field(diagType, "PortClosed");
                _aoNoReturnField = AccessTools.Field(diagType, "NoReturnPath");
                _aoBlockedField = AccessTools.Field(diagType, "AxialBlocked");
                _aoRequestedField = AccessTools.Field(diagType, "Requested");
                _aoAcceptedField = AccessTools.Field(diagType, "Accepted");
            }
            catch (System.Exception ex)
            {
                Log.WarningOnce("[AOPatch/ProxyHeat] 解析 AO 喷流诊断失败：" + ex.GetType().Name + ": " + ex.Message, 0x4A11);
            }
            return _aoJetDiagField != null && _aoPairsField != null && _aoClosedField != null
                && _aoNoReturnField != null && _aoBlockedField != null
                && _aoRequestedField != null && _aoAcceptedField != null;
        }

        /// <summary>
        /// 停掉本图所有已注册的喷流。模块关闭、地图卸载时用。
        /// </summary>
        private void StopAllJets()
        {
            foreach (ProxyHeatSourceState state in _states.Values)
            {
                if (state.JetRegistered)
                {
                    StopJets(state);
                }
                if (state.RingRegistered || state.WindRingCells.Count > 0)
                {
                    StopRing(state);
                }
            }
        }

        /// <summary>
        /// 停掉一个源已经注册的喷流。只在它此前注册过时才真的去注销，
        /// 避免每周期对所有未开喷流的源都扫一遍 AO 的 airMovers 列表。
        /// </summary>
        private void StopJets(ProxyHeatSourceState state)
        {
            // 签名必须随注销一起清掉：否则下次重新开启时，端口/速度没变就会被
            // 误判为「已注册」而跳过 RegisterAirMover。
            state.JetSignature = null;
            if (!state.JetRegistered)
            {
                return;
            }
            state.JetRegistered = false;

            Thing parent = state.Parent;
            if (parent == null || parent.Destroyed || !parent.Spawned)
            {
                return;
            }
            AtmosphereFieldBridge.UnregisterAirMovers(map, parent);
        }

        // ------------------------------------------------------------ 环形风源（ring TargetWind）

        private static string RingPrefix(Thing parent)
        {
            return RingKeyPrefix + parent.thingIDNumber + ":";
        }

        /// <summary>
        /// 塔边最近 1~2 圈的环形风源。
        ///
        /// 为什么不用 <c>RegisterAirMover</c>：AO 的 JetTransport 只沿 4 条轴向走
        /// （<c>BuildPath</c> 对角方向取 Sign(dx)、忽略 dz），对角端口会借相邻
        /// 端口出口格接力，表现为「四角强、四面短」。这里改为在塔外第一/二圈
        /// 注册 TargetWind 连续源——它在 solver 之后应用
        /// （<c>ApplyTargetWindSourcesAfterSolverUnsafe</c>），不会被压力投影
        /// 当成散度吃掉；方向是连续的径向，外侧由 AO 的 advection/diffusion
        /// 自行演化。
        ///
        /// 关键约束：只在参数变化时注册，绝不每 tick 调 <c>SetContinuousSource</c>。
        /// 后者的 <c>UpdateContinuousSource</c> 会先 FlushContinuousIntervalToQueues，
        /// 把 TargetWind 退化成 Project 之前的速度增量（会被投影削弱），
        /// 并重置 <c>LastAppliedTick</c>。
        /// </summary>
        private void InjectWindRing(System.Collections.IList comps, float outdoor, float[] obstacle, int width)
        {
            if (!AoBridge.ContinuousSourceAvailable || AOPatchMod.Settings == null)
            {
                return;
            }

            int mapWidth = width > 0 ? width : map.Size.x;
            // 用户要求「最近一圈或最近两圈」；超过 2 圈交给 AO 的流场自己演化。
            int layers = Mathf.Clamp(AOPatchMod.Settings.JetLayers, 1, 2);

            for (int i = 0; i < comps.Count; i++)
            {
                object comp = comps[i];
                if (comp == null)
                {
                    continue;
                }

                ProxyHeatSourceState state;
                if (!_states.TryGetValue(comp, out state) || state.Parent == null)
                {
                    continue;
                }

                Thing parent = state.Parent;
                if (parent.Destroyed || !parent.Spawned)
                {
                    continue;
                }

                ProxyHeatMode modes = GetModes(parent);
                bool jetOn = (modes & ProxyHeatMode.Jet) != 0;
                bool want = ProxyHeatBridge.GetActive(comp)
                    && jetOn
                    && state.Outcome != 0f
                    && state.Baseline + state.Outcome > outdoor;

                float speed = want ? GetJetSpeed(parent, state.Radius) * RingSpeedFactor : 0f;
                want = want && speed > 0f;

                if (!want)
                {
                    StopRing(state);
                    continue;
                }

                CellRect occupied = parent.OccupiedRect();
                IntVec3 origin = occupied.CenterCell;

                bool rebuild = state.WindRingCells.Count == 0
                    || state.RingLayers != layers
                    || state.RingOrigin != origin
                    || state.RingRot != parent.Rotation;

                if (rebuild)
                {
                    BuildWindRing(state, parent, occupied, layers, obstacle, mapWidth);
                    state.RingLayers = layers;
                    state.RingOrigin = origin;
                    state.RingRot = parent.Rotation;
                    state.RingRegistered = false;
                    state.RingSpeed = 0f;
                }

                if (state.WindRingCells.Count == 0)
                {
                    StopRing(state);
                    continue;
                }

                if (state.RingRegistered && Mathf.Approximately(state.RingSpeed, speed))
                {
                    if (_ringDiagCount < 8 || Find.TickManager.TicksGame - _lastRingDiagTick > 600)
                    {
                        DiagRing(parent, state, speed, want);
                    }
                    continue;
                }

                RegisterWindRing(parent, state, speed);
                if (_ringDiagCount < 8 || Find.TickManager.TicksGame - _lastRingDiagTick > 600)
                {
                    DiagRing(parent, state, speed, want);
                }
            }
        }

        /// <summary>
        /// 构建塔外 1~2 圈的开放格。用 AO 的 ObstacleMask 而不是 ProxyHeat 的
        /// AffectedCells：后者已经做过连通性筛选，但「不假设塔周围可达」要求
        /// 我们只按空气格判定，让 AO 的流场自己绕开障碍。
        /// </summary>
        private void BuildWindRing(
            ProxyHeatSourceState state,
            Thing parent,
            CellRect occupied,
            int layers,
            float[] obstacle,
            int width)
        {
            state.WindRingCells.Clear();
            Map parentMap = parent.Map;
            for (int layer = 0; layer < layers; layer++)
            {
                CellRect rect = occupied.ExpandedBy(layer + 1);
                for (int z = rect.minZ; z <= rect.maxZ; z++)
                {
                    for (int x = rect.minX; x <= rect.maxX; x++)
                    {
                        // 只取矩形边界（外扩圈），内部已被上一层覆盖。
                        if (x != rect.minX && x != rect.maxX && z != rect.minZ && z != rect.maxZ)
                        {
                            continue;
                        }

                        IntVec3 cell = new IntVec3(x, 0, z);
                        if (!cell.InBounds(parentMap) || occupied.Contains(cell))
                        {
                            continue;
                        }

                        int index = cell.z * width + cell.x;
                        if (index < 0 || index >= width * parentMap.Size.z)
                        {
                            continue;
                        }
                        if (obstacle != null && (index >= obstacle.Length || obstacle[index] <= 0f))
                        {
                            continue;
                        }
                        // 建筑遮挡：以 HeatProxy 的 AffectedCells 为准（它已经做过 FloodFill 连通性
                        // 与遮挡筛选），环上落在有效格之外的格不喷。
                        if (state.Cells != null && state.Cells.Count > 0 && !state.Cells.Contains(cell))
                        {
                            continue;
                        }

                        state.WindRingCells.Add(cell);
                    }
                }
            }
        }

        /// <summary>把 ring 格注册成 TargetWind 连续源；先清旧 key 再全量写入。</summary>
        private void RegisterWindRing(Thing parent, ProxyHeatSourceState state, float speed)
        {
            Map parentMap = parent.Map;
            string prefix = RingPrefix(parent);
            AoBridge.RemoveContinuousSourcesWithPrefix(parentMap, parent, prefix);

            IntVec3 origin = state.RingOrigin;
            for (int i = 0; i < state.WindRingCells.Count; i++)
            {
                IntVec3 cell = state.WindRingCells[i];
                RegisterWindCell(parentMap, parent, prefix + i, origin, cell, speed);

                // 沿径向把矢量风再延伸到链上 2 格：四角比四边长的主因是轴向速度
                // 在 4 邻域扩散里被两侧建筑抹平，给轴向链格也钉住方向/速度即可抵消。
                float rx = cell.x - origin.x;
                float rz = cell.z - origin.z;
                float rlen = Mathf.Sqrt(rx * rx + rz * rz);
                if (rlen <= 0.0001f)
                {
                    continue;
                }
                float ux = rx / rlen;
                float uz = rz / rlen;
                for (int step = 1; step <= VolumeChainDepth; step++)
                {
                    IntVec3 chainCell = new IntVec3(
                        Mathf.RoundToInt(cell.x + ux * step), 0,
                        Mathf.RoundToInt(cell.z + uz * step));
                    if (chainCell == cell || state.Cells == null || !state.Cells.Contains(chainCell))
                    {
                        break;
                    }
                    RegisterWindCell(
                        parentMap, parent, prefix + i + "c" + step, origin,
                        chainCell, speed * Mathf.Pow(VolumeChainFalloff, step));
                }
            }

            state.RingRegistered = true;
            state.RingSpeed = speed;
        }

        /// <summary>给单格注册一条径向 TargetWind 源。</summary>
        private static void RegisterWindCell(
            Map map, Thing parent, string key, IntVec3 origin, IntVec3 cell, float speed)
        {
            float dx = cell.x - origin.x;
            float dz = cell.z - origin.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len <= 0.0001f)
            {
                return;
            }

            SlotRates rates = new SlotRates
            {
                MaintainTargetWind = true,
                TargetWind = new Vector2(dx / len, dz / len) * speed,
                TargetWindBlendPerTick = RingBlendPerTick,
            };
            AoBridge.SetContinuousSource(map, parent, key, cell, rates);
        }

        /// <summary>关掉喷流/降温停摆时注销 ring 源，避免塔边继续吹风。</summary>
        private void StopRing(ProxyHeatSourceState state)
        {
            bool had = state.RingRegistered;
            state.RingRegistered = false;
            state.RingSpeed = 0f;
            if (!had && state.WindRingCells.Count == 0)
            {
                return;
            }

            Thing parent = state.Parent;
            if (parent == null || parent.Destroyed || !parent.Spawned)
            {
                return;
            }
            AoBridge.RemoveContinuousSourcesWithPrefix(parent.Map, parent, RingPrefix(parent));
        }

        /// <summary>
        /// 体积环：复用 <see cref="BuildWindRing"/> 的环格几何，但每步向这些格注入体积源，
        /// 由 AO 的 <c>externalDivergence → Project</c> 解出压力驱动的径向流。
        ///
        /// 与环形 TargetWind 的区别：TargetWind 在 solver 之后钉速度，出环一步就被平流/投影/阻尼削掉；
        /// 体积源在 solver 之前制造散度，速度是从压力场解出来的，能传到远场。
        /// 体积源一次性提交（pendingPointVolumes 每步清空），不需要注册/注销 key。
        /// </summary>
        private void InjectWindVolumeRing(System.Collections.IList comps, float outdoor, float[] obstacle, int width)
        {
            if (AOPatchMod.Settings == null)
            {
                return;
            }

            int mapWidth = width > 0 ? width : map.Size.x;
            int layers = Mathf.Clamp(AOPatchMod.Settings.JetLayers, 1, 2);
            float baseVolumes = AOPatchMod.Settings.WindVolumePerCell > 0f
                ? AOPatchMod.Settings.WindVolumePerCell
                : DefaultVolumePerCell;

            for (int i = 0; i < comps.Count; i++)
            {
                object comp = comps[i];
                if (comp == null)
                {
                    continue;
                }

                ProxyHeatSourceState state;
                if (!_states.TryGetValue(comp, out state) || state.Parent == null)
                {
                    continue;
                }

                Thing parent = state.Parent;
                if (parent.Destroyed || !parent.Spawned)
                {
                    continue;
                }

                ProxyHeatMode modes = GetModes(parent);
                bool jetOn = (modes & ProxyHeatMode.Jet) != 0;
                bool want = ProxyHeatBridge.GetActive(comp)
                    && jetOn
                    && state.Outcome != 0f
                    && state.Baseline + state.Outcome > outdoor;

                if (!want)
                {
                    StopRing(state);
                    continue;
                }

                // 体积量随该塔的喷流速度缩放：用户调「喷流速度」时，环格 TargetWind 与
                // 体积驱动一起变，否则只改环格速度、环外体积不动，看起来「调了没反应」。
                float referenceSpeed = AutoJetSpeed(state.Radius);
                float tunedSpeed = GetJetSpeed(parent, state.Radius);
                float volumes = baseVolumes * (referenceSpeed > 0.1f
                    ? Mathf.Clamp(tunedSpeed / referenceSpeed, 0.25f, 3f)
                    : 1f);

                CellRect occupied = parent.OccupiedRect();
                IntVec3 origin = occupied.CenterCell;
                bool rebuild = state.WindRingCells.Count == 0
                    || state.RingLayers != layers
                    || state.RingOrigin != origin
                    || state.RingRot != parent.Rotation;

                if (rebuild)
                {
                    BuildWindRing(state, parent, occupied, layers, obstacle, mapWidth);
                    state.RingLayers = layers;
                    state.RingOrigin = origin;
                    state.RingRot = parent.Rotation;
                    state.RingRegistered = false;
                    state.RingSpeed = 0f;
                }

                if (state.WindRingCells.Count == 0)
                {
                    StopRing(state);
                    continue;
                }

                // 矢量风：体积源负责把压力场推出去（射程），TargetWind 负责在 solver 之后
                // 把环格速度钉在径向上（方向）。两者阶段不同、方向一致，叠加后出环的第一跳
                // 就有明确方向，不会被 Project 抹成各向同性；只注册一次，避免每步 Flush 退化。
                float ringSpeed = GetJetSpeed(parent, state.Radius) * RingSpeedFactor;
                if (!state.RingRegistered || !Mathf.Approximately(state.RingSpeed, ringSpeed))
                {
                    RegisterWindRing(parent, state, ringSpeed);
                }

                Map parentMap = parent.Map;
                int injected = 0;
                int rejected = 0;
                for (int c = 0; c < state.WindRingCells.Count; c++)
                {
                    IntVec3 cell = state.WindRingCells[c];
                    float celsius = outdoor;
                    AoBridge.TryGetTemperature(parentMap, cell, out celsius);
                    if (AoBridge.AddVolumeSourceAtTemperature(parentMap, cell, volumes, 21f, celsius))
                    {
                        injected++;
                    }
                    else
                    {
                        rejected++;
                    }
                }

                // 链式注入：环格之外沿径向再推 2 格，按 0.6 衰减继续灌体积，形成 AO Jet
                // 那种「沿路径逐格分配体积」的链式散度——单靠环格的话，压力波一过就散，
                // 环外 8/12 步只剩天然风。推进时以 AffectedCells 为边界，建筑遮挡即停。
                int chainCells = 0;
                if (state.Cells != null && state.Cells.Count > 0)
                {
                    IntVec3 ringOrigin = state.RingOrigin;
                    for (int c = 0; c < state.WindRingCells.Count; c++)
                    {
                        IntVec3 ringCell = state.WindRingCells[c];
                        float rx = ringCell.x - ringOrigin.x;
                        float rz = ringCell.z - ringOrigin.z;
                        float rlen = Mathf.Sqrt(rx * rx + rz * rz);
                        if (rlen <= 0.0001f)
                        {
                            continue;
                        }
                        float ux = rx / rlen;
                        float uz = rz / rlen;
                        for (int step = 1; step <= VolumeChainDepth; step++)
                        {
                            IntVec3 cell = new IntVec3(
                                Mathf.RoundToInt(ringCell.x + ux * step), 0,
                                Mathf.RoundToInt(ringCell.z + uz * step));
                            if (cell == ringCell || !state.Cells.Contains(cell))
                            {
                                break;
                            }
                            float celsius = outdoor;
                            AoBridge.TryGetTemperature(parentMap, cell, out celsius);
                            if (AoBridge.AddVolumeSourceAtTemperature(
                                    parentMap, cell, volumes * Mathf.Pow(VolumeChainFalloff, step), 21f, celsius))
                            {
                                chainCells++;
                            }
                        }
                    }
                }

                if (_ringDiagCount < 8 || Find.TickManager.TicksGame - _lastRingDiagTick > 600)
                {
                    _ringDiagCount++;
                    _lastRingDiagTick = Find.TickManager.TicksGame;
                    Diag("volring " + parent.def.defName
                        + " cells=" + state.WindRingCells.Count
                        + " vol=" + volumes.ToString("F3")
                        + " inj=" + injected + "/" + rejected
                        + " chain=" + chainCells
                        + " vec=" + (state.RingRegistered ? 1 : 0)
                        + " pos=" + parent.Position);
                    DiagRingDirs(parent, state);
                }
            }
        }

        /// <summary>诊断：环形风源的格数/层数/首格方向，前 8 条 + 每 600 tick 一条。</summary>
        private void DiagRing(Thing parent, ProxyHeatSourceState state, float speed, bool want)
        {
            _ringDiagCount++;
            _lastRingDiagTick = Find.TickManager.TicksGame;
            StringBuilder sb = new StringBuilder();
            sb.Append("ring ").Append(parent.def.defName)
              .Append(" want=").Append(want ? 1 : 0)
              .Append(" speed=").Append(speed.ToString("F1"))
              .Append(" cells=").Append(state.WindRingCells.Count)
              .Append(" layers=").Append(state.RingLayers)
              .Append(" reg=").Append(state.RingRegistered ? 1 : 0)
              .Append(" pos=").Append(parent.Position);
            if (state.WindRingCells.Count > 0)
            {
                IntVec3 c = state.WindRingCells[0];
                sb.Append(" first=").Append(c)
                  .Append(" d=(").Append((c.x - state.RingOrigin.x).ToString("F1"))
                  .Append(',').Append((c.z - state.RingOrigin.z).ToString("F1")).Append(')');
            }
            Diag(sb.ToString());
            DiagRingDirs(parent, state);
        }

        /// <summary>诊断用的 8 方向单位步进（E/NE/N/NW/W/SW/S/SE）。</summary>
        private static readonly int[] RingDiagDirX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] RingDiagDirZ = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly string[] RingDiagDirName = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };

        /// <summary>
        /// 诊断：沿 8 个方向各找一个环格，再沿同方向每 4 格采样一次实测风速（环格/4/8/12 步），
        /// 用来区分「环格注入不到位」与「AO 平流/投影把不同方向传播得不一样」。
        /// 只读，不写任何场。
        /// </summary>
        private void DiagRingDirs(Thing parent, ProxyHeatSourceState state)
        {
            Map map = parent.Map;
            if (map == null || state.WindRingCells.Count == 0)
            {
                return;
            }
            IntVec3 origin = state.RingOrigin;
            CellRect occ = parent.OccupiedRect();
            int reach = Mathf.Max(occ.Width, occ.Height) / 2 + Mathf.Max(1, state.RingLayers);
            StringBuilder sb = new StringBuilder("ringdir ");
            for (int k = 0; k < RingDiagDirX.Length; k++)
            {
                int dx = RingDiagDirX[k];
                int dz = RingDiagDirZ[k];
                string name = RingDiagDirName[k];
                IntVec3 ring = IntVec3.Invalid;
                for (int d = 1; d <= reach + 2; d++)
                {
                    IntVec3 probe = new IntVec3(origin.x + dx * d, 0, origin.z + dz * d);
                    if (probe.InBounds(map) && state.WindRingCells.Contains(probe))
                    {
                        ring = probe;
                        break;
                    }
                }
                if (!ring.IsValid)
                {
                    sb.Append(name).Append("=none ");
                    continue;
                }
                sb.Append(name).Append('(');
                AppendWindAt(sb, map, ring);
                for (int step = 4; step <= 12; step += 4)
                {
                    IntVec3 probe = new IntVec3(ring.x + dx * step, 0, ring.z + dz * step);
                    if (probe.InBounds(map))
                    {
                        sb.Append('/');
                        AppendWindAt(sb, map, probe);
                    }
                    else
                    {
                        sb.Append("/x");
                    }
                }
                sb.Append(") ");
            }
            Diag(sb.ToString());
        }

        /// <summary>把某格实测风速模长（m/s）写进诊断串；读不到记 ?。</summary>
        private static void AppendWindAt(StringBuilder sb, Map map, IntVec3 cell)
        {
            if (!AtmosphereFieldBridge.TryGetWind(map, cell, out Vector2 wind))
            {
                sb.Append('?');
                return;
            }
            float speed = Mathf.Sqrt(wind.x * wind.x + wind.y * wind.y);
            sb.Append(speed.ToString("F1"));
        }

        // ------------------------------------------------------------ 单源刷新

        /// <summary>
        /// 重采样环形基线并换算本源的温差贡献。
        ///
        /// 动态源（ProxyHeat 自动派生的 Building_TempControl）不能读
        /// <c>TemperatureOutcome</c>：AO 用 Prefix 短路了 TickRare，
        /// <c>RegisterRoomTemperatureChange</c> 永不触发，那个属性恒为 0。
        /// 改读 <c>CompTempControl.TargetTemperature</c>（绝对温度）。
        /// </summary>
        private void RefreshOutcome(
            ProxyHeatSourceState state,
            object comp,
            float[] temperature,
            float[] obstacle,
            int width,
            int size)
        {
            float sum = 0f;
            int count = 0;
            List<IntVec3> ring = state.RingCells;
            for (int i = 0; i < ring.Count; i++)
            {
                IntVec3 cell = ring[i];
                int index = cell.z * width + cell.x;
                if (index < 0 || index >= size)
                {
                    continue;
                }
                if (obstacle != null && obstacle[index] <= 0f)
                {
                    continue;
                }
                sum += temperature[index];
                count++;
            }
            state.Baseline = count >= RingCellMinimum ? sum / count : map.mapTemperature.OutdoorTemp;

            object props = ProxyHeatBridge.GetProps(comp);
            if (props == null)
            {
                state.Outcome = 0f;
                return;
            }

            state.Cap = ProxyHeatBridge.GetMaxTemperature(props);
            state.Floor = ProxyHeatBridge.GetMinTemperature(props);

            float? staticOutcome = ProxyHeatBridge.GetTempOutcome(props);
            if (staticOutcome.HasValue)
            {
                state.Outcome = staticOutcome.Value;
                state.IsCooler = staticOutcome.Value < 0f;
                return;
            }

            CompTempControl control = state.Parent.TryGetComp<CompTempControl>();
            if (control == null)
            {
                state.Outcome = 0f;
                state.IsCooler = false;
                return;
            }
            float targetTemperature = control.TargetTemperature;
            if (targetTemperature < -2000f)
            {
                state.Outcome = 0f;
                state.IsCooler = false;
                return;
            }
            CompProperties_TempControl controlProps = control.Props;
            state.IsCooler = state.Parent is Building_Cooler
                             || (controlProps != null && controlProps.energyPerSecond < 0f && !(state.Parent is Building_Heater));
            state.Outcome = targetTemperature - state.Baseline;
        }

        /// <summary>
        /// 自维护 active。
        ///
        /// 我们不调 <c>TempTick</c>（那会连带跑融雪，而且 enableProxyHeat=false 时它自己也会提前返回），
        /// 所以断电 / 断燃料 / 关开关这些状态变化不会有人替我们同步。
        /// 这里每周期比对一次 ProxyHeat 自己的判定，必要时改 active 并 MarkDirty 重建照射区。
        /// </summary>
        private bool RefreshActive(object comp, Thing parent)
        {
            bool active = ProxyHeatBridge.GetActive(comp);
            bool should = ProxyHeatBridge.GetShouldBeActive(comp);

            CompFlickable flickable = parent.TryGetComp<CompFlickable>();
            if (flickable != null && !flickable.SwitchIsOn)
            {
                should = false;
            }

            if (active == should)
            {
                return active;
            }

            ProxyHeatBridge.SetActive(comp, should);
            ProxyHeatBridge.MarkDirty(comp);
            return should;
        }

        private ProxyHeatSourceState GetOrCreateState(object comp, Thing parent)
        {
            ProxyHeatSourceState state;
            if (_states.TryGetValue(comp, out state))
            {
                state.Parent = parent;
                return state;
            }
            state = new ProxyHeatSourceState
            {
                Comp = comp,
                Parent = parent,
                Cells = ProxyHeatBridge.GetAffectedCells(comp),
            };
            _states[comp] = state;
            ApplyDefaultIndoors(state);
            RefreshRing(state, comp);
            return state;
        }

        private void TrimDeadIndoorEntries()
        {
            for (int i = _indoorOn.Count - 1; i >= 0; i--)
            {
                Thing thing = _indoorOn[i];
                if (thing == null || thing.Destroyed || !thing.Spawned)
                {
                    _indoorOn.RemoveAt(i);
                    if (thing != null)
                    {
                        _indoorOnSet.Remove(thing);
                    }
                }
            }
        }

        /// <summary>清掉已拆除建筑的通道记录。两条列表必须同步删，否则掩码会错位。</summary>
        private void TrimDeadModeEntries()
        {
            bool dirty = false;
            for (int i = _modeThings.Count - 1; i >= 0; i--)
            {
                Thing thing = _modeThings[i];
                if (thing != null && !thing.Destroyed && thing.Spawned)
                {
                    continue;
                }
                _modeThings.RemoveAt(i);
                if (i < _modeMasks.Count)
                {
                    _modeMasks.RemoveAt(i);
                }
                dirty = true;
            }
            if (dirty)
            {
                RebuildModeCache();
            }
        }

        /// <summary>
        /// 每 600 tick 清一次死源，避免 _states 与 _indoorOn 随建造/拆除无限增长。
        /// 不能边遍历边删，先收集 key 再删。
        /// </summary>
        private void CleanupStates()
        {
            if (_states.Count == 0)
            {
                return;
            }
            _deadKeys.Clear();
            foreach (KeyValuePair<object, ProxyHeatSourceState> pair in _states)
            {
                Thing parent = pair.Value.Parent;
                if (parent == null || parent.Destroyed || !parent.Spawned)
                {
                    _deadKeys.Add(pair.Key);
                }
            }
            for (int i = 0; i < _deadKeys.Count; i++)
            {
                _states.Remove(_deadKeys[i]);
            }
            _deadKeys.Clear();
        }

        private void EnsureBuffers(int size, int width)
        {
            if (_size == size && _width == width && _sumAir != null)
            {
                return;
            }
            _size = size;
            _width = width;

            _sumAir = new float[size];
            _maxAir = new float[size];
            _baselineAir = new float[size];
            _capAir = new float[size];
            _floorAir = new float[size];
            _stampAir = new int[size];
            _coolAir = new bool[size];
            _strengthAir = new float[size];
            _touchedAir.Clear();

            _sumSoil = new float[size];
            _maxSoil = new float[size];
            _baselineSoil = new float[size];
            _capSoil = new float[size];
            _floorSoil = new float[size];
            _stampSoil = new int[size];
            _coolSoil = new bool[size];
            _strengthSoil = new float[size];
            _touchedSoil.Clear();

            // 走公共 AddTemperatureDeltas 的批量缓冲：一次锁内写整批，避免逐格加锁。
            _publicIndices = new int[size];
            _publicDeltas = new float[size];

            _stampCounter = 0;
        }
    }
}
