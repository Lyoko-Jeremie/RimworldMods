using System.Collections.Generic;
using System.Text;
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

        /// <summary>喷流注册的有效期（tick）。比模拟周期大得多，避免两次注入间失效。</summary>
        private const float JetActiveTicks = 60f;

        private readonly Dictionary<object, ProxyHeatSourceState> _states =
            new Dictionary<object, ProxyHeatSourceState>();

        /// <summary>逐建筑的「室内也生效」开关。只存显式开启的，未列出 = 关。</summary>
        private List<Thing> _indoorOn = new List<Thing>();

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
                RebuildModeCache();
                RebuildTuningCache();
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

        /// <summary>取某建筑的喷流速度（米/秒）。没有显式记录时用设置页默认值。</summary>
        public float GetJetSpeed(Thing parent)
        {
            int index;
            if (parent != null && _tuningCache.TryGetValue(parent, out index)
                && index >= 0 && index < _tunedJetSpeed.Count)
            {
                return _tunedJetSpeed[index];
            }
            AOPatchSettings settings = AOPatchMod.Settings;
            return settings != null ? settings.JetSpeedMetresPerSecond : 60f;
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
            return parent != null && _indoorOn.Contains(parent);
        }

        /// <summary>切换某建筑的「室内也生效」。返回切换后的状态。</summary>
        public bool ToggleIndoors(Thing parent)
        {
            if (parent == null)
            {
                return false;
            }
            if (_indoorOn.Contains(parent))
            {
                _indoorOn.Remove(parent);
                return false;
            }
            _indoorOn.Add(parent);
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
            if (settings != null && settings.DefaultIndoorEffect && !_indoorOn.Contains(state.Parent))
            {
                _indoorOn.Add(state.Parent);
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
            if (cells == null || cells.Count == 0 || state.Parent == null)
            {
                return;
            }

            object props = ProxyHeatBridge.GetProps(comp);
            float radius = ProxyHeatBridge.GetRadius(props);
            float inner = radius * RingInnerRatio;
            IntVec3 origin = state.Parent.Position;
            CellRect occupied = state.Parent.OccupiedRect();

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

            BuildJetPorts(state, radius, origin, occupied);
        }

        /// <summary>
        /// 由环形采样格构造喷流出口集：按与建筑中心的极角分桶，每桶保留最外侧的格，
        /// 方向取该格相对中心的真实单位向量。
        ///
        /// 桶数随半径自适应（约每 4 格弧长一个桶），下限 8、上限 24。
        /// 这样半径 3 的火堆有 8 条，半径 60 的塔也只要 24 条，不会注册上千个 AirMover。
        /// </summary>
        private static void BuildJetPorts(ProxyHeatSourceState state, float radius, IntVec3 origin, CellRect occupied)
        {
            state.Jets.Clear();
            int count = state.RingCells.Count;
            if (count == 0 || radius <= 0f)
            {
                return;
            }

            int buckets = Mathf.CeilToInt(6.2831855f * radius / 4f);
            if (buckets < 8)
            {
                buckets = 8;
            }
            if (buckets > 24)
            {
                buckets = 24;
            }

            // 每桶记录「距中心最远」的格；用平方距离比较，免开方。
            int[] bestIndex = new int[buckets];
            int[] bestDistSq = new int[buckets];
            for (int i = 0; i < buckets; i++)
            {
                bestIndex[i] = -1;
                bestDistSq[i] = -1;
            }

            float twoPi = 6.2831855f;
            float pi = 3.1415927f;
            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = state.RingCells[i];
                int dx = cell.x - origin.x;
                int dz = cell.z - origin.z;
                int distSq = dx * dx + dz * dz;
                if (distSq <= 0)
                {
                    continue;
                }
                float angle = (float)System.Math.Atan2(dz, dx);
                int bucket = (int)((angle + pi) / twoPi * buckets);
                if (bucket < 0)
                {
                    bucket = 0;
                }
                if (bucket >= buckets)
                {
                    bucket = buckets - 1;
                }
                if (distSq > bestDistSq[bucket])
                {
                    bestDistSq[bucket] = distSq;
                    bestIndex[bucket] = i;
                }
            }

            for (int b = 0; b < buckets; b++)
            {
                int i = bestIndex[b];
                if (i < 0)
                {
                    continue;
                }
                IntVec3 cell = state.RingCells[i];
                float vx = cell.x - origin.x;
                float vz = cell.z - origin.z;
                float len = Mathf.Sqrt(vx * vx + vz * vz);
                if (len <= 0.0001f)
                {
                    continue;
                }
                vx /= len;
                vz /= len;

                // 入口贴在出口格内侧一格，出口再往外两格；都用四舍五入后的整数方向，避免浮点抖动。
                IntVec3 step = new IntVec3(
                    Mathf.RoundToInt(vx),
                    0,
                    Mathf.RoundToInt(vz));
                if (step.x == 0 && step.z == 0)
                {
                    continue;
                }
                IntVec3 inlet = cell - step;
                IntVec3 outlet = cell + step + step;
                if (!inlet.InBounds(state.Parent.Map) || !outlet.InBounds(state.Parent.Map))
                {
                    continue;
                }
                if (occupied.Contains(inlet))
                {
                    continue;
                }

                state.Jets.Add(new JetPort
                {
                    Inlet = inlet,
                    Outlet = outlet,
                    DirX = vx,
                    DirY = vz,
                });
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
        public void Inject()
        {
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null || !settings.MasterEnabled || !settings.ProxyHeatRelayEnabled)
            {
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
            float[] pending = AtmosphereFieldBridge.GetPendingDeltas(_aoComponent);
            float[] ground = AtmosphereFieldBridge.GetGroundTemperature(engine);
            float[] obstacle = AtmosphereFieldBridge.GetObstacleMask(engine);
            if (temperature == null || pending == null)
            {
                DiagOnce("temperature=" + (temperature == null ? "null" : "ok")
                         + " pending=" + (pending == null ? "null" : "ok"));
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
            int dt = now - _lastInjectTick;
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
                if (state.Cells == null || state.Cells.Count == 0)
                {
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
                ProxyHeatMode modes = GetModes(parent);
                bool useAir = (modes & ProxyHeatMode.Air) != 0;
                bool useSoil = (modes & ProxyHeatMode.Soil) != 0 && soilAvailable;
                if (!useAir && !useSoil)
                {
                    continue;
                }
                modeOnCount++;

                bool indoors = _indoorOn.Contains(parent);
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

                    if (useAir)
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

            float airWritten = WriteChannel(
                _sumAir, _maxAir, _baselineAir, _capAir, _floorAir, _coolAir, _strengthAir, _touchedAir,
                pending, temperature, outdoor, stacking, cooling, maxCool, dt);

            float soilWritten = 0f;
            if (soilAvailable)
            {
                soilWritten = WriteChannel(
                    _sumSoil, _maxSoil, _baselineSoil, _capSoil, _floorSoil, _coolSoil, _strengthSoil,
                    _touchedSoil, ground, temperature, outdoor, stacking, cooling, maxCool, dt);
            }

            // ---- 喷流：逐建筑判断，向建筑四周的八个径向注册 AO 原生 AirMover
            bool jet = settings.JetModeEnabled && AtmosphereFieldBridge.AirMoverAvailable;
            if (jet)
            {
                InjectJets(_knownSources, outdoor);
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
                     " jet=" + jet +
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
            float outdoor, bool stacking, bool cooling, float maxCool, int dt)
        {
            float written = 0f;
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
                destination[index] += amount;
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
        /// 喷流通道：沿建筑外缘的 8 个径向，各注册一条 AO 原生 AirMover。
        ///
        /// 方向取「从建筑中心指向外侧」的固定八向，不跟建筑朝向走 —— 喷流是
        /// 热源向四周散开，不是定向吹风。风的强度、压升、排气温度全部交给
        /// AO 的模拟，我们只提供入口/出口格和 <c>ExhaustTemperatureFloor</c>。
        ///
        /// AO 的 <c>RegisterAirMover</c> 按 owner+key 就地更新，所以每个模拟周期
        /// 重复调用是安全的；不注册时旧记录会在 <paramref name="JetActiveTicks"/>
        /// 后自动过期，无需显式注销。
        /// </summary>
        private void InjectJets(System.Collections.IList comps, float outdoor)
        {
            if (!AtmosphereFieldBridge.AirMoverAvailable)
            {
                return;
            }
            AOPatchSettings settings = AOPatchMod.Settings;
            if (settings == null)
            {
                return;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                object comp = comps[i];
                if (comp == null || !ProxyHeatBridge.GetActive(comp))
                {
                    continue;
                }
                Thing parent = ProxyHeatBridge.GetParent(comp);
                if (parent == null || parent.Destroyed || !parent.Spawned)
                {
                    continue;
                }

                // 逐建筑：只有开了喷流通道的源才喷。
                if ((GetModes(parent) & ProxyHeatMode.Jet) == 0)
                {
                    continue;
                }

                ProxyHeatSourceState state;
                if (!_states.TryGetValue(comp, out state) || state.Outcome == 0f)
                {
                    continue;
                }

                float target = state.Baseline + state.Outcome;
                if (target <= outdoor)
                {
                    continue;
                }

                float speed = GetJetSpeed(parent);
                if (speed <= 0f)
                {
                    continue;
                }

                // 出口集在 RecalculateAffectedCells 的 Postfix 里按极角分桶建好，
                // 这里只做注册，不再算几何。
                List<JetPort> jets = state.Jets;
                for (int d = 0; d < jets.Count; d++)
                {
                    JetPort port = jets[d];
                    if (!port.Inlet.InBounds(map) || !port.Outlet.InBounds(map))
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
                        ExhaustTemperatureFloor = target,
                        ExhaustPressureRatio = 1.5f,
                        InletPressureRatio = 0.5f,
                        ExhaustOxygenPercent = 21f,
                        WaterKgPerTick = 0f,
                    };

                    AtmosphereFieldBridge.RegisterAirMover(
                        map, parent,
                        "aopjet:" + parent.thingIDNumber + ":" + d,
                        port.Inlet, port.Outlet, p, JetActiveTicks);
                }
            }
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

            _stampCounter = 0;
        }
    }
}
