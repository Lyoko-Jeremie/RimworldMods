using HarmonyLib;
using OuterrealmTechRoadProject.World;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.Patches
{
    /// <summary>
    /// 修正超维链路海上营地的初始自然地形。
    /// 原版 Ocean biome 没有 terrainsByFertility，直接进入 GenStep_Terrain 会打印缺失地形错误；
    /// 这里只在拥有超维链路的水域地图上，把自然地形提前指定为临时沙地。
    /// </summary>
    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.GetNaturalTerrainAt))]
    public static class Patch_MapGenUtility_GetNaturalTerrainAt
    {
        private const string IsOuterrealmLinkOceanMapKey = "OuterrealmTechRoadProject_IsOuterrealmLinkOceanMap";

        /// <summary>
        /// 对超维链路海图跳过原版 TerrainFrom 查询，避免 Ocean biome 缺少自然地形表时刷红字。
        /// 这里必须返回可站立地形，供后续 GenStep_Roads 寻找道路中心和出口；
        /// 海洋收尾步骤会在道路生成后把非道路格统一改回深海。
        /// </summary>
        public static bool Prefix(IntVec3 cell, Map map, ref TerrainDef __result)
        {
            if (!IsOuterrealmLinkOceanMap(map))
            {
                return true;
            }

            __result = TerrainDefOf.Sand;
            return false;
        }

        /// <summary>
        /// 判断当前生成地图是否是拥有超维链路的水域世界 tile。
        /// 使用 MapGenerator 临时变量缓存结果，避免每个格子重复扫描世界道路。
        /// </summary>
        private static bool IsOuterrealmLinkOceanMap(Map map)
        {
            if (map == null || !map.Tile.Valid)
            {
                return false;
            }

            if (map == MapGenerator.mapBeingGenerated)
            {
                bool cachedResult;
                if (MapGenerator.TryGetVar(IsOuterrealmLinkOceanMapKey, out cachedResult))
                {
                    return cachedResult;
                }
            }

            SurfaceTile surfaceTile = Find.WorldGrid[map.Tile] as SurfaceTile;
            bool result = surfaceTile != null &&
                          surfaceTile.WaterCovered &&
                          OuterrealmLinkUtility.TileHasOuterrealmLink(map.Tile, out _);

            if (map == MapGenerator.mapBeingGenerated)
            {
                MapGenerator.SetVar(IsOuterrealmLinkOceanMapKey, result);
            }

            return result;
        }
    }
}
