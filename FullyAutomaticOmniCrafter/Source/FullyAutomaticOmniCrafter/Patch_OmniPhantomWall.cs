using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace FullyAutomaticOmniCrafter
{

    // ── 区域类型补丁：让幻影墙形成真正的房间边界（不产生无用单格房间）───────
    [HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
    public static class RegionTypeUtility_GetExpectedRegionType_Patch
    {
        private static readonly RegionType[] PhantomWall2RegionTypeBuckets =
        {
            (RegionType)18,
            (RegionType)34,
            (RegionType)50,
            (RegionType)66,
            (RegionType)82,
            (RegionType)98,
            (RegionType)114,
            (RegionType)130,
            (RegionType)146,
            (RegionType)162,
            (RegionType)194,
            (RegionType)210,
            (RegionType)226,
            (RegionType)242
        };

        private static readonly Dictionary<Map, PhantomWall2RegionColorCache> PhantomWall2ColorCaches =
            new Dictionary<Map, PhantomWall2RegionColorCache>();

        public static void Postfix(IntVec3 c, Map map, ref RegionType __result)
        {
            // 只有当结果本来是 Normal（Standable 地块）才需要覆写
            if (__result == RegionType.Normal)
            {
                var edifice = c.GetEdifice(map);
                if (edifice is Building_OmniPhantomWall)
                {
                    __result = Building_OmniPhantomWall.PhantomWallRegionType;
                }
                else if (edifice is Building_OmniPhantomWall2 wall2)
                {
                    // 按当前地图上的相邻规则组件贪心涂色，使用固定桶让 RegionMaker 自动分割。
                    __result = GetRegionTypeForWall2Cell(map, c, wall2);
                }
            }
        }

        public static void NotifyPhantomWall2ColoringDirty(Map map)
        {
            if (map == null)
                return;

            GetPhantomWall2ColorCache(map).MarkDirty();
        }

        public static bool IsPhantomWallRegion(RegionType type)
        {
            for (int i = 0; i < PhantomWall2RegionTypeBuckets.Length; i++)
            {
                if (type == PhantomWall2RegionTypeBuckets[i])
                    return true;
            }
            return type == Building_OmniPhantomWall.PhantomWallRegionType;
        }

        private static RegionType GetRegionTypeForWall2Cell(Map map, IntVec3 c, Building_OmniPhantomWall2 wall2)
        {
            if (map == null || wall2 == null)
                return PhantomWall2RegionTypeBuckets[0];

            return GetPhantomWall2ColorCache(map).GetRegionType(c);
        }

        private static PhantomWall2RegionColorCache GetPhantomWall2ColorCache(Map map)
        {
            PhantomWall2RegionColorCache cache;
            if (!PhantomWall2ColorCaches.TryGetValue(map, out cache) || !cache.MatchesMap)
            {
                cache = new PhantomWall2RegionColorCache(map, PhantomWall2RegionTypeBuckets);
                PhantomWall2ColorCaches[map] = cache;
            }
            return cache;
        }

        private sealed class PhantomWall2RegionColorCache
        {
            private readonly Map map;
            private readonly RegionType[] buckets;
            private readonly List<Building_OmniPhantomWall2> walls = new List<Building_OmniPhantomWall2>();
            private readonly List<int> wallCells = new List<int>();
            private readonly List<IntVec3> floodStack = new List<IntVec3>();
            private readonly List<Component> components = new List<Component>();
            private readonly List<int> colorOrder = new List<int>();

            private int[] regionTypeByCell;
            private int[] wallIndexByCell;
            private int[] componentByCell;
            private bool dirty = true;

            public PhantomWall2RegionColorCache(Map map, RegionType[] buckets)
            {
                this.map = map;
                this.buckets = buckets;
            }

            public bool MatchesMap
            {
                get
                {
                    return map != null
                        && regionTypeByCell != null
                        && regionTypeByCell.Length == map.cellIndices.NumGridCells;
                }
            }

            public void MarkDirty()
            {
                dirty = true;
            }

            public RegionType GetRegionType(IntVec3 c)
            {
                if (dirty || !MatchesMap)
                    Rebuild();

                int index = map.cellIndices.CellToIndex(c);
                int regionType = regionTypeByCell[index];
                return regionType != 0 ? (RegionType)regionType : buckets[0];
            }

            private void Rebuild()
            {
                EnsureArrays();
                ClearArrays();
                CollectWalls(map.listerBuildings.allBuildingsColonist);
                CollectWalls(map.listerBuildings.allBuildingsNonColonist);
                BuildComponents();
                BuildEdges();
                ColorComponents();
                ApplyColorsToCells();
                dirty = false;
            }

            private void EnsureArrays()
            {
                int cellCount = map.cellIndices.NumGridCells;
                if (regionTypeByCell == null || regionTypeByCell.Length != cellCount)
                {
                    regionTypeByCell = new int[cellCount];
                    wallIndexByCell = new int[cellCount];
                    componentByCell = new int[cellCount];
                }
            }

            private void ClearArrays()
            {
                for (int i = 0; i < regionTypeByCell.Length; i++)
                {
                    regionTypeByCell[i] = 0;
                    wallIndexByCell[i] = -1;
                    componentByCell[i] = -1;
                }
                walls.Clear();
                wallCells.Clear();
                floodStack.Clear();
                components.Clear();
                colorOrder.Clear();
            }

            private void CollectWalls(List<Building> buildings)
            {
                if (buildings == null)
                    return;

                for (int i = 0; i < buildings.Count; i++)
                {
                    Building_OmniPhantomWall2 wall = buildings[i] as Building_OmniPhantomWall2;
                    if (wall == null || wall.Destroyed || !wall.Spawned || wall.Map != map)
                        continue;

                    int wallIndex = walls.Count;
                    bool hasCell = false;
                    foreach (IntVec3 cell in wall.OccupiedRect())
                    {
                        if (!cell.InBounds(map) || cell.GetEdifice(map) != wall)
                            continue;

                        int cellIndex = map.cellIndices.CellToIndex(cell);
                        if (wallIndexByCell[cellIndex] >= 0)
                            continue;

                        wallIndexByCell[cellIndex] = wallIndex;
                        wallCells.Add(cellIndex);
                        hasCell = true;
                    }

                    if (hasCell)
                        walls.Add(wall);
                }
            }

            private void BuildComponents()
            {
                for (int i = 0; i < wallCells.Count; i++)
                {
                    int rootIndex = wallCells[i];
                    if (componentByCell[rootIndex] >= 0)
                        continue;

                    int wallIndex = wallIndexByCell[rootIndex];
                    if (wallIndex < 0)
                        continue;

                    Component component = new Component
                    {
                        signature = GetWallSignature(walls[wallIndex]),
                        firstCellIndex = rootIndex
                    };
                    int componentIndex = components.Count;
                    components.Add(component);

                    floodStack.Clear();
                    componentByCell[rootIndex] = componentIndex;
                    floodStack.Add(map.cellIndices.IndexToCell(rootIndex));

                    while (floodStack.Count > 0)
                    {
                        int last = floodStack.Count - 1;
                        IntVec3 cell = floodStack[last];
                        floodStack.RemoveAt(last);

                        int cellIndex = map.cellIndices.CellToIndex(cell);
                        if (cellIndex < component.firstCellIndex)
                            component.firstCellIndex = cellIndex;

                        for (int dir = 0; dir < 4; dir++)
                        {
                            IntVec3 neighbor = cell + GenAdj.CardinalDirections[dir];
                            if (!neighbor.InBounds(map))
                                continue;

                            int neighborIndex = map.cellIndices.CellToIndex(neighbor);
                            int neighborWallIndex = wallIndexByCell[neighborIndex];
                            if (neighborWallIndex >= 0)
                            {
                                if (componentByCell[neighborIndex] < 0
                                    && GetWallSignature(walls[neighborWallIndex]) == component.signature)
                                {
                                    componentByCell[neighborIndex] = componentIndex;
                                    floodStack.Add(neighbor);
                                }
                                continue;
                            }

                            Building edifice = neighbor.GetEdifice(map);
                            if (edifice is Building_OmniPhantomWall && !(edifice is Building_OmniPhantomWall2))
                            {
                                // 一代幻影墙固定使用 18，贴边的二代组件不能再使用 18，避免被 flood-fill 合并。
                                component.blocksFirstBucket = true;
                            }
                        }
                    }
                }
            }

            private void BuildEdges()
            {
                for (int i = 0; i < wallCells.Count; i++)
                {
                    int cellIndex = wallCells[i];
                    int componentIndex = componentByCell[cellIndex];
                    if (componentIndex < 0)
                        continue;

                    IntVec3 cell = map.cellIndices.IndexToCell(cellIndex);
                    Component component = components[componentIndex];
                    for (int dir = 0; dir < 4; dir++)
                    {
                        IntVec3 neighbor = cell + GenAdj.CardinalDirections[dir];
                        if (!neighbor.InBounds(map))
                            continue;

                        int neighborComponentIndex = componentByCell[map.cellIndices.CellToIndex(neighbor)];
                        if (neighborComponentIndex < 0 || neighborComponentIndex == componentIndex)
                            continue;

                        Component neighborComponent = components[neighborComponentIndex];
                        if (neighborComponent.signature == component.signature)
                            continue;

                        component.neighbors.Add(neighborComponentIndex);
                        neighborComponent.neighbors.Add(componentIndex);
                    }
                }
            }

            private void ColorComponents()
            {
                for (int i = 0; i < components.Count; i++)
                {
                    components[i].colorIndex = -1;
                    colorOrder.Add(i);
                }

                colorOrder.Sort(CompareComponentsForColoring);
                bool[] used = new bool[buckets.Length];

                for (int i = 0; i < colorOrder.Count; i++)
                {
                    Component component = components[colorOrder[i]];
                    for (int j = 0; j < used.Length; j++)
                        used[j] = false;

                    if (component.blocksFirstBucket)
                        used[0] = true;

                    foreach (int neighborIndex in component.neighbors)
                    {
                        int neighborColor = components[neighborIndex].colorIndex;
                        if (neighborColor >= 0)
                            used[neighborColor] = true;
                    }

                    int colorIndex = FirstAvailableColor(used);
                    if (colorIndex < 0)
                    {
                        colorIndex = LeastConflictingColor(component);
                        Log.Warning($"[OmniPhantomWall2] RegionType 桶池已耗尽，使用 {buckets[colorIndex]}，可能产生局部冲突。");
                    }
                    component.colorIndex = colorIndex;
                }
            }

            private int CompareComponentsForColoring(int a, int b)
            {
                Component componentA = components[a];
                Component componentB = components[b];
                int degreeCompare = componentB.neighbors.Count.CompareTo(componentA.neighbors.Count);
                if (degreeCompare != 0)
                    return degreeCompare;

                int blockCompare = componentB.blocksFirstBucket.CompareTo(componentA.blocksFirstBucket);
                if (blockCompare != 0)
                    return blockCompare;

                return componentA.firstCellIndex.CompareTo(componentB.firstCellIndex);
            }

            private int FirstAvailableColor(bool[] used)
            {
                for (int i = 0; i < used.Length; i++)
                {
                    if (!used[i])
                        return i;
                }
                return -1;
            }

            private int LeastConflictingColor(Component component)
            {
                int bestColor = component.blocksFirstBucket ? 1 : 0;
                int bestConflicts = int.MaxValue;
                for (int color = bestColor; color < buckets.Length; color++)
                {
                    int conflicts = 0;
                    foreach (int neighborIndex in component.neighbors)
                    {
                        if (components[neighborIndex].colorIndex == color)
                            conflicts++;
                    }

                    if (conflicts < bestConflicts)
                    {
                        bestConflicts = conflicts;
                        bestColor = color;
                        if (conflicts == 0)
                            break;
                    }
                }
                return bestColor;
            }

            private void ApplyColorsToCells()
            {
                for (int i = 0; i < wallCells.Count; i++)
                {
                    int cellIndex = wallCells[i];
                    int componentIndex = componentByCell[cellIndex];
                    if (componentIndex < 0)
                        continue;

                    Component component = components[componentIndex];
                    regionTypeByCell[cellIndex] = (int)buckets[component.colorIndex];
                }
            }

            private static int GetWallSignature(Building_OmniPhantomWall2 wall)
            {
                return wall.settings != null ? wall.settings.GetSignature() : 0;
            }

            private sealed class Component
            {
                public int signature;
                public int firstCellIndex;
                public int colorIndex = -1;
                public bool blocksFirstBucket;
                public readonly HashSet<int> neighbors = new HashSet<int>();
            }
        }
    }

    // ── AffectsRegions 补丁：使 SpawnSetup/DeSpawn 正确触发区域 dirty ─────────
    /// <summary>
    /// ThingDef.AffectsRegions 默认只对 Impassable/IsDoor/IsFence 返回 true。
    /// 幻影墙 passability=Standable，导致 Thing.SpawnSetup 和 Thing.DeSpawn 均不会调用
    /// Notify_ThingAffectingRegionsSpawned / Notify_ThingAffectingRegionsDespawned，
    /// 区域永远不 dirty，GetExpectedRegionType 补丁无法生效，围墙无法形成独立房间。
    ///
    /// 此补丁让 OmniPhantomWall 的 ThingDef 返回 AffectsRegions=true，
    /// 使 Thing.SpawnSetup/DeSpawn 走正常的区域 dirty 流程。
    /// </summary>
    [HarmonyPatch(typeof(ThingDef), "AffectsRegions", MethodType.Getter)]
    public static class ThingDef_AffectsRegions_Patch
    {
        public static void Postfix(ThingDef __instance, ref bool __result)
        {
            if (__result) return; // already true
            // 支持子类，如果将来有继承自 Building_OmniPhantomWall 的子类，也会自动生效。
            if (typeof(Building_OmniPhantomWall).IsAssignableFrom(__instance.thingClass))
                __result = true;
            // 支持子类，如果将来有继承自 Building_OmniPhantomWall2 的子类，也会自动生效。
            if (typeof(Building_OmniPhantomWall2).IsAssignableFrom(__instance.thingClass))
                __result = true;
        }
    }

    // ── 可达性补丁：敌方小人在 BFS 层无法穿越幻影墙区域 ─────────────────────
    /// <summary>
    /// 敌方小人不应在可达性 BFS（Region.Allows）层面穿越幻影墙区域。
    ///
    /// 否则敌人会"认为"能到达幻影墙内部，反复尝试寻路，产生无效 AI 行为。
    /// 友方小人（pawn=null 或 Faction=OfPlayer/HostFaction=OfPlayer）正常穿越。
    /// </summary>
    [HarmonyPatch(typeof(Region), nameof(Region.Allows))]
    public static class Region_Allows_PhantomWall_Patch
    {
        public static void Postfix(Region __instance, TraverseParms tp, ref bool __result)
        {
            if (!RegionTypeUtility_GetExpectedRegionType_Patch.IsPhantomWallRegion(__instance.type))
                return;

            // Log.Message(
            //     $"[OmniPhantomWall] Region.Allows PhantomWallRegion: pawn={tp.pawn}, " +
            //     $"regionType={__instance.type}, originalResult={__result}");

            if (tp.pawn == null)
            {
                // 如果没有提供 Pawn 信息（如区域检查），不执行进一步拦截，以免破坏系统功能
                return;
            }

            // 从该区域的任意幻影墙 Thing 上读取规则
            var cell = __instance.AnyCell;
            var building = cell.GetEdifice(__instance.Map);
            
            // 检查是否是OmniPhantomWall2
            if (building is Building_OmniPhantomWall2 wall2)
            {
                __result = wall2.CanPawnPassInstance(tp.pawn);
                return;
            }
            
            // 检查是否是OmniPhantomWall
            if (building is Building_OmniPhantomWall wall1)
            {
                var ext = wall1.def.GetModExtension<PhantomWallExtension>();
                __result = Building_OmniPhantomWall.CanPawnPass(tp.pawn, ext);
                return;
            }
            
            // 默认允许通过
            __result = true;
        }
    }

    // ── 房间合并补丁：让幻影墙形成独立的房间区域 ───────────────────────────
    /// <summary>
    /// 修正幻影墙无法产生房间的问题。
    ///
    /// RimWorld 原逻辑中，只有 Normal/ImpassableFreeAirExchange/Fence 才能属于一个 Room。
    /// 此补丁允许 PhantomWallRegionType 区域互相合并进入同一个 Room，
    /// 但阻止它们与 Normal 等其他区域合并，从而在物理上和逻辑上切断内外连接，形成独立房间。
    /// 
    /// 对于OmniPhantomWall2，需要比对通行规则签名，规则相同的墙体才能合并为同一房间。
    /// </summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater), "ShouldBeInTheSameRoom")]
    public static class RegionAndRoomUpdater_ShouldBeInTheSameRoom_Patch
    {
        public static bool Prefix(District a, District b, ref bool __result)
        {
            RegionType typeA = a.RegionType;
            RegionType typeB = b.RegionType;

            bool isPhantomA = RegionTypeUtility_GetExpectedRegionType_Patch.IsPhantomWallRegion(typeA);
            bool isPhantomB = RegionTypeUtility_GetExpectedRegionType_Patch.IsPhantomWallRegion(typeB);

            // 如果两个都是幻影墙，且 RegionType 相同，则它们属于同一个房间。
            // 二代墙的涂色缓存会保证相邻的不同规则组件使用不同 RegionType。
            if (isPhantomA && isPhantomB)
            {
                __result = (typeA == typeB);
                return false;
            }

            // 如果其中一个是幻影墙（另一个必然不是），它们绝不属于同一个房间（隔离内外）
            if (isPhantomA || isPhantomB)
            {
                __result = false;
                return false;
            }

            // 其余情况执行原版逻辑
            return true;
        }
    }

    // ── 子弹穿透补丁 ──────────────────────────────────────────────────
    /// <summary>
    /// 让玩家发射的子弹穿过幻影墙，敌人发射的子弹被挡住。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "CanHit")]
    public static class Projectile_CanHit_Patch
    {
        public static void Postfix(Projectile __instance, Thing thing, ref bool __result)
        {
            if (!(thing is Building_OmniPhantomWall) && !(thing is Building_OmniPhantomWall2))
                return;

            // Launcher 属性返回发射者 Thing（武器持有者/建筑炮台）
            Thing launcher = __instance.Launcher;
            if (launcher != null && launcher.Faction == Faction.OfPlayer)
            {
                // 玩家发射的子弹穿透幻影墙
                __result = false;
            }
            else
            {
                // 敌人发射的子弹被幻影墙拦截
                __result = true;
            }
        }
    }

    // ── 激光穿透补丁 ──────────────────────────────────────────────────
    /// <summary>
    /// 让玩家发射的激光穿过幻影墙，敌人发射的激光被挡住。
    /// 激光（Verb_ShootBeam）使用 GenSight.LastPointOnLineOfSight 进行命中检测，
    /// 其中使用了 CanBeSeenOverFast 检查建筑是否阻挡视线（Fillage == Full）。
    /// </summary>
    [HarmonyPatch(typeof(GenGrid), "CanBeSeenOverFast")]
    public static class GenGrid_CanBeSeenOverFast_Patch
    {
        // 我们使用 ThreadLocal 来标记当前的视线检查是否来自于 Verb
        private static System.Threading.ThreadLocal<Verb> currentVerb = new System.Threading.ThreadLocal<Verb>();

        [HarmonyPatch(typeof(Verb), "TryFindShootLineFromTo")]
        [HarmonyPrefix]
        public static void Verb_TryFindShootLineFromTo_Prefix(Verb __instance) => currentVerb.Value = __instance;

        [HarmonyPatch(typeof(Verb), "TryFindShootLineFromTo")]
        [HarmonyPostfix]
        public static void Verb_TryFindShootLineFromTo_Postfix() => currentVerb.Value = null;

        [HarmonyPatch(typeof(Verb_ShootBeam), "TryGetHitCell")]
        [HarmonyPrefix]
        public static void ShootBeam_TryGetHitCell_Prefix(Verb_ShootBeam __instance) => currentVerb.Value = __instance;

        [HarmonyPatch(typeof(Verb_ShootBeam), "TryGetHitCell")]
        [HarmonyPostfix]
        public static void ShootBeam_TryGetHitCell_Postfix() => currentVerb.Value = null;

        [HarmonyPatch(typeof(Verb_ShootBeam), "BurstingTick")]
        [HarmonyPrefix]
        public static void ShootBeam_BurstingTick_Prefix(Verb_ShootBeam __instance) => currentVerb.Value = __instance;

        [HarmonyPatch(typeof(Verb_ShootBeam), "BurstingTick")]
        [HarmonyPostfix]
        public static void ShootBeam_BurstingTick_Postfix() => currentVerb.Value = null;

        [HarmonyPatch(typeof(Verb_ShootBeam), "ApplyDamage")]
        [HarmonyPrefix]
        public static void ShootBeam_ApplyDamage_Prefix(Verb_ShootBeam __instance) => currentVerb.Value = __instance;

        [HarmonyPatch(typeof(Verb_ShootBeam), "ApplyDamage")]
        [HarmonyPostfix]
        public static void ShootBeam_ApplyDamage_Postfix() => currentVerb.Value = null;

        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            // 如果已经被判定为不阻挡（__result == true），则无需处理
            // 注意：CanBeSeenOverFast 返回 true 表示“可以被看透”，即“不阻挡”
            // if (__result) return;

            // 检查该位置是否有幻影墙
            Building edifice = c.GetEdifice(map);
            if (!(edifice is Building_OmniPhantomWall) && !(edifice is Building_OmniPhantomWall2))
                return;

            // 如果当前正处于 Verb 的路径计算中
            Verb verb = currentVerb.Value;
            if (verb != null)
            {
                if (verb.caster?.Faction == Faction.OfPlayer)
                {
                    // 玩家行为：设为不阻挡视线
                    __result = true;
                }
                else
                {
                    // 敌人行为：设为阻挡视线
                    __result = false;
                }
            }
            else
            {
                // 非 Verb 发起的检查（可能是 AI 寻找路径或其他逻辑）
                // 默认维持原有 Fillage 逻辑，或者根据需要调整。
                // 这里的 edifice.def.Fillage 通常是 Full，所以 __result 默认是 false。
            }
        }
    }

    // ── 破墙攻击 AI 补丁 ───────────────────────────────────────────────
    /// <summary>
    /// 防止破墙攻击（Breaching）的袭击者将幻影墙视为目标。
    /// </summary>
    [HarmonyPatch(typeof(BreachingUtility), "ShouldBreachBuilding")]
    public static class BreachingUtility_ShouldBreachBuilding_Patch
    {
        public static bool Postfix(bool __result, Thing thing)
        {
            if (!__result) return false;

            if (thing is Building_OmniPhantomWall || thing is Building_OmniPhantomWall2)
            {
                // 如果是幻影墙，我们通常不希望它被爆破。
                // 如果小人本身就被允许通过，那么爆破它是多余的。
                // 如果小人被禁止通过，根据上一轮的需求，我们依然拦截爆破行为，
                // 这将迫使 AI 寻找其他爆破路径（绕过幻影墙）。
                return false;
            }

            return __result;
        }
    }

    /// <summary>
    /// 防止破墙攻击路径算法将幻影墙所在的格子视为“阻塞”。
    /// 如果格子被视为阻塞，AI 会倾向于避开它，或者尝试爆破它。
    /// 我们配合 ShouldBreachBuilding 补丁（防止爆破），使 AI 在不可通行时倾向于绕道。
    /// </summary>
    [HarmonyPatch(typeof(BreachingUtility), "BlocksBreaching")]
    public static class BreachingUtility_BlocksBreaching_Patch
    {
        public static void Postfix(Map map, IntVec3 c, ref bool __result)
        {
            Building edifice = c.GetEdifice(map);
            if (edifice is Building_OmniPhantomWall wall1)
            {
                // 一代幻影墙目前没有任何模式允许敌对单位通过
                // 因此对于破墙 AI（敌方）来说，它始终是阻塞的
                __result = true;
            }
            else if (edifice is Building_OmniPhantomWall2 wall2)
            {
                // 二代幻影墙根据其设置决定是否阻塞敌对单位
                if (!wall2.settings.allowHostiles)
                {
                    __result = true;
                }
                else
                {
                    __result = false;
                }
            }
        }
    }

    // ── 性能优化：Harmony Patch 统一管理幻影墙区域温度 ───────────────────────────
    /// <summary>
    /// 当幻影墙数量巨大（如上万个）时，MapComponent 定时遍历房间虽然比逐个建筑 Tick 高效，
    /// 但仍存在不必要的开销。通过 Patch Room.Temperature 的 Getter，可以在不产生任何
    /// 定时计算开销的情况下，让幻影墙区域始终表现为恒温。
    /// </summary>
    [HarmonyPatch(typeof(Room), "get_Temperature")]
    public static class Room_Temperature_Getter_Patch
    {
        public static void Postfix(Room __instance, ref float __result)
        {
            // 只有当房间属于幻影墙区域时才拦截
            Region firstRegion = __instance.FirstRegion;
            if (firstRegion == null || !RegionTypeUtility_GetExpectedRegionType_Patch.IsPhantomWallRegion(firstRegion.type))
                return;

            // 防止在区域系统重建中途（invalid 或尚无 cells）访问 AnyCell，
            // 否则 Region.AnyCell → RegionGrid.DirectGrid 会触发递归重建，
            // 导致 "Could not register region" / "Couldn't find any cell in region" 错误。
            if (!firstRegion.valid)
                return;

            Map map = __instance.Map;
            if (map == null)
                return;

            // 仿 AnyCell 逻辑，但使用 GetRegionAt_NoRebuild_InvalidAllowed（不触发重建）
            // 而非 DirectGrid（会调用 TryRebuildDirtyRegionsAndRooms，导致递归重建崩溃）。
            // 同时避免 Cells（yield return 生成器，存在 IEnumerator 堆分配开销）。
            IntVec3 cell = IntVec3.Invalid;
            RegionGrid regionGrid = map.regionGrid;
            foreach (IntVec3 c in firstRegion.extentsClose)
            {
                if (regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(c) == firstRegion)
                {
                    cell = c;
                    break;
                }
            }
            if (!cell.IsValid)
                return;

            Building building = cell.GetEdifice(map);
            var ext = building?.def.GetModExtension<PhantomWallExtension>();
            __result = ext?.targetTemperature ?? 21f;
        }
    }

    [HarmonyPatch(typeof(Room), "set_Temperature")]
    public static class Room_Temperature_Setter_Patch
    {
        public static bool Prefix(Room __instance, ref float value)
        {
            // 如果是幻影墙房间，阻止任何温度修改，使其永远保持在 getter 返回的值
            Region firstRegion = __instance.FirstRegion;
            if (firstRegion != null && firstRegion.valid && RegionTypeUtility_GetExpectedRegionType_Patch.IsPhantomWallRegion(firstRegion.type))
            {
                return false;
            }
            return true;
        }
    }
}
