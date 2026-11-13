using System.Collections.Generic;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
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
