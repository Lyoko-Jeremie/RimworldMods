using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 高维寻路网格：全图所有格子通行成本恒为 10。
    /// 无视地形（深水/浅水/山体/真空）、建筑（围墙/不可通行建筑）、天气与火焰成本，
    /// 使高维状态下的女仆可以穿过并停留在任意格子。
    /// </summary>
    public class HighDimPathGrid : PathGrid
    {
        public HighDimPathGrid(Map map, PathGridDef def) : base(map, def)
        {
        }

        public override int CalculatedCostAt(IntVec3 c, bool perceivedStatic, IntVec3 prevCell, int? baseCostOverride = null)
        {
            return 10;
        }
    }
}
