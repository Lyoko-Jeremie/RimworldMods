using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticGrowingZone
{
    public class FullyAutomaticGrowingZoneManager : MapComponent
    {
        public FullyAutomaticGrowingZoneManager(Map map) : base(map)
        {
        }

        // 活跃队列：刚刚被收获，或者刚刚被划定为自动种植区的格子。
        // 这里的格子大概率是可以直接播种的。
        public Queue<IntVec3> activeCellsToSow = new Queue<IntVec3>();

        // 休眠池：因为任何原因暂时无法播种的格子。
        private List<IntVec3> sleepingCells = new List<IntVec3>();

        // 三个独立开关集合
        public HashSet<Zone_Growing> autoSowZones = new HashSet<Zone_Growing>();
        public HashSet<Zone_Growing> autoHarvestZones = new HashSet<Zone_Growing>();
        public HashSet<Zone_Growing> autoStoreZones = new HashSet<Zone_Growing>();

        // 兼容性：任意开关开启即视为活跃区
        public HashSet<Zone_Growing> activeAutoZones => _activeAutoZonesCache;
        private HashSet<Zone_Growing> _activeAutoZonesCache = new HashSet<Zone_Growing>();

        public void RebuildActiveCache()
        {
            _activeAutoZonesCache.Clear();
            _activeAutoZonesCache.UnionWith(autoSowZones);
            _activeAutoZonesCache.UnionWith(autoHarvestZones);
            _activeAutoZonesCache.UnionWith(autoStoreZones);
        }

        public Queue<Plant> plantsToHarvest = new Queue<Plant>();

        // 去重集合：防止同一植物被多次加入收获队列（用 thingIDNumber 作为 key）
        private HashSet<int> plantsInHarvestQueue = new HashSet<int>();

        // 虚拟仓库：记录每种作物的暂存数量
        public Dictionary<ThingDef, int> virtualYieldBuffer = new Dictionary<ThingDef, int>();

        public override void ExposeData()
        {
            base.ExposeData();

            // 保存虚拟仓库
            Scribe_Collections.Look(ref virtualYieldBuffer, "virtualYieldBuffer", LookMode.Def, LookMode.Value);

            List<Zone_Growing> sowList = autoSowZones.ToList();
            List<Zone_Growing> harvestList = autoHarvestZones.ToList();
            List<Zone_Growing> storeList = autoStoreZones.ToList();
            Scribe_Collections.Look(ref sowList, "autoSowZones", LookMode.Reference);
            Scribe_Collections.Look(ref harvestList, "autoHarvestZones", LookMode.Reference);
            Scribe_Collections.Look(ref storeList, "autoStoreZones", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                virtualYieldBuffer = virtualYieldBuffer ?? new Dictionary<ThingDef, int>();
                autoSowZones = new HashSet<Zone_Growing>();
                autoHarvestZones = new HashSet<Zone_Growing>();
                autoStoreZones = new HashSet<Zone_Growing>();

                if (sowList != null)
                    foreach (var z in sowList)
                    {
                        if (z != null) autoSowZones.Add(z);
                    }

                if (harvestList != null)
                    foreach (var z in harvestList)
                    {
                        if (z != null) autoHarvestZones.Add(z);
                    }

                if (storeList != null)
                    foreach (var z in storeList)
                    {
                        if (z != null) autoStoreZones.Add(z);
                    }

                RebuildActiveCache();

                // 读档后，为所有自动区的格子做一次全面扫描
                foreach (var zone in autoSowZones)
                {
                    foreach (IntVec3 cell in zone.Cells)
                    {
                        activeCellsToSow.Enqueue(cell);
                    }
                }
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            // 每隔一段时间清理无效的区域引用（区域被玩家删除的情况）
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                autoSowZones.RemoveWhere(z => z == null || z.Map == null);
                autoHarvestZones.RemoveWhere(z => z == null || z.Map == null);
                autoStoreZones.RemoveWhere(z => z == null || z.Map == null);
                RebuildActiveCache();
            }

            // 定期刷出虚拟仓库中未满一组的残余物资，防止超大堆叠mod下物资长期滞留
            if (Find.TickManager.TicksGame % 600 == 0)
            {
                FlushVirtualBuffer();
            }

            // 处理收获队列
            int harvestCountThisTick = Mathf.CeilToInt(plantsToHarvest.Count / 60f);
            // 设定一个硬性上限，防止极端情况单帧卡死（比如一次最多处理 100 个）
            harvestCountThisTick = Mathf.Clamp(harvestCountThisTick, 0, 100);

            for (int i = 0; i < harvestCountThisTick; i++)
            {
                if (plantsToHarvest.TryDequeue(out Plant plant))
                {
                    if (plant != null)
                        plantsInHarvestQueue.Remove(plant.thingIDNumber);
                    if (plant != null && !plant.Destroyed && plant.Growth >= 1f)
                    {
                        ExecuteHarvest(plant);
                        if (IsAutoSow(plant.Position))
                        {
                            activeCellsToSow.Enqueue(plant.Position);
                        }
                    }
                }
            }

            // 处理活跃队列
            int activeToProcess = Mathf.Min(activeCellsToSow.Count, 10);
            for (int i = 0; i < activeToProcess; i++)
            {
                IntVec3 cell = activeCellsToSow.Dequeue();

                ThingDef plantDef = GetPlantDefForCell(cell);
                if (plantDef != null)
                {
                    if (CanAutoSowAndClear(plantDef, cell, map))
                    {
                        ExecuteSow(cell, plantDef);
                    }
                    else
                    {
                        sleepingCells.Add(cell);
                    }
                }
                // 如果 plantDef == null，说明格子已不在自动区内，直接丢弃
            }

            // 随机乱步重试休眠池
            if (sleepingCells.Count > 0)
            {
                // 无论休眠池里有 5 个还是 50,000 个，每帧只随机抽查 5 个！
                // 性能开销永远是 O(1)
                int retryCount = Mathf.Min(sleepingCells.Count, 5);
                for (int i = 0; i < retryCount; i++)
                {
                    // 随机抽取一个索引
                    int randomIndex = Rand.Range(0, sleepingCells.Count);
                    IntVec3 cell = sleepingCells[randomIndex];

                    ThingDef plantDef = GetPlantDefForCell(cell);

                    // 如果格子已不在自动区内，从休眠池移除
                    if (plantDef == null)
                    {
                        sleepingCells[randomIndex] = sleepingCells[sleepingCells.Count - 1];
                        sleepingCells.RemoveAt(sleepingCells.Count - 1);
                        continue;
                    }

                    if (CanAutoSowAndClear(plantDef, cell, map))
                    {
                        ExecuteSow(cell, plantDef);
                        sleepingCells[randomIndex] = sleepingCells[sleepingCells.Count - 1];
                        sleepingCells.RemoveAt(sleepingCells.Count - 1);
                    }
                }
            }
        }

        public void ExecuteHarvest(Plant plant)
        {
            // 1. 获取作物的产出物类型（比如水稻、玉米）
            ThingDef harvestedThingDef = plant.def.plant.harvestedThingDef;

            // 2. 计算产量。
            // plant.YieldNow() 会根据植物当前的生长进度、健康状态计算出基础产量。
            // 因为是全自动农场没有小人参与，所以不需要计算小人的农业技能加成（这本身也是一种平衡）。
            int yieldAmount = plant.YieldNow();
            IntVec3 position = plant.Position; // 在销毁前缓存位置

            // 检查该格子所在区是否开启了 autoStore
            bool shouldStore = IsAutoStore(position);

            plant.Destroy(DestroyMode.Vanish);

            // 4. 将产物放入虚拟仓库进行“合并堆叠”
            if (harvestedThingDef != null && yieldAmount > 0)
            {
                if (shouldStore)
                {
                    // autoStore 模式：合并堆叠后尝试搬运到仓库
                    if (!virtualYieldBuffer.ContainsKey(harvestedThingDef))
                        virtualYieldBuffer[harvestedThingDef] = 0;

                    virtualYieldBuffer[harvestedThingDef] += yieldAmount;

                    // 使用原版堆叠上限，但设置一个合理的刷出阈值（至少500），
                    // 防止超大堆叠mod导致物资长期滞留在虚拟仓库中
                    int stackLimit = harvestedThingDef.stackLimit;
                    int flushThreshold = Mathf.Min(stackLimit, Mathf.Max(500, stackLimit / 10));
                    while (virtualYieldBuffer[harvestedThingDef] >= flushThreshold)
                    {
                        // 每次刷出量不超过原版堆叠上限，也不超过当前库存
                        int spawnCount = Mathf.Min(virtualYieldBuffer[harvestedThingDef], stackLimit);
                        Thing fullStack = ThingMaker.MakeThing(harvestedThingDef);
                        fullStack.stackCount = spawnCount;

                        // 尝试放入最佳仓库
                        if (!TryPlaceInStockpile(fullStack))
                        {
                            // 找不到仓库，回退到就地放置
                            GenPlace.TryPlaceThing(fullStack, position, map, ThingPlaceMode.Near);
                        }

                        virtualYieldBuffer[harvestedThingDef] -= spawnCount;
                    }
                }
                else
                {
                    // 非 autoStore 模式：直接就地掉落，不经过虚拟仓库
                    int stackLimit = harvestedThingDef.stackLimit;
                    while (yieldAmount > 0)
                    {
                        int count = Mathf.Min(yieldAmount, stackLimit);
                        Thing stack = ThingMaker.MakeThing(harvestedThingDef);
                        stack.stackCount = count;
                        GenPlace.TryPlaceThing(stack, position, map, ThingPlaceMode.Near);
                        yieldAmount -= count;
                    }
                }
            }
        }

        /// <summary>
        /// 尝试将物品放入地图上最合适的仓库格子
        /// </summary>
        private bool TryPlaceInStockpile(Thing thing)
        {
            // 使用原版的 StoreUtility 来找到最佳仓库位置
            if (StoreUtility.TryFindBestBetterStoreCellFor(thing, null, map, StoragePriority.Unstored, null,
                    out IntVec3 storeCell))
            {
                GenPlace.TryPlaceThing(thing, storeCell, map, ThingPlaceMode.Near);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 定期将虚拟仓库中未满一组的残余物资刷出到仓库或田地上，
        /// 防止在超大堆叠 mod 下物资长期滞留在虚拟仓库中。
        /// </summary>
        public void FlushVirtualBuffer()
        {
            foreach (var def in virtualYieldBuffer.Keys.ToList())
            {
                int amount = virtualYieldBuffer[def];
                if (amount <= 0) continue;

                // 找一个 autoStore 区的格子作为回退掉落点
                IntVec3 fallbackCell = IntVec3.Invalid;
                foreach (var zone in autoStoreZones)
                {
                    if (zone != null && zone.Cells.Count > 0)
                    {
                        fallbackCell = zone.Cells.First();
                        break;
                    }
                }

                if (!fallbackCell.IsValid) break; // 没有 autoStore 区了，保留buffer

                Thing stack = ThingMaker.MakeThing(def);
                stack.stackCount = amount;

                if (!TryPlaceInStockpile(stack))
                {
                    GenPlace.TryPlaceThing(stack, fallbackCell, map, ThingPlaceMode.Near);
                }

                virtualYieldBuffer[def] = 0;
            }
        }

        public void ForceDropAllBuffer(IntVec3 dropCell)
        {
            foreach (var def in virtualYieldBuffer.Keys.ToList())
            {
                int amount = virtualYieldBuffer[def];
                while (amount > 0)
                {
                    int count = Mathf.Min(amount, def.stackLimit);
                    Thing leftovers = ThingMaker.MakeThing(def);
                    leftovers.stackCount = count;
                    GenPlace.TryPlaceThing(leftovers, dropCell, map, ThingPlaceMode.Near);
                    amount -= count;
                }

                virtualYieldBuffer[def] = 0;
            }
        }

        public void ExecuteSow(IntVec3 cell, ThingDef plantDef)
        {
            // 1. O(1) 极速查询该格子当前所属的 Zone
            Zone zone = map.zoneManager.ZoneAt(cell);

            // 2. 校验：这个格子还在种植区里吗？它是普通的种植区吗？
            if (zone is Zone_Growing growingZone)
            {
                if (autoSowZones.Contains(growingZone))
                {
                    // 4. 获取玩家【当前时刻】设定的植物类型！
                    // 这样无论玩家怎么更改作物，你的 Mod 永远种的是正确的类型
                    ThingDef plantDefToGrow = growingZone.GetPlantDefToGrow();
                    if (plantDefToGrow != null)
                    {
                        // 5. 校验：这个格子现在可以种这个植物吗？（比如温度够不够，有没有被石头挡住）
                        if (CanAutoSowAndClear(plantDefToGrow, cell, map))
                        {
                            // 6. 终于可以安全地生成植物了
                            Plant newPlant = (Plant)GenSpawn.Spawn(plantDefToGrow, cell, map);
                            newPlant.Growth = 0f;
                            newPlant.sown = true;
                        }
                    }
                }
            }
        }

        public ThingDef GetPlantDefForCell(IntVec3 cell)
        {
            // O(1) 极速查询：底层直接读取 map.zoneManager.zoneGrid 数组
            Zone zone = map.zoneManager.ZoneAt(cell);

            // 如果这个格子属于原版的种植区
            if (zone is Zone_Growing growingZone)
            {
                if (autoSowZones.Contains(growingZone))
                {
                    // 直接返回玩家当前在该区指定的作物类型
                    return growingZone.GetPlantDefToGrow();
                }
            }

            // 不在自动种植区内，或者根本不是种植区
            return null;
        }

        public bool CanAutoSowAndClear(ThingDef plantDef, IntVec3 c, Map map)
        {
            // ==========================================
            // 1. 生长季节与温度校验 (原生方法：完美处理室内外)
            // 底层会直接 $O(1)$ 查询该格子所属 Room 的缓存温度
            // 注: 如果你在极老版本(1.3之前)，这个方法在 GenPlant.GrowthSeasonNow
            // ==========================================
            if (!PlantUtility.GrowthSeasonNow(c, map, plantDef))
            {
                return false;
            }

            // ==========================================
            // 2. 地形肥力校验
            // 防止玩家把种植区划好后，地形被炸弹破坏变成了沙地
            // 极速原生查询：直接读取 fertilityGrid 浮点数组
            // ==========================================
            if (plantDef.plant.fertilityMin > 0f && map.fertilityGrid.FertilityAt(c) < plantDef.plant.fertilityMin)
            {
                return false;
            }

            // ==========================================
            // 3. 物理遮挡与自动清障 (核心精华)
            // ==========================================
            List<Thing> thingList = c.GetThingList(map);

            // 【关键】：必须倒序遍历！
            // 因为我们可能会在循环中 Destroy 植物，正序遍历会导致 List 长度改变引发越界报错。
            for (int i = thingList.Count - 1; i >= 0; i--)
            {
                Thing thing = thingList[i];

                // 遇到建筑（墙、建造蓝图、风力发电机）或物品（岩石碎块、掉落物）
                // 判定为真正的物理阻塞 -> 返回 false，让格子进入 sleepingCells (休眠池)
                if (thing.def.category == ThingCategory.Building || thing.def.category == ThingCategory.Item)
                {
                    return false;
                }

                // 遇到植物
                if (thing.def.category == ThingCategory.Plant)
                {
                    // 情况 A：格子上的植物就是我们要种的植物
                    // 可能是休眠池重试时发现小人已经帮忙种上了，直接跳过，防止同一格 Spawn 两个植物
                    if (thing.def == plantDef) return false;

                    // 情况 B：格子上有野草、树木，或者是玩家刚刚把这个区的种植计划从“水稻”改成了“玉米”
                    // 自动农场特权：无情抹除！直接腾出地块，且不产生任何垃圾或掉落物
                    thing.Destroy(DestroyMode.Vanish);
                }

                // 至于污物(Filth)、特效(Mote)、动物或小人(Pawn)踩在上面，
                // 在 RimWorld 的规则里都不影响播种，所以直接无视，最大化性能！
            }

            // 通过了所有考验：温度合适、地块肥沃、没有遮挡（或杂草已被清除）
            return true;
        }


        // ===========================================================

        // 提供给 Harmony 快速调用的辅助方法
        public bool IsAutoZone(IntVec3 cell)
        {
            Zone zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Growing growingZone)
                return autoHarvestZones.Contains(growingZone);
            return false;
        }

        public bool IsAutoSow(IntVec3 cell)
        {
            Zone zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Growing growingZone)
                return autoSowZones.Contains(growingZone);
            return false;
        }

        public bool IsAutoStore(IntVec3 cell)
        {
            Zone zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Growing growingZone)
                return autoStoreZones.Contains(growingZone);
            return false;
        }

        // 带去重的入队方法，防止同一植物被多次加入收获队列
        public bool TryEnqueueHarvest(Plant plant)
        {
            if (plantsInHarvestQueue.Add(plant.thingIDNumber))
            {
                plantsToHarvest.Enqueue(plant);
                return true;
            }

            return false;
        }

        // 当玩家更改某个自动区的作物类型时，重新扫描该区所有格子
        public void OnPlantDefChanged(Zone_Growing zone)
        {
            if (autoSowZones.Contains(zone))
            {
                foreach (IntVec3 cell in zone.Cells)
                {
                    activeCellsToSow.Enqueue(cell);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Plant), "TickLong")]
    public static class Plant_TickLong_Patch
    {
        // 使用 Postfix (后置补丁)，等原版计算完生长进度后再执行我们的逻辑
        public static void Postfix(Plant __instance)
        {
            // 1. 安全校验：植物可能在 TickLong 期间因起火等原因被摧毁，或者地图已关闭
            if (__instance.Destroyed || __instance.Map == null) return;

            // 2. 检查是否成熟 (Growth 达到 1.0)
            if (__instance.Growth >= 1f)
            {
                // 3. 获取我们自定义的 MapComponent
                var comp = __instance.Map.GetComponent<FullyAutomaticGrowingZoneManager>();
                if (comp != null)
                {
                    if (comp.IsAutoZone(__instance.Position) && comp.TryEnqueueHarvest(__instance))
                    {
                        // 已加入收获队列
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), "AddCell")]
    public static class Zone_Growing_AddCell_Patch
    {
        public static void Postfix(Zone_Growing __instance, IntVec3 c)
        {
            Map map = __instance.Map;
            if (map == null) return;

            var comp = map.GetComponent<FullyAutomaticGrowingZoneManager>();
            if (comp != null && comp.autoSowZones.Contains(__instance))
            {
                comp.activeCellsToSow.Enqueue(c);
            }
        }
    }

    // 当玩家更改种植区的作物类型时，重新扫描
    [HarmonyPatch(typeof(Zone_Growing), "SetPlantDefToGrow")]
    public static class Zone_Growing_SetPlantDefToGrow_Patch
    {
        public static void Postfix(Zone_Growing __instance)
        {
            Map map = __instance.Map;
            if (map == null) return;

            var comp = map.GetComponent<FullyAutomaticGrowingZoneManager>();
            comp?.OnPlantDefChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), "GetGizmos")]
    public static class Zone_Growing_GetGizmos_Patch
    {
        // ── 缓存的图标贴图 ──
        private static readonly Texture2D IconAutoHarvest =
            ContentFinder<Texture2D>.Get("UI/Commands/autoHarvestGrowingZone", false) ?? BaseContent.WhiteTex;

        private static readonly Texture2D IconAutoSow =
            ContentFinder<Texture2D>.Get("UI/Commands/autoSowGrowingZone", false) ?? BaseContent.WhiteTex;

        private static readonly Texture2D IconAutoStore =
            ContentFinder<Texture2D>.Get("UI/Commands/autoStoreGrowingZone", false) ?? BaseContent.WhiteTex;

        private static readonly Texture2D IconFlushBuffer =
            ContentFinder<Texture2D>.Get("UI/Commands/flushVirtualBuffer", false) ?? BaseContent.WhiteTex;

        // 使用 Postfix 返回 IEnumerable 的经典写法
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Zone_Growing __instance)
        {
            // 1. 先把原版的按钮（如允许播种、选择植物）全部 yield return 出去
            foreach (var gizmo in values)
            {
                yield return gizmo;
            }

            var comp = __instance.Map.GetComponent<FullyAutomaticGrowingZoneManager>();
            if (comp == null) yield break;

            // 自动播种开关
            yield return new Command_Toggle
            {
                defaultLabel = "自动播种",
                defaultDesc = "开启后，本区域将自动播种作物。",
                icon = IconAutoSow,
                isActive = () => comp.autoSowZones.Contains(__instance),
                toggleAction = () =>
                {
                    if (comp.autoSowZones.Contains(__instance))
                    {
                        comp.autoSowZones.Remove(__instance);
                    }
                    else
                    {
                        comp.autoSowZones.Add(__instance);
                        foreach (IntVec3 cell in __instance.Cells)
                        {
                            comp.activeCellsToSow.Enqueue(cell);
                        }
                    }

                    comp.RebuildActiveCache();
                }
            };

            // 自动收获开关
            yield return new Command_Toggle
            {
                defaultLabel = "自动收获",
                defaultDesc = "开启后，本区域将自动收割成熟作物。",
                icon = IconAutoHarvest,
                isActive = () => comp.autoHarvestZones.Contains(__instance),
                toggleAction = () =>
                {
                    if (comp.autoHarvestZones.Contains(__instance))
                    {
                        comp.autoHarvestZones.Remove(__instance);
                    }
                    else
                    {
                        comp.autoHarvestZones.Add(__instance);
                    }

                    comp.RebuildActiveCache();
                }
            };

            // 自动存储开关
            yield return new Command_Toggle
            {
                defaultLabel = "自动存储",
                defaultDesc = "开启后，收获的作物将自动搬运到仓库。关闭则直接掉落在田地上。",
                icon = IconAutoStore,
                isActive = () => comp.autoStoreZones.Contains(__instance),
                toggleAction = () =>
                {
                    if (comp.autoStoreZones.Contains(__instance))
                    {
                        comp.autoStoreZones.Remove(__instance);
                        // 关闭时吐出虚拟仓库中的剩余物资
                        comp.ForceDropAllBuffer(__instance.Cells.First());
                    }
                    else
                    {
                        comp.autoStoreZones.Add(__instance);
                    }

                    comp.RebuildActiveCache();
                }
            };

            // 强制刷出虚拟仓库
            if (comp.autoStoreZones.Contains(__instance))
            {
                yield return new Command_Action
                {
                    defaultLabel = "刷出虚拟仓库",
                    defaultDesc = "立即将虚拟仓库中暂存的所有物资刷出到仓库或田地上。",
                    icon = IconFlushBuffer,
                    action = () =>
                    {
                        comp.FlushVirtualBuffer();
                    }
                };
            }
        }
    }
}

