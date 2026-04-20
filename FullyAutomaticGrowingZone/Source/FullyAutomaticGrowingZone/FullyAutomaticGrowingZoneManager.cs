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

        // 待播种集合：用 HashSet 天然去重，避免同一格子被重复加入
        public HashSet<IntVec3> pendingCellsToSow = new HashSet<IntVec3>();

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

        // 延迟重试集合：播种失败的格子移入此处，每隔 DeferredRetryInterval ticks 才重新尝试
        public HashSet<IntVec3> deferredCellsToSow = new HashSet<IntVec3>();
        private const int DeferredRetryInterval = 900;

        // 用于遍历 pendingCellsToSow 的临时缓冲，避免每帧分配
        private List<IntVec3> _iterBuffer = new List<IntVec3>();

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
                        pendingCellsToSow.Add(cell);
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

            // 定期将延迟重试集合中的格子移回待播种集合
            if (Find.TickManager.TicksGame % DeferredRetryInterval == 0 && deferredCellsToSow.Count > 0)
            {
                pendingCellsToSow.UnionWith(deferredCellsToSow);
                deferredCellsToSow.Clear();
            }

            // 定期刷出虚拟仓库中未满一组的残余物资，防止超大堆叠mod下物资长期滞留
            if (Find.TickManager.TicksGame % 600 == 0)
            {
                FlushVirtualBuffer();
            }

            // 处理收获队列 —— 提高吞吐量：每帧处理 1/10 而非 1/60，上限 500
            int harvestCountThisTick = Mathf.CeilToInt(plantsToHarvest.Count / 10f);
            harvestCountThisTick = Mathf.Clamp(harvestCountThisTick, 0, 500);

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
                            pendingCellsToSow.Add(plant.Position);
                        }
                    }
                }
            }

            // 处理待播种集合 —— 每帧批量处理，大幅提升吞吐量
            if (pendingCellsToSow.Count > 0)
            {
                int toProcess = Mathf.Min(pendingCellsToSow.Count, 200);

                _iterBuffer.Clear();
                int count = 0;
                foreach (IntVec3 cell in pendingCellsToSow)
                {
                    _iterBuffer.Add(cell);
                    if (++count >= toProcess) break;
                }

                for (int i = 0; i < _iterBuffer.Count; i++)
                {
                    IntVec3 cell = _iterBuffer[i];
                    ThingDef plantDef = GetPlantDefForCell(cell);

                    if (plantDef == null)
                    {
                        // 格子已不在自动区内，移除
                        pendingCellsToSow.Remove(cell);
                        continue;
                    }

                    // 【无法播种的格子在此处理】：CanAutoSowAndClear 返回 false 时（如非生长季节、肥力不足、
                    // 格子上有建筑/物品等），将格子移入延迟重试集合，避免每 tick 反复检查造成性能浪费。
                    if (CanAutoSowAndClear(plantDef, cell, map))
                    {
                        ExecuteSow(cell, plantDef);
                        pendingCellsToSow.Remove(cell); // 播种成功，从待播种集合中移除
                    }
                    else
                    {
                        // 播种失败，移入延迟重试集合，等待 DeferredRetryInterval ticks 后再重试
                        pendingCellsToSow.Remove(cell);
                        deferredCellsToSow.Add(cell);
                    }
                }
            }
        }

        public void ExecuteHarvest(Plant plant)
        {
            ThingDef harvestedThingDef = plant.def.plant.harvestedThingDef;
            int yieldAmount = plant.YieldNow();
            IntVec3 position = plant.Position;
            bool shouldStore = IsAutoStore(position);

            plant.Destroy(DestroyMode.Vanish);

            if (harvestedThingDef != null && yieldAmount > 0)
            {
                if (shouldStore)
                {
                    if (!virtualYieldBuffer.ContainsKey(harvestedThingDef))
                        virtualYieldBuffer[harvestedThingDef] = 0;

                    virtualYieldBuffer[harvestedThingDef] += yieldAmount;

                    int stackLimit = harvestedThingDef.stackLimit;
                    int flushThreshold = Mathf.Min(stackLimit, Mathf.Max(500, stackLimit / 10));
                    while (virtualYieldBuffer[harvestedThingDef] >= flushThreshold)
                    {
                        int spawnCount = Mathf.Min(virtualYieldBuffer[harvestedThingDef], stackLimit);
                        Thing fullStack = ThingMaker.MakeThing(harvestedThingDef);
                        fullStack.stackCount = spawnCount;
                        TryPlaceInStockpile(fullStack, position);
                        virtualYieldBuffer[harvestedThingDef] -= spawnCount;
                    }
                }
                else
                {
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

        private bool TryPlaceInStockpile(Thing thing, IntVec3 fallbackCell)
        {
            GenPlace.TryPlaceThing(thing, fallbackCell, map, ThingPlaceMode.Near);

            if (!thing.Destroyed && thing.Spawned)
            {
                if (StoreUtility.TryFindBestBetterStoreCellFor(thing, null, map, StoragePriority.Unstored, null,
                        out IntVec3 storeCell))
                {
                    thing.DeSpawn();
                    GenPlace.TryPlaceThing(thing, storeCell, map, ThingPlaceMode.Near);
                }
            }

            return true;
        }

        public void FlushVirtualBuffer()
        {
            foreach (var def in virtualYieldBuffer.Keys.ToList())
            {
                int amount = virtualYieldBuffer[def];
                if (amount <= 0) continue;

                IntVec3 fallbackCell = IntVec3.Invalid;
                foreach (var zone in autoStoreZones)
                {
                    if (zone != null && zone.Cells.Count > 0)
                    {
                        fallbackCell = zone.Cells.First();
                        break;
                    }
                }

                if (!fallbackCell.IsValid) break;

                Thing stack = ThingMaker.MakeThing(def);
                stack.stackCount = amount;
                TryPlaceInStockpile(stack, fallbackCell);
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
            Zone zone = map.zoneManager.ZoneAt(cell);

            if (zone is Zone_Growing growingZone)
            {
                if (autoSowZones.Contains(growingZone))
                {
                    ThingDef plantDefToGrow = growingZone.GetPlantDefToGrow();
                    if (plantDefToGrow != null)
                    {
                        if (CanAutoSowAndClear(plantDefToGrow, cell, map))
                        {
                            Plant newPlant = (Plant)GenSpawn.Spawn(plantDefToGrow, cell, map);
                            newPlant.Growth = Plant.BaseSownGrowthPercent;
                            newPlant.sown = true;
                        }
                    }
                }
            }
        }

        public ThingDef GetPlantDefForCell(IntVec3 cell)
        {
            Zone zone = map.zoneManager.ZoneAt(cell);

            if (zone is Zone_Growing growingZone)
            {
                if (autoSowZones.Contains(growingZone))
                {
                    return growingZone.GetPlantDefToGrow();
                }
            }

            return null;
        }

        public bool CanAutoSowAndClear(ThingDef plantDef, IntVec3 c, Map map)
        {
            if (!PlantUtility.GrowthSeasonNow(c, map, plantDef))
            {
                return false;
            }

            if (plantDef.plant.fertilityMin > 0f && map.fertilityGrid.FertilityAt(c) < plantDef.plant.fertilityMin)
            {
                return false;
            }

            List<Thing> thingList = c.GetThingList(map);

            for (int i = thingList.Count - 1; i >= 0; i--)
            {
                Thing thing = thingList[i];

                if (thing.def.category == ThingCategory.Building || thing.def.category == ThingCategory.Item)
                {
                    return false;
                }

                if (thing.def.category == ThingCategory.Plant)
                {
                    if (thing.def == plantDef) return false;
                    thing.Destroy(DestroyMode.Vanish);
                }
            }

            return true;
        }

        // ===========================================================

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

        public bool TryEnqueueHarvest(Plant plant)
        {
            if (plantsInHarvestQueue.Add(plant.thingIDNumber))
            {
                plantsToHarvest.Enqueue(plant);
                return true;
            }

            return false;
        }

        public void OnPlantDefChanged(Zone_Growing zone)
        {
            if (autoSowZones.Contains(zone))
            {
                foreach (IntVec3 cell in zone.Cells)
                {
                    pendingCellsToSow.Add(cell);
                }
            }
        }
    }

    // =====================================================
    // 关键修复：当植物被任何原因 DeSpawn（火灾、战斗、其他mod等）时，
    // 自动将该格子重新加入播种队列
    // =====================================================
    [HarmonyPatch(typeof(Plant), "DeSpawn")]
    public static class Plant_DeSpawn_Patch
    {
        public static void Prefix(Plant __instance)
        {
            if (__instance.Spawned && __instance.Map != null)
            {
                var comp = __instance.Map.GetComponent<FullyAutomaticGrowingZoneManager>();
                if (comp != null && comp.IsAutoSow(__instance.Position))
                {
                    comp.pendingCellsToSow.Add(__instance.Position);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Plant), "TickLong")]
    public static class Plant_TickLong_Patch
    {
        public static void Postfix(Plant __instance)
        {
            if (__instance.Destroyed || __instance.Map == null) return;

            if (__instance.Growth >= 1f)
            {
                var comp = __instance.Map.GetComponent<FullyAutomaticGrowingZoneManager>();
                if (comp != null)
                {
                    if (comp.IsAutoZone(__instance.Position) && comp.TryEnqueueHarvest(__instance))
                    {
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
                comp.pendingCellsToSow.Add(c);
            }
        }
    }

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


    [StaticConstructorOnStartup]
    public static class FullyAutomaticGrowingZoneTex
    {
        public static readonly Texture2D IconAutoHarvest =
            ContentFinder<Texture2D>.Get("UI/Commands/autoHarvestGrowingZone", true) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconAutoSow =
            ContentFinder<Texture2D>.Get("UI/Commands/autoSowGrowingZone", true) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconAutoStore =
            ContentFinder<Texture2D>.Get("UI/Commands/autoStoreGrowingZone", true) ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconFlushBuffer =
            ContentFinder<Texture2D>.Get("UI/Commands/flushVirtualBufferGrowingZone", true) ?? BaseContent.WhiteTex;
    }

    [HarmonyPatch(typeof(Zone_Growing), "GetGizmos")]
    public static class Zone_Growing_GetGizmos_Patch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Zone_Growing __instance)
        {
            foreach (var gizmo in values)
            {
                yield return gizmo;
            }

            var comp = __instance.Map.GetComponent<FullyAutomaticGrowingZoneManager>();
            if (comp == null) yield break;

            yield return new Command_Toggle
            {
                defaultLabel = "FullyAutomaticGrowingZone_autoSow".Translate(),
                defaultDesc = "FullyAutomaticGrowingZone_autoSowDesc".Translate(),
                icon = FullyAutomaticGrowingZoneTex.IconAutoSow,
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
                            comp.pendingCellsToSow.Add(cell);
                        }
                    }

                    comp.RebuildActiveCache();
                }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "FullyAutomaticGrowingZone_autoHarvest".Translate(),
                defaultDesc = "FullyAutomaticGrowingZone_autoHarvestDesc".Translate(),
                icon = FullyAutomaticGrowingZoneTex.IconAutoHarvest,
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

            yield return new Command_Toggle
            {
                defaultLabel = "FullyAutomaticGrowingZone_autoStore".Translate(),
                defaultDesc = "FullyAutomaticGrowingZone_autoStoreDesc".Translate(),
                icon = FullyAutomaticGrowingZoneTex.IconAutoStore,
                isActive = () => comp.autoStoreZones.Contains(__instance),
                toggleAction = () =>
                {
                    if (comp.autoStoreZones.Contains(__instance))
                    {
                        comp.autoStoreZones.Remove(__instance);
                        comp.ForceDropAllBuffer(__instance.Cells.First());
                    }
                    else
                    {
                        comp.autoStoreZones.Add(__instance);
                    }

                    comp.RebuildActiveCache();
                }
            };

            if (comp.autoStoreZones.Contains(__instance))
            {
                yield return new Command_Action
                {
                    defaultLabel = "FullyAutomaticGrowingZone_flushVirtualBuffer".Translate(),
                    defaultDesc = "FullyAutomaticGrowingZone_flushVirtualBufferDesc".Translate(),
                    icon = FullyAutomaticGrowingZoneTex.IconFlushBuffer,
                    action = () => { comp.FlushVirtualBuffer(); }
                };
            }
        }
    }
}