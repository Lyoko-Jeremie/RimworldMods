using OuterrealmTechRoadProject.DefOfs;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    /// <summary>
    /// 超维链路在局部地图中的道路生成步骤。
    /// 原版 GenStep_Roads 会沿世界道路路线采样一批格子，然后调用 RoadDef 的 roadGenSteps。
    /// 本类负责把这些格子转换成符合超维链路设定的地形。
    /// </summary>
    public class RoadDefGenStep_OuterrealmLinkPlace : RoadDefGenStep_Bulldoze
    {
        /// <summary>
        /// 普通地形、冰面、山地清理后使用的超维链路路面。
        /// 可在 XML 中覆盖；为空时使用本 Mod 的 DefOf。
        /// </summary>
        public TerrainDef linkTerrain;

        /// <summary>
        /// 水面上使用的桥梁地形。
        /// XML 默认指向 RotR 的 ConcreteBridge；如果缺失，会自动回退到原版/资料片桥梁。
        /// </summary>
        public TerrainDef bridgeTerrain;

        /// <summary>
        /// 在局部地图指定格子放置超维链路地形。
        /// </summary>
        public override void Place(
            Map map,
            IntVec3 position,
            TerrainDef rockDef,
            IntVec3 origin,
            GenStep_Roads.DistanceElement[,] distance)
        {
            if (!position.InBounds(map))
            {
                return;
            }

            // 先移除道路格子上的阻挡物，避免后续 SetTerrain 后仍被岩石墙或不可通行建筑堵住。
            ClearBlockingThings(map, position);
            TerrainDef terrain = position.GetTerrain(map);
            bool waterCoveredWorldTile = IsWaterCoveredWorldTile(map);

            if (waterCoveredWorldTile)
            {
                GenStep_OuterrealmLinkOceanMapFinalize.RegisterRoadCell(position);
            }

            // 不可通行山地按设计清出露天空旷直线：拆掉阻挡物、清除岩顶、铺超维链路路面。
            if (IsImpassableMountainMap(map))
            {
                ClearMountainCell(map, position, LinkTerrain);
                return;
            }

            // 水域世界 tile 的局部地图初始地形可能被原版回退成沙地；这里仍按海上道路处理为桥梁。
            if (waterCoveredWorldTile || terrain != null && terrain.IsWater)
            {
                map.roofGrid.SetRoof(position, null);
                map.fogGrid.Unfog(position);
                PlaceOuterrealmLinkOnBridge(map, position, BridgeTerrain, LinkTerrain);
                return;
            }

            // 其他地形，包括冰面、沼泽、普通陆地，都转换为超维链路路面。
            map.roofGrid.SetRoof(position, null);
            map.fogGrid.Unfog(position);
            map.terrainGrid.SetTerrain(position, LinkTerrain);
        }

        /// <summary>
        /// 实际使用的超维链路地形。
        /// </summary>
        private TerrainDef LinkTerrain
        {
            get
            {
                return linkTerrain ?? OuterrealmTerrainDefOf.OuterrealmTech_OuterrealmLinkTerrain;
            }
        }

        /// <summary>
        /// 实际使用的桥梁地形。
        /// 优先级：XML 指定地形 -> RotR ConcreteBridge -> Odyssey HeavyBridge -> 原版 Bridge。
        /// </summary>
        private TerrainDef BridgeTerrain
        {
            get
            {
                return BestAvailableBridgeFoundation(bridgeTerrain);
            }
        }

        /// <summary>
        /// 在桥基上铺设超维链路路面。
        /// RimWorld 1.6 中桥梁是 foundation，直接 SetTerrain(桥) 会只显示桥；
        /// 正确结构是 foundation=桥、top terrain=超维链路地板。
        /// </summary>
        public static void PlaceOuterrealmLinkOnBridge(Map map, IntVec3 position, TerrainDef bridgeTerrain, TerrainDef linkTerrain)
        {
            TerrainDef bridgeFoundation = BestAvailableBridgeFoundation(bridgeTerrain);
            TerrainDef outerrealmLinkTerrain = linkTerrain ?? OuterrealmTerrainDefOf.OuterrealmTech_OuterrealmLinkTerrain;

            if (bridgeFoundation != null && bridgeFoundation.isFoundation)
            {
                TerrainDef currentFoundation = map.terrainGrid.FoundationAt(position);
                if (currentFoundation != bridgeFoundation)
                {
                    if (currentFoundation != null)
                    {
                        map.terrainGrid.RemoveFoundation(position, false);
                    }

                    map.terrainGrid.SetFoundation(position, bridgeFoundation);
                }

                map.terrainGrid.SetTerrain(position, outerrealmLinkTerrain);
                return;
            }

            // 极端情况下如果没有可用的 foundation 桥梁，至少保留可见且可站立的超维链路路面。
            map.terrainGrid.SetTerrain(position, outerrealmLinkTerrain);
        }

        /// <summary>
        /// 选择可作为 foundation 的桥梁地形。
        /// 优先使用 XML 指定项；如果它不是 foundation，则回退到可分层承载地板的桥梁。
        /// </summary>
        public static TerrainDef BestAvailableBridgeFoundation(TerrainDef preferredBridgeTerrain)
        {
            if (preferredBridgeTerrain != null && preferredBridgeTerrain.isFoundation)
            {
                return preferredBridgeTerrain;
            }

            TerrainDef concreteBridge = DefDatabase<TerrainDef>.GetNamedSilentFail("ConcreteBridge");
            if (concreteBridge != null && concreteBridge.isFoundation)
            {
                return concreteBridge;
            }

            TerrainDef heavyBridge = DefDatabase<TerrainDef>.GetNamedSilentFail("HeavyBridge");
            if (heavyBridge != null && heavyBridge.isFoundation)
            {
                return heavyBridge;
            }

            return TerrainDefOf.Bridge;
        }

        /// <summary>
        /// 判断当前局部地图是否来自不可通行山地世界 tile。
        /// </summary>
        private static bool IsImpassableMountainMap(Map map)
        {
            SurfaceTile surfaceTile = Find.WorldGrid[map.Tile] as SurfaceTile;
            return surfaceTile != null && surfaceTile.hilliness == Hilliness.Impassable;
        }

        /// <summary>
        /// 判断当前局部地图是否来自水域覆盖的世界 tile。
        /// 原版海洋 tile 生成局部地图时可能先回退成沙地，所以不能只依赖当前格子的 TerrainDef.IsWater。
        /// </summary>
        private static bool IsWaterCoveredWorldTile(Map map)
        {
            SurfaceTile surfaceTile = Find.WorldGrid[map.Tile] as SurfaceTile;
            return surfaceTile != null && surfaceTile.WaterCovered;
        }

        /// <summary>
        /// 清理不可通行山地中的道路格子。
        /// 需求是“没有岩顶的空旷直线”，因此这里强制去掉屋顶并解除迷雾。
        /// </summary>
        private static void ClearMountainCell(Map map, IntVec3 position, TerrainDef terrain)
        {
            ClearBlockingThings(map, position);
            map.roofGrid.SetRoof(position, null);
            map.fogGrid.Unfog(position);
            map.terrainGrid.SetTerrain(position, terrain);
        }

        /// <summary>
        /// 清除道路格子上的不可通行阻挡物。
        /// 这个函数只处理当前道路格，不扫描全图，避免局部地图生成时造成额外性能负担。
        /// </summary>
        private static void ClearBlockingThings(Map map, IntVec3 position)
        {
            bool removed;
            do
            {
                removed = false;

                // 优先清 edifice。岩石墙、建筑墙等通常都在 edifice 层。
                Thing edifice = position.GetEdifice(map);
                if (edifice != null)
                {
                    edifice.Destroy(DestroyMode.Vanish);
                    removed = true;
                }

                if (!removed)
                {
                    var thingList = position.GetThingList(map);
                    for (int i = thingList.Count - 1; i >= 0; i--)
                    {
                        Thing thing = thingList[i];
                        // 只清除可摧毁且不可通行的物体，避免无意删除道路上的可通行物品。
                        if (thing.def.destroyable && thing.def.passability == Traversability.Impassable)
                        {
                            thing.Destroy(DestroyMode.Vanish);
                            removed = true;
                            break;
                        }
                    }
                }
            }
            while (removed && position.InBounds(map));
        }
    }
}
