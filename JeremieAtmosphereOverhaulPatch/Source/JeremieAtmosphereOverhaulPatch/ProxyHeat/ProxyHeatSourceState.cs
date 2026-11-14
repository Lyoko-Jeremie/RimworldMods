using System.Collections.Generic;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>一条喷流的入口/出口格与方向（已归一化）。</summary>
    public struct JetPort
    {
        public IntVec3 Inlet;
        public IntVec3 Outlet;
        public float DirX;
        public float DirY;
    }

    /// <summary>
    /// 单个 ProxyHeat 源的转译状态。
    ///
    /// 生命周期与本 Mod 的地图组件绑定：首次在模拟周期里见到该源时创建，
    /// 源被销毁后从字典里移除。**不参与存档**——读档后由
    /// <c>RecalculateAffectedCells</c> 的 Postfix 重新填 <see cref="RingCells"/>，
    /// 其余字段每周期都会重算。
    /// </summary>
    public sealed class ProxyHeatSourceState
    {
        /// <summary>ProxyHeat.CompTemperatureSource 实例（反射持有的 object）。</summary>
        public object Comp;

        /// <summary>源所属建筑。</summary>
        public Thing Parent;

        /// <summary>直接引用源自己的 AffectedCells，不复制。只读。</summary>
        public HashSet<IntVec3> Cells;

        /// <summary>
        /// 环形基线采样格：AffectedCells 中 Chebyshev 距离落在 [0.75r, r] 且不在本建筑上的格。
        /// 只在 <c>RecalculateAffectedCells</c> 之后重建。
        /// </summary>
        public List<IntVec3> RingCells = new List<IntVec3>();

        /// <summary>
        /// 喷流出口集：由建筑外扩一圈的周长格按「与建筑中心的极角」分桶，
        /// 每桶取角度最接近桶中心的那格，方向取该格真实的方向向量。
        ///
        /// 端口贴在建筑外围而不是半径外环：喷流的直觉是「从塔身向外喷」，
        /// 落在 0.75r~r 的外环上会让气流起点离建筑几十格，区域内反而没有风。
        /// 桶数按周长自适应，上限 24，避免大建筑注册过多 AirMover。
        /// </summary>
        public List<JetPort> Jets = new List<JetPort>();

        /// <summary>本源的 ProxyHeat 半径（Props.radius），用于按半径推算默认风速。</summary>
        public float Radius;

        /// <summary>构造这批喷口时用的层数，用于检测设置变化后重建。</summary>
        public int Layers = 1;

        /// <summary>
        /// 当前是否已向 AO 注册过喷流。因为我们用 activeTicks = -1（永不过期），
        /// 关掉开关后必须显式注销，这个标志用来避免每周期都去注销一遍。
        /// </summary>
        public bool JetRegistered;

        /// <summary>
        /// 上一次真正注册喷流时，每个端口的签名（速度、出风温度台阶、入口/出口格索引）。
        /// <c>InjectJets</c> 只在签名变化时才调 AO 的 <c>RegisterAirMover</c>，
        /// 避免每模拟周期对每个端口都做一次 AO 列表的线性查重（O(端口数 × 已注册数)）。
        /// 端口集在 <c>RefreshRing</c> 重建时置 null，强制下一次全部重注册。
        /// </summary>
        public float[] JetSignature;

        /// <summary>本周期重采样得到的环形基线温度（℃）。</summary>
        public float Baseline;

        /// <summary>本源的温差贡献：静态源 = tempOutcome；动态源 = TargetTemperature - Baseline。</summary>
        public float Outcome;

        /// <summary>逐 Def 上限（ProxyHeat 的 maxTemperature），null = 无上限。</summary>
        public float? Cap;

        /// <summary>逐 Def 下限（ProxyHeat 的 minTemperature），null = 无下限。</summary>
        public float? Floor;

        /// <summary>是否属于「制冷方向」的源（Building_Cooler，或 energyPerSecond &lt; 0 且不是 Building_Heater）。</summary>
        public bool IsCooler;

        /// <summary>是否已经应用过 defaultIndoorEffect 的初始值。</summary>
        public bool Initialized;
    }
}
