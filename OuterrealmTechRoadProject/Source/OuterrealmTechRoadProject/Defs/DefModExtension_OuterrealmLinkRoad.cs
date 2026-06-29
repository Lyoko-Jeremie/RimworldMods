using Verse;

namespace OuterrealmTechRoadProject.Defs
{
    /// <summary>
    /// 挂在 RoadDef 上的本 Mod 专用扩展。
    /// RimWorld/RotR 原本只知道这是一条 RoadDef；这些字段用于标记“超维链路”的特殊规则。
    /// </summary>
    public class DefModExtension_OuterrealmLinkRoad : DefModExtension
    {
        /// <summary>
        /// 是否允许世界地图任意地形铺设。
        /// 当前设计中超维链路无视 biome、海洋、冰面和不可通行山脉限制。
        /// </summary>
        public bool allowAnyTerrain = true;

        /// <summary>
        /// 是否要求在特殊地形上按道路边通行。
        /// 当前版本先实现 tile 级放行；字段保留给后续 WorldPathing 边级限制 patch 使用。
        /// </summary>
        public bool strictEdgePassability = true;

        /// <summary>
        /// 原本不可通行的世界 tile 在拥有超维链路后使用的基础移动难度。
        /// 原版不可通行值是 1000；这里设为 4，让它比普通 RotR 跨海/穿山方案更快。
        /// </summary>
        public float impassableTileMovementDifficulty = 4f;
    }
}
