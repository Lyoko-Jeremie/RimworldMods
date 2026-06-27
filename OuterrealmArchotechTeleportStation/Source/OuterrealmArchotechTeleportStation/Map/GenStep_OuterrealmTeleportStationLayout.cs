using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 传送站小地图的专用生成步骤。
    /// 它故意不调用完整遭遇地图生成流程，而是只铺设基础地形、生成一个小型 prefab、补道路并设置出生点，
    /// 以降低进入传送站地图时的加载成本。
    /// </summary>
    public class GenStep_OuterrealmTeleportStationLayout : GenStep
    {
        /// <summary>
        /// PrefabUtility.SpawnPrefab 可把生成出的 Thing 填进外部列表。
        /// 这里复用静态临时列表，避免每次地图生成时分配新的集合。
        /// </summary>
        private static readonly List<Thing> SpawnedThings = new List<Thing>();

        /// <summary>
        /// RimWorld 用 SeedPart 让不同 GenStep 在同一地图种子下获得稳定但互不干扰的随机序列。
        /// </summary>
        public override int SeedPart => 744391701;

        /// <summary>
        /// 执行传送站局部地图生成。
        /// 生成顺序：清理地图、选择 prefab、尝试生成 prefab、失败则 fallback、设置玩家出生点和清雾。
        /// </summary>
        public override void Generate(Map map, GenStepParams parms)
        {
            PrepareMap(map);

            IntVec3 center = map.Center;
            OuterrealmTeleportStationPrefabDef prefabDef = ChoosePrefab(map);
            Building portal = null;
            IntVec3 playerStart = IntVec3.Invalid;

            if (prefabDef != null && prefabDef.prefab != null)
            {
                // 优先使用 XML 定义的 prefab，方便后续只改 Def 就能扩展更多布局。
                TrySpawnPrefab(map, prefabDef, center, out portal, out playerStart);
            }

            if (portal == null)
            {
                // Prefab 缺失、被其他 Mod 改坏或放置失败时，使用 C# 保底布局确保地图仍然可用。
                portal = SpawnFallbackLayout(map, center);
                playerStart = center + new IntVec3(0, 0, -6);
            }

            if (!playerStart.IsValid || !playerStart.InBounds(map) || !playerStart.Standable(map))
            {
                // XML 配置的出生点可能被建筑占用；最终必须保证玩家单位有可站立落点。
                playerStart = CellFinder.StandableCellNear(portal.Position, map, 8f);
            }

            // 原版进入 MapParent 地图时会读取 MapGenerator.PlayerStartSpot 作为远行队入场参考。
            MapGenerator.PlayerStartSpot = playerStart;

            // 保留 rootsToUnfog，便于以后重新启用 Fog GenStep 时仍能围绕出生点和传送门揭示区域。
            MapGenerator.rootsToUnfog.Add(playerStart);
            MapGenerator.rootsToUnfog.Add(portal.Position);

            // 当前传送站地图极小且无遭遇内容，直接清雾能避免玩家进入后看不见传送门。
            map.fogGrid.ClearAllFog();
        }

        /// <summary>
        /// 准备一张干净、轻量的小地图。
        /// 全图使用 PackedDirt 作为底面并移除屋顶，避免原版自然地形/岩顶影响可达性。
        /// </summary>
        private static void PrepareMap(Map map)
        {
            foreach (IntVec3 cell in map.AllCells)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.PackedDirt);
                map.roofGrid.SetRoof(cell, null);
                List<Thing> things = cell.GetThingList(map);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    Thing thing = things[i];
                    if (thing.def.destroyable)
                    {
                        // GenStep 运行时地图上通常还没有复杂物件；这里是防御式清场，避免其他生成步骤留下阻挡物。
                        thing.Destroy();
                    }
                }
            }
        }

        /// <summary>
        /// 按生态群系过滤并按权重随机选择一个传送站 prefab 包装 Def。
        /// </summary>
        private static OuterrealmTeleportStationPrefabDef ChoosePrefab(Map map)
        {
            List<OuterrealmTeleportStationPrefabDef> prefabs = DefDatabase<OuterrealmTeleportStationPrefabDef>.AllDefsListForReading
                .Where(def => def.prefab != null && def.weight > 0f && def.AllowsBiome(map.Biome))
                .ToList();

            if (prefabs.Count == 0)
            {
                return null;
            }

            return prefabs.RandomElementByWeight(def => def.weight);
        }

        /// <summary>
        /// 尝试在地图中心生成指定 prefab。
        /// 该方法会先清理和铺设 prefab 占用区域，再通过原版 PrefabUtility 检查和生成。
        /// </summary>
        private static bool TrySpawnPrefab(
            Map map,
            OuterrealmTeleportStationPrefabDef prefabDef,
            IntVec3 center,
            out Building portal,
            out IntVec3 playerStart)
        {
            portal = null;
            playerStart = IntVec3.Invalid;
            Rot4 rot = PrefabUtility.ValidateRotation(prefabDef.prefab, Rot4.North);
            IntVec3 root = PrefabUtility.GetRoot(prefabDef.prefab, center, rot);
            CellRect occupied = new CellRect(root.x, root.z, prefabDef.prefab.size.x, prefabDef.prefab.size.z).ExpandedBy(3).ClipInsideMap(map);

            // 先给占用区域铺重型地面并清理墙体类阻挡，提升 CanSpawnPrefab 成功率。
            foreach (IntVec3 cell in occupied)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.MetalTile ?? TerrainDefOf.Concrete);
                GenSpawn.WipeExistingThings(cell, Rot4.North, ThingDefOf.Wall, map, DestroyMode.Vanish);
            }

            // 让原版工具做最终放置校验，避免 prefab 与边界、建筑占用等规则冲突。
            if (!PrefabUtility.CanSpawnPrefab(prefabDef.prefab, map, root, rot))
            {
                return false;
            }

            SpawnedThings.Clear();
            PrefabUtility.SpawnPrefab(prefabDef.prefab, map, root, rot, spawned: SpawnedThings);

            // 优先从生成列表中找主传送门，这是最可靠的定位方式。
            portal = SpawnedThings.OfType<Building>()
                .FirstOrDefault(thing => thing.def == OuterrealmDefOf.OuterrealmArchotechTeleportPortal);

            if (portal == null && prefabDef.portalOffset.IsValid)
            {
                // 如果生成列表没有捕获到建筑，则按 XML 里记录的 portalOffset 回查地图格。
                IntVec3 portalPos = root + prefabDef.portalOffset.ToIntVec3;
                portal = portalPos.GetFirstBuilding(map);
            }

            if (prefabDef.playerStartOffset.IsValid)
            {
                // 出生点先取 XML 配置，后续 Generate 会统一校验 standable。
                playerStart = root + prefabDef.playerStartOffset.ToIntVec3;
            }

            if (portal != null)
            {
                // prefab 只负责建筑群，通向地图边缘的道路由生成器根据实际地图尺寸补齐。
                EnsureRoadToEdge(map, portal.Position);
            }

            SpawnedThings.Clear();
            return portal != null;
        }

        /// <summary>
        /// C# 保底布局。
        /// 当 prefab 体系不可用时，仍生成一个中心平台、十字道路和主传送门，保证功能闭环不被 XML 布局破坏。
        /// </summary>
        private static Building SpawnFallbackLayout(Map map, IntVec3 center)
        {
            CellRect platform = CellRect.CenteredOn(center, 13, 13).ClipInsideMap(map);
            foreach (IntVec3 cell in platform)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.MetalTile ?? TerrainDefOf.Concrete);
            }

            EnsureRoadToEdge(map, center);
            return (Building)GenSpawn.Spawn(OuterrealmDefOf.OuterrealmArchotechTeleportPortal, center, map, Rot4.South);
        }

        /// <summary>
        /// 从中心点铺 3 格宽十字道路到地图四边。
        /// 这能保证传送门附近、玩家出生点和地图边缘之间有简单可靠的可达路径。
        /// </summary>
        private static void EnsureRoadToEdge(Map map, IntVec3 center)
        {
            CellRect mapRect = CellRect.WholeMap(map);
            for (int z = mapRect.minZ; z <= mapRect.maxZ; z++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    IntVec3 cell = new IntVec3(center.x + dx, 0, z);
                    if (cell.InBounds(map))
                    {
                        map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
                    }
                }
            }

            for (int x = mapRect.minX; x <= mapRect.maxX; x++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    IntVec3 cell = new IntVec3(x, 0, center.z + dz);
                    if (cell.InBounds(map))
                    {
                        map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
                    }
                }
            }
        }
    }
}
