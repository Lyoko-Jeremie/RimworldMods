using System.Collections.Generic;
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

        // 休眠池：因为任何原因（温度、毒物、物理阻塞）暂时无法播种的格子。
        // 使用 List 是为了实现 O(1) 的随机访问。
        private List<IntVec3> sleepingCells = new List<IntVec3>();

        // 在你的 MapComponent 中，只需要记录“哪些种植区开启了自动功能”
        // 因为玩家的种植区数量通常是个位数或十位数，HashSet 的性能在这里是完美的
        public HashSet<Zone_Growing> activeAutoZones = new HashSet<Zone_Growing>();

        // 等待收获的植物队列。
        public Queue<Plant> plantsToHarvest = new Queue<Plant>();

        // 虚拟仓库：记录每种作物的暂存数量
        // 比如：{ 物品Def: 水稻, 数量: 45 }
        public Dictionary<ThingDef, int> virtualYieldBuffer = new Dictionary<ThingDef, int>();

        public override void ExposeData()
        {
            base.ExposeData();
            // 别忘了把这个字典存进存档，否则玩家读档会丢失零碎的农产品
            Scribe_Collections.Look(ref virtualYieldBuffer, "virtualYieldBuffer", LookMode.Def, LookMode.Value);
            // 初始化字典防止读档报错
            if (Scribe.mode == LoadSaveMode.PostLoadInit && virtualYieldBuffer == null)
            {
                virtualYieldBuffer = new Dictionary<ThingDef, int>();
            }
        }

        // MapComponent 自带每 tick 执行一次的心跳
        public override void MapComponentTick()
        {
            base.MapComponentTick();

            // ==========================================
            // 高效触发“自动收获”
            // 原版的所有植物（Plant）都会执行 TickLong()（每 2000 ticks 执行一次）。你可以用 Harmony 写一个 Postfix 补丁挂在 Plant.TickLong 上。
            // 逻辑： 每次植物更新生长进度后，检查它是否 Growth >= 1f（成熟）。如果是，并且它所在的格子属于你的 autoZones，就把这个植物加入到 plantsToHarvest 队列中。
            // 性能优势： 极大地分散了计算量，只在植物本来就需要更新状态的瞬间顺便做一次轻量级判断。
            // ==========================================
            // TODO

            // 假设我们希望积压的任务最多在 60 Ticks（1秒）内处理完
            int harvestCountThisTick = Mathf.CeilToInt(plantsToHarvest.Count / 60f);
            // 设定一个硬性上限，防止极端情况单帧卡死（比如一次最多处理 100 个）
            harvestCountThisTick = Mathf.Clamp(harvestCountThisTick, 0, 100);

            for (int i = 0; i < harvestCountThisTick; i++)
            {
                if (plantsToHarvest.TryDequeue(out Plant plant))
                {
                    if (plant != null && !plant.Destroyed && plant.Growth >= 1f)
                    {
                        ExecuteHarvest(plant);
                        activeCellsToSow.Enqueue(plant.Position);
                    }
                }
            }

            // ==========================================
            // 处理活跃队列 (刚空出来的格子)
            // ==========================================
            int activeToProcess = Mathf.Min(activeCellsToSow.Count, 10); // 每帧最多处理 10 个
            for (int i = 0; i < activeToProcess; i++)
            {
                IntVec3 cell = activeCellsToSow.Dequeue();

                // 获取该格子的植物要求 (参考之前的延迟查询逻辑)
                ThingDef plantDef = GetPlantDefForCell(cell);
                if (plantDef != null)
                {
                    // 使用原版的终极校验
                    if (CanAutoSowAndClear(plantDef, cell, map))
                    {
                        ExecuteSow(cell, plantDef);
                    }
                    else
                    {
                        // 核心：不管因为什么原因失败，直接扔进休眠池！
                        sleepingCells.Add(cell);
                    }
                }
            }

            // ==========================================
            // 随机乱步重试休眠池 (解决复杂阻塞)
            // ==========================================
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
                    if (plantDef != null && CanAutoSowAndClear(plantDef, cell, map))
                    {
                        // 阻塞解除了！(石头被搬走 / 温度回暖了)
                        ExecuteSow(cell, plantDef);

                        // 极其关键的 O(1) 移除技巧 (Fast Remove)
                        // 不要用 RemoveAt(randomIndex)，那会导致数组移位产生巨大的 CPU 开销
                        // 我们把最后一个元素挪到当前位置，然后删掉最后一个元素
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

            // 3. 极其关键：无痕销毁植物！
            // 必须使用 DestroyMode.Vanish。这会让植物直接从内存中抹除，
            // 不会触发任何原版的掉落、死亡特效或声音，极大地节省了 CPU 开销。
            plant.Destroy(DestroyMode.Vanish);

            // 4. 将产物放入虚拟仓库进行“合并堆叠”
            if (harvestedThingDef != null && yieldAmount > 0)
            {
                // 确保字典里有这个作物的键
                if (!virtualYieldBuffer.ContainsKey(harvestedThingDef))
                {
                    virtualYieldBuffer[harvestedThingDef] = 0;
                }

                // 产物入库
                virtualYieldBuffer[harvestedThingDef] += yieldAmount;

                // 5. 满仓吐出逻辑（Batch Spawning）
                int stackLimit = harvestedThingDef.stackLimit;

                // 只要虚拟仓库里的数量大于一组的最大堆叠量（比如水稻是 75）
                while (virtualYieldBuffer[harvestedThingDef] >= stackLimit)
                {
                    // 在内存中制造一组满堆叠的物品
                    Thing fullStack = ThingMaker.MakeThing(harvestedThingDef);
                    fullStack.stackCount = stackLimit;

                    // 将这满堆叠的物品安全地生成在刚刚被收割的植物的位置附近
                    GenPlace.TryPlaceThing(fullStack, plant.Position, map, ThingPlaceMode.Near);

                    // 从虚拟仓库中扣除数量
                    virtualYieldBuffer[harvestedThingDef] -= stackLimit;
                }
            }
        }

        // 当玩家关闭某个区的自动功能时，可以选择把账本里剩余的零碎物资全部吐出来
        public void ForceDropAllBuffer()
        {
            foreach (var kvp in virtualYieldBuffer)
            {
                ThingDef def = kvp.Key;
                int amount = kvp.Value;

                if (amount > 0)
                {
                    Thing leftovers = ThingMaker.MakeThing(def);
                    leftovers.stackCount = amount;
                    // TODO 找个好地方扔出来，比如玩家基地的降落区，或者当前区的随便一个有效格子
                    // GenPlace.TryPlaceThing(...)

                    virtualYieldBuffer[def] = 0;
                }
            }
        }

        public void ExecuteSow(IntVec3 cell, ThingDef plantDef)
        {
            // 1. O(1) 极速查询该格子当前所属的 Zone
            Zone zone = map.zoneManager.ZoneAt(cell);

            // 2. 校验：这个格子还在种植区里吗？它是普通的种植区吗？
            if (zone is Zone_Growing growingZone)
            {
                // 3. 校验：这个种植区开启了你的“自动功能”吗？
                if (activeAutoZones.Contains(growingZone))
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
                        // else
                        // {
                        //     // 如果因为温度/毒物等原因暂时不能种，可以把它重新塞回队列尾部，或者过一段时间再重试
                        //     // sleepingCells.Add(cell); 
                        // }
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
                // 校验：玩家是否为这个区开启了自动功能？
                // activeAutoZones 是你在 MapComponent 里维护的 HashSet<Zone_Growing>
                if (activeAutoZones.Contains(growingZone))
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
            {
                return activeAutoZones.Contains(growingZone);
            }

            return false;
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
                    // 4. O(1) 极速校验该格子是否属于自动种植区
                    if (comp.IsAutoZone(__instance.Position))
                    {
                        // 5. 加入收获队列，等待 MapComponentTick 去平滑处理
                        comp.plantsToHarvest.Enqueue(__instance);
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

            // 如果玩家扩建的是一个已经开启了“自动”的种植区
            if (comp != null && comp.activeAutoZones.Contains(__instance))
            {
                // 将新划定的格子加入活跃队列，立刻安排播种
                comp.activeCellsToSow.Enqueue(c);
            }
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

            bool isActive = comp.activeAutoZones.Contains(__instance);

            // 2. 添加我们自己的“切换自动功能”按钮
            yield return new Command_Toggle
            {
                defaultLabel = "自动农场:",
                defaultDesc = "开启后，本区域将自动收割成熟作物并立即播种。",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/YourIcon"), // TODO 你的Mod图标
                isActive = () => isActive,
                toggleAction = () =>
                {
                    if (isActive)
                    {
                        comp.activeAutoZones.Remove(__instance);
                    }
                    else
                    {
                        comp.activeAutoZones.Add(__instance);
                        // 【关键】：刚开启时，将区内现有的所有格子推入活跃队列，进行一次全面扫描
                        foreach (IntVec3 cell in __instance.Cells)
                        {
                            comp.activeCellsToSow.Enqueue(cell);
                        }
                    }
                }
            };

            // // 自动耕种开关
            // yield return new Command_Toggle
            // {
            //     defaultLabel = "FullyAutomaticGrowingZone_autoSow".Translate(),
            //     defaultDesc = "FullyAutomaticGrowingZone_autoSowDesc".Translate(),
            //     icon = IconAutoSow,
            //     isActive = () => autoSow,
            //     toggleAction = () =>
            //     {
            //         autoSow = !autoSow;
            //         UpdateRegistration();
            //     }
            // };
            //
            // // 自动收获开关
            // yield return new Command_Toggle
            // {
            //     defaultLabel = "FullyAutomaticGrowingZone_autoHarvest".Translate(),
            //     defaultDesc = "FullyAutomaticGrowingZone_autoHarvestDesc".Translate(),
            //     icon = IconAutoHarvest,
            //     isActive = () => autoHarvest,
            //     toggleAction = () =>
            //     {
            //         autoHarvest = !autoHarvest;
            //         UpdateRegistration();
            //     }
            // };
            //
            // // 自动存储开关
            // yield return new Command_Toggle
            // {
            //     defaultLabel = "FullyAutomaticGrowingZone_autoStore".Translate(),
            //     defaultDesc = "FullyAutomaticGrowingZone_autoStoreDesc".Translate(),
            //     icon = IconAutoStore,
            //     isActive = () => autoStore,
            //     toggleAction = () =>
            //     {
            //         autoStore = !autoStore;
            //
            //         if (autoStore && Manager != null)
            //         {
            //             // 获取当前盆子里计划种植的作物类型
            //             ThingDef currentPlantDef = (parent as Building_PlantGrower)?.GetPlantDefToGrow()?.plant
            //                 ?.harvestedThingDef;
            //             if (currentPlantDef != null)
            //             {
            //                 Manager.ResetCooldown(currentPlantDef);
            //             }
            //         }
            //
            //         UpdateRegistration();
            //     }
            // };
        }
    }
}