using RimWorld;
using Verse;

namespace OuterrealmTechRoadProject.DefOfs
{
    /// <summary>
    /// TerrainDef 的强类型入口，用于局部地图道路生成时放置超维链路路面。
    /// </summary>
    [DefOf]
    public static class OuterrealmTerrainDefOf
    {
        /// <summary>
        /// 超维链路在普通地形、冰面、山体清理后使用的局部地图地形。
        /// </summary>
        public static TerrainDef OuterrealmTech_OuterrealmLinkTerrain;

        static OuterrealmTerrainDefOf()
        {
            // 与 RoadDefOf 一样，字段名需要和 XML defName 保持一致。
            DefOfHelper.EnsureInitializedInCtor(typeof(OuterrealmTerrainDefOf));
        }
    }
}
