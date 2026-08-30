using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 在 RimExodus 完成全部地图生成步骤后，把传送站核心区与每个接缝方向用硬质道路连通。
    /// 道路忽略原地形和障碍物：山体、水面、流沙、屋顶及阻挡物都会被清除并覆盖为铺装道路。
    /// </summary>
    internal static class RimExodusStationRoadUtility
    {
        private const string EnterSpotDefName = "RimExodus_SeamlessEnterSpot";
        private const string EnterSpotCompTypeName = "RimExodus.CompSeamlessTileEnterSpot";
        private const string VoidTerrainDefName = "RimExodus_Void";

        private static FieldInfo targetWorldTileField;
        private static bool targetFieldResolved;

        internal static void BuildGuaranteedRoads(Map map)
        {
            if (!RimExodusCompat.Active || map == null ||
                !(map.Parent is OuterrealmArchotechTeleportStationWorldObject))
            {
                return;
            }

            ThingDef enterSpotDef = DefDatabase<ThingDef>.GetNamedSilentFail(EnterSpotDefName);
            if (enterSpotDef == null)
            {
                Log.Error("[OuterrealmArchotechTeleportStation] RimExodus enter spot Def was not found; " +
                    "station boundary roads could not be generated.");
                return;
            }

            Dictionary<int, List<IntVec3>> spotsByTarget = CollectEnterSpots(map, enterSpotDef);
            if (spotsByTarget.Count == 0)
            {
                Log.Error("[OuterrealmArchotechTeleportStation] No RimExodus enter spots were found on teleport station map " +
                    map.uniqueID + ".");
                return;
            }

            IntVec3 start = FindRoadStart(map);
            if (!start.IsValid || !start.InBounds(map) || IsVoid(map, start))
            {
                Log.Error("[OuterrealmArchotechTeleportStation] Could not find a valid road start on teleport station map " +
                    map.uniqueID + ".");
                return;
            }

            int cellCount = map.cellIndices.NumGridCells;
            int[] distance = new int[cellCount];
            int[] parent = new int[cellCount];
            int[] queue = new int[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                distance[i] = -1;
                parent[i] = -1;
            }

            BuildVoidAvoidingSearchTree(map, start, distance, parent, queue);

            HashSet<IntVec3> roadCells = new HashSet<IntVec3>();
            Dictionary<int, IntVec3> selectedTargets = new Dictionary<int, IntVec3>();
            foreach (KeyValuePair<int, List<IntVec3>> pair in spotsByTarget)
            {
                IntVec3 target = SelectNearestReachedSpot(map, pair.Value, distance);
                if (!target.IsValid)
                {
                    Log.Error("[OuterrealmArchotechTeleportStation] No non-void route exists from the station to RimExodus " +
                        "neighbor tile " + pair.Key + " on map " + map.uniqueID + ".");
                    continue;
                }

                selectedTargets[pair.Key] = target;
                AppendThreeWidePath(map, start, target, parent, roadCells);
            }

            if (roadCells.Count == 0)
            {
                return;
            }

            foreach (IntVec3 cell in roadCells)
            {
                MakeHardRoadCell(map, cell, enterSpotDef);
            }

            // SetTerrain 会逐格更新局部寻路成本；生成结束后再统一全图刷新和重建区域，确保最终状态一致。
            map.pathing.RecalculateAllPerceivedPathCosts();
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();

            ValidateConnections(map, start, selectedTargets);
            Log.Message("[OuterrealmArchotechTeleportStation] Generated " + selectedTargets.Count +
                " hardened RimExodus boundary roads on station map " + map.uniqueID + ".");
        }

        private static Dictionary<int, List<IntVec3>> CollectEnterSpots(Map map, ThingDef enterSpotDef)
        {
            Dictionary<int, List<IntVec3>> result = new Dictionary<int, List<IntVec3>>();
            List<Thing> spots = map.listerThings.ThingsOfDef(enterSpotDef);
            for (int i = 0; i < spots.Count; i++)
            {
                ThingWithComps thing = spots[i] as ThingWithComps;
                if (thing == null)
                {
                    continue;
                }

                int targetWorldTile;
                if (!TryGetTargetWorldTile(thing, out targetWorldTile) || targetWorldTile < 0)
                {
                    continue;
                }

                List<IntVec3> group;
                if (!result.TryGetValue(targetWorldTile, out group))
                {
                    group = new List<IntVec3>();
                    result.Add(targetWorldTile, group);
                }

                group.Add(thing.Position);
            }

            return result;
        }

        private static bool TryGetTargetWorldTile(ThingWithComps thing, out int targetWorldTile)
        {
            targetWorldTile = -1;
            List<ThingComp> comps = thing.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp == null || comp.GetType().FullName != EnterSpotCompTypeName)
                {
                    continue;
                }

                if (!targetFieldResolved)
                {
                    targetWorldTileField = AccessTools.Field(comp.GetType(), "targetWorldTile");
                    targetFieldResolved = true;
                }

                if (targetWorldTileField == null)
                {
                    return false;
                }

                object value = targetWorldTileField.GetValue(comp);
                if (value is int)
                {
                    targetWorldTile = (int)value;
                    return true;
                }
            }

            return false;
        }

        private static IntVec3 FindRoadStart(Map map)
        {
            IntVec3 playerStart = MapGenerator.PlayerStartSpot;
            if (playerStart.IsValid && playerStart.InBounds(map) && !IsVoid(map, playerStart))
            {
                return playerStart;
            }

            List<Thing> portals = map.listerThings.ThingsOfDef(OuterrealmDefOf.OuterrealmArchotechTeleportPortal);
            if (portals.Count > 0)
            {
                IntVec3 portalPosition = portals[0].Position;
                for (int radius = 1; radius <= 8; radius++)
                {
                    foreach (IntVec3 cell in GenRadial.RadialCellsAround(portalPosition, radius, true))
                    {
                        if (!cell.InBounds(map) || IsVoid(map, cell))
                        {
                            continue;
                        }

                        Building building = cell.GetFirstBuilding(map);
                        if (building == null || building.def != OuterrealmDefOf.OuterrealmArchotechTeleportPortal)
                        {
                            return cell;
                        }
                    }
                }
            }

            return map.Center;
        }

        private static void BuildVoidAvoidingSearchTree(
            Map map,
            IntVec3 start,
            int[] distance,
            int[] parent,
            int[] queue)
        {
            int startIndex = map.cellIndices.CellToIndex(start);
            int head = 0;
            int tail = 0;
            queue[tail++] = startIndex;
            distance[startIndex] = 0;

            while (head < tail)
            {
                int currentIndex = queue[head++];
                IntVec3 current = map.cellIndices.IndexToCell(currentIndex);
                IntVec3[] directions = GenAdj.CardinalDirections;
                for (int i = 0; i < directions.Length; i++)
                {
                    IntVec3 next = current + directions[i];
                    if (!next.InBounds(map) || IsVoid(map, next))
                    {
                        continue;
                    }

                    int nextIndex = map.cellIndices.CellToIndex(next);
                    if (distance[nextIndex] >= 0)
                    {
                        continue;
                    }

                    distance[nextIndex] = distance[currentIndex] + 1;
                    parent[nextIndex] = currentIndex;
                    queue[tail++] = nextIndex;
                }
            }
        }

        private static IntVec3 SelectNearestReachedSpot(Map map, List<IntVec3> candidates, int[] distance)
        {
            IntVec3 result = IntVec3.Invalid;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                IntVec3 cell = candidates[i];
                if (!cell.InBounds(map) || IsVoid(map, cell))
                {
                    continue;
                }

                int value = distance[map.cellIndices.CellToIndex(cell)];
                if (value >= 0 && value < bestDistance)
                {
                    bestDistance = value;
                    result = cell;
                }
            }

            return result;
        }

        private static void AppendThreeWidePath(
            Map map,
            IntVec3 start,
            IntVec3 target,
            int[] parent,
            HashSet<IntVec3> roadCells)
        {
            int startIndex = map.cellIndices.CellToIndex(start);
            int currentIndex = map.cellIndices.CellToIndex(target);
            while (currentIndex >= 0)
            {
                IntVec3 center = map.cellIndices.IndexToCell(currentIndex);
                IntVec3[] offsets = GenAdj.AdjacentCellsAndInside;
                for (int i = 0; i < offsets.Length; i++)
                {
                    IntVec3 roadCell = center + offsets[i];
                    if (roadCell.InBounds(map) && !IsVoid(map, roadCell))
                    {
                        roadCells.Add(roadCell);
                    }
                }

                if (currentIndex == startIndex)
                {
                    break;
                }

                currentIndex = parent[currentIndex];
            }
        }

        private static void MakeHardRoadCell(Map map, IntVec3 cell, ThingDef enterSpotDef)
        {
            if (!cell.InBounds(map) || IsVoid(map, cell))
            {
                return;
            }

            map.terrainGrid.RemoveTempTerrain(cell, false, true);
            ClearBlockingThings(map, cell, enterSpotDef, false);
            map.roofGrid.SetRoof(cell, null);
            map.snowGrid.SetDepth(cell, 0f);
            map.sandGrid?.SetDepth(cell, 0f);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);

            if (!cell.Standable(map))
            {
                // 防御性第二遍：若第三方 Thing 的 passability 定义异常，清除除 Pawn 和关键传送设施外的全部 Thing。
                ClearBlockingThings(map, cell, enterSpotDef, true);
                map.terrainGrid.RemoveTempTerrain(cell, false, true);
                map.roofGrid.SetRoof(cell, null);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
            }
        }

        private static void ClearBlockingThings(Map map, IntVec3 cell, ThingDef enterSpotDef, bool clearAll)
        {
            List<Thing> things = cell.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing thing = things[i];
                if (thing == null || thing is Pawn ||
                    thing.def == OuterrealmDefOf.OuterrealmArchotechTeleportPortal ||
                    thing.def == enterSpotDef)
                {
                    continue;
                }

                bool blocksRoad = clearAll ||
                    thing.def.category == ThingCategory.Building ||
                    thing.def.category == ThingCategory.Plant ||
                    thing.def.IsBlueprint ||
                    thing.def.IsFrame ||
                    thing.def.passability != Traversability.Standable;
                if (!blocksRoad)
                {
                    continue;
                }

                if (thing.def.destroyable)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
                else if (thing.Spawned)
                {
                    thing.DeSpawn();
                }
            }
        }

        private static void ValidateConnections(Map map, IntVec3 start, Dictionary<int, IntVec3> targets)
        {
            int cellCount = map.cellIndices.NumGridCells;
            bool[] visited = new bool[cellCount];
            int[] queue = new int[cellCount];
            int head = 0;
            int tail = 0;

            if (!start.Standable(map))
            {
                Log.Error("[OuterrealmArchotechTeleportStation] Hardened station road start is not standable on map " +
                    map.uniqueID + ".");
                return;
            }

            int startIndex = map.cellIndices.CellToIndex(start);
            queue[tail++] = startIndex;
            visited[startIndex] = true;
            while (head < tail)
            {
                IntVec3 current = map.cellIndices.IndexToCell(queue[head++]);
                IntVec3[] directions = GenAdj.CardinalDirections;
                for (int i = 0; i < directions.Length; i++)
                {
                    IntVec3 next = current + directions[i];
                    if (!next.InBounds(map) || !next.Standable(map))
                    {
                        continue;
                    }

                    int index = map.cellIndices.CellToIndex(next);
                    if (!visited[index])
                    {
                        visited[index] = true;
                        queue[tail++] = index;
                    }
                }
            }

            foreach (KeyValuePair<int, IntVec3> pair in targets)
            {
                int targetIndex = map.cellIndices.CellToIndex(pair.Value);
                if (!visited[targetIndex])
                {
                    Log.Error("[OuterrealmArchotechTeleportStation] Hardened road validation failed for RimExodus " +
                        "neighbor tile " + pair.Key + " on map " + map.uniqueID + ".");
                }
            }
        }

        private static bool IsVoid(Map map, IntVec3 cell)
        {
            TerrainDef terrain = cell.GetTerrain(map);
            return terrain != null && terrain.defName == VoidTerrainDefName;
        }
    }
}
