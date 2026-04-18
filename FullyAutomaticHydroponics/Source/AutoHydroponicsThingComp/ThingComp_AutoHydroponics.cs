using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;


// 让一个真正种植了原版植物的水培盆实现“自动收获”和“自动重新种植”
// 编写自定义组件 (C# ThingComp) —— 最正规且主流的做法
//
// RimWorld 的物品（Thing）可以通过挂载组件（Comp）来拓展功能。你可以用 C# 写一个专门用于水培盆的组件，例如 CompAutoPlanter。
//
//     工作原理：
//
//         让这个组件使用 CompTickRare() 或 CompTickLong()，让游戏每隔一段时间（比如 250 tick 或 2000 tick）唤醒一次这个水培盆。
//
//         水培盆被唤醒后，扫描自己所在的格子上是否有植物（Plant）。
//
//         自动收获：检查该植物的生长进度（plant.Growth）。如果达到 1.0（100% 成熟），通过代码生成植物的产物（ThingMaker.MakeThing）并掉落在水培盆旁边。
//
//         自动重种：将刚刚那株植物销毁，然后读取水培盆当前设置的种植计划（IPlantToGrowSettable.GetPlantDefToGrow()），直接在原位生成一株进度为 0% 的新植物。
//
//     优点：逻辑清晰，只对挂载了该组件的特定水培盆生效，不影响原版普通农田，性能消耗可控。
//
//
// 简化的 C# 逻辑概念演示
// ```C#
// public override void CompTickRare()
// {
//     base.CompTickRare();
//
//     // 1. 获取水培盆位置
//     IntVec3 pos = this.parent.Position;
//     Map map = this.parent.Map;
//
//     // 2. 找到上面的植物
//     Plant plant = pos.GetPlant(map);
//
//     // 3. 判断是否成熟
//     if (plant != null && plant.Growth >= 1.0f)
//     {
//         // 掉落产物
//         int yieldCount = plant.YieldNow();
//         Thing yieldThing = ThingMaker.MakeThing(plant.def.plant.harvestedThingDef);
//         yieldThing.stackCount = yieldCount;
//         GenPlace.TryPlaceThing(yieldThing, pos, map, ThingPlaceMode.Near);
//
//         // 销毁老植物
//         plant.Destroy();
//
//         // 4. 获取设定的植物类型并种下新植物
//         ThingDef plantDefToGrow = ((Building_PlantGrower)this.parent).GetPlantDefToGrow();
//         if (plantDefToGrow != null)
//         {
//             Plant newPlant = (Plant)GenSpawn.Spawn(plantDefToGrow, pos, map);
//             newPlant.Growth = 0f; // 从零开始
//         }
//     }
// }
// ```
//

// ```
// <comps>
//   <li Class="AutoHydroponicsThingComp.CompProperties_AutoHydroponics">
//     <defaultAutoHarvest>true</defaultAutoHarvest>
//     <defaultAutoSow>false</defaultAutoSow>
//   </li>
// </comps>
// ```

namespace FullyAutoHydroponicsThingComp
{
    // 新增属性类
    public class CompProperties_FullyAutoHydroponics : CompProperties
    {
        // 可在 XML Def 中配置的默认开关值
        public bool defaultAutoHarvest = true;
        public bool defaultAutoSow = true;

        public CompProperties_FullyAutoHydroponics()
        {
            // 将这个属性类与你的逻辑类绑定
            this.compClass = typeof(ThingComp_FullyAutoHydroponics);
        }
    }


    // 继承自 ThingComp，作为挂载在水培盆建筑上的自定义组件
    public class ThingComp_FullyAutoHydroponics : ThingComp
    {
        // ── 缓存的图标贴图 ──
        private static readonly Texture2D IconAutoHarvest =
            ContentFinder<Texture2D>.Get("UI/Commands/autoHarvest", false) ?? BaseContent.WhiteTex;

        private static readonly Texture2D IconAutoSow =
            ContentFinder<Texture2D>.Get("UI/Commands/autoSow", false) ?? BaseContent.WhiteTex;

        // ── 持久化开关字段 ──
        // 是否启用自动收获功能（默认值由 CompProperties 决定）
        public bool autoHarvest;

        // 是否启用自动耕种功能（默认值由 CompProperties 决定）
        public bool autoSow;

        private CompProperties_FullyAutoHydroponics Props => (CompProperties_FullyAutoHydroponics)props;

        // 首次生成时从 Props 读取默认值
        public override void PostPostMake()
        {
            base.PostPostMake();
            autoHarvest = Props.defaultAutoHarvest;
            autoSow = Props.defaultAutoSow;
        }

        // ── 存档序列化：让两个开关随存档持久化 ──
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref autoHarvest, "autoHarvest", Props.defaultAutoHarvest);
            Scribe_Values.Look(ref autoSow, "autoSow", Props.defaultAutoSow);
        }

        // ── UI 按钮：在选中水培盆时显示开关 Gizmo ──
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // 自动收获开关
            yield return new Command_Toggle
            {
                defaultLabel = "FullyAutoHydroponics_autoHarvest".Translate(),
                defaultDesc = "FullyAutoHydroponics_autoHarvestDesc".Translate(),
                icon = IconAutoHarvest,
                isActive = () => autoHarvest,
                toggleAction = () =>
                {
                    autoHarvest = !autoHarvest;
                    // Log.Message($"[AutoHydroponics] {parent.ThingID} 自动收获 -> {autoHarvest}");
                }
            };

            // 自动耕种开关
            yield return new Command_Toggle
            {
                defaultLabel = "FullyAutoHydroponics_autoSow".Translate(),
                defaultDesc = "FullyAutoHydroponics_autoSowDesc".Translate(),
                icon = IconAutoSow,
                isActive = () => autoSow,
                toggleAction = () =>
                {
                    autoSow = !autoSow;
                    // Log.Message($"[AutoHydroponics] {parent.ThingID} 自动耕种 -> {autoSow}");
                }
            };
        }

        // 重写 CompTickRare()，每 250 tick 由游戏自动调用一次
        public override void CompTickRare()
        {
            // 调用父类的 CompTickRare，保留原有逻辑
            base.CompTickRare();

            // 如果自动耕种和自动收获均未启用，则跳过。减少无效的计算和对象访问。
            if (!autoSow && !autoHarvest)
                return;

            // 如果宿主建筑尚未生成到地图上（例如正在建造中），则跳过本次处理
            if (!parent.Spawned)
                return;

            // 尝试将宿主建筑转型为 Building_PlantGrower（水培盆）；若不是则跳过
            Building_PlantGrower grower = parent as Building_PlantGrower;
            if (grower == null)
                return;

            // 若水培盆没有电力供应，则不执行任何自动操作
            if (!grower.CanAcceptSowNow())
                return;

            // ── 阶段一：收获已成熟的植物 ──
            // 遍历水培盆上所有格子中的植物（ToList() 防止在遍历中修改集合导致异常）
            if (autoHarvest)
            {
                foreach (Plant plant in grower.PlantsOnMe.ToList())
                {
                    // 跳过已经被销毁的植物（防止访问无效对象）
                    if (plant == null || plant.Destroyed)
                        continue;

                    // 如果植物尚未完全成熟（生长度 < 100%），跳过，等待下次检查
                    if (plant.Growth < 1.0f)
                        continue;

                    // 记录植物当前所在的地图格坐标
                    IntVec3 pos = plant.Position;
                    // 记录植物所在的地图对象
                    Map map = plant.Map;

                    // 如果植物没有 plant 属性定义，则跳过（防御性检查）
                    if (plant.def.plant == null)
                        continue;

                    // 仅当该植物有定义的收获物时才进行收获
                    if (plant.def.plant.harvestedThingDef != null)
                    {
                        // 计算当前植物在现有生长度和血量下的实际产量
                        int yieldCount = plant.YieldNow();
                        // 只有产量大于 0 时才生成收获物，避免创建无意义的空物品
                        if (yieldCount > 0)
                        {
                            // 根据植物定义的收获物类型创建一个新的 Thing 实例
                            Thing yieldThing = ThingMaker.MakeThing(plant.def.plant.harvestedThingDef);
                            // 设置收获物的数量
                            yieldThing.stackCount = yieldCount;
                            // 将收获物放置到水培盆附近（Near 模式会自动寻找最近的可放置位置）
                            GenPlace.TryPlaceThing(yieldThing, pos, map, ThingPlaceMode.Near);
                        }
                    }

                    // 判断该植物是否为多年生植物（即收获后不销毁，而是恢复到一定生长度继续生长）
                    // HarvestDestroys 为 true 表示 harvestAfterGrowth <= 0，即一年生植物，需要销毁并重种
                    // HarvestDestroys 为 false 表示多年生植物（如莓果），收获后将生长度重置为 harvestAfterGrowth 即可
                    if (plant.def.plant.HarvestDestroys)
                    {
                        // ── 一年生植物：销毁旧植物，在原位重新种一株新植物 ──

                        // 销毁已收获完毕的旧植物，释放该格子以便重新种植
                        plant.Destroy();

                        if (!autoSow)
                            continue;
                        // 从水培盆中读取玩家设定的目标种植植物类型
                        ThingDef plantDefToGrow = grower.GetPlantDefToGrow();
                        // 只有种植类型有效时才继续（防止种植计划为空时生成错误）
                        if (plantDefToGrow != null)
                        {
                            // 修复：使用 BaseSownGrowthPercent 并设置 sown=true，
                            // 确保植物进入 Growing 阶段（而非 Sowing 阶段），
                            // 从而显示正常幼苗图像且能继续生长。
                            Plant newPlant = (Plant)GenSpawn.Spawn(plantDefToGrow, pos, map);
                            newPlant.Growth = Plant.BaseSownGrowthPercent;
                            newPlant.sown = true;
                        }
                    }
                    else
                    {
                        // ── 多年生植物（如莓果）：不销毁，仅将生长度回退到 harvestAfterGrowth，让其继续生长 ──

                        // 检查当前种植的植物是否与玩家设定的目标植物一致
                        ThingDef plantDefToGrow = grower.GetPlantDefToGrow();
                        if (plantDefToGrow != null && plant.def != plantDefToGrow)
                        {
                            // 植物种类不一致：销毁当前植物，在原位种植正确种类的植物
                            plant.Destroy();

                            if (!autoSow)
                                continue;

                            // 检查生长季节（温度等条件）
                            if (!PlantUtility.GrowthSeasonNow(pos, map, plantDefToGrow))
                                continue;

                            Plant newPlant = (Plant)GenSpawn.Spawn(plantDefToGrow, pos, map);
                            newPlant.Growth = Plant.BaseSownGrowthPercent;
                            newPlant.sown = true;
                        }
                        else
                        {
                            // 将生长进度设回收获后的初始值（由植物 Def 定义，例如 0.08 表示 8%）
                            plant.Growth = plant.def.plant.harvestAfterGrowth;

                            // 标记该格子的地图网格需要重绘，以更新植物的外观（与原版 PlantCollected 逻辑一致）
                            map.mapDrawer.MapMeshDirty(pos, MapMeshFlagDefOf.Things);
                        }
                    }
                }
            }

            // ── 阶段二：自动种植——对所有没有植物的空格进行补种 ──
            if (!autoSow)
                return;
            ThingDef defToGrow = grower.GetPlantDefToGrow();
            if (defToGrow == null)
                return;

            foreach (IntVec3 cell in grower.OccupiedRect())
            {
                // 若该格已有植物则跳过
                if (cell.GetPlant(grower.Map) != null)
                    continue;

                // 检查生长季节（温度等条件）
                if (!PlantUtility.GrowthSeasonNow(cell, grower.Map, defToGrow))
                    continue;

                Plant sownPlant = (Plant)GenSpawn.Spawn(defToGrow, cell, grower.Map);
                sownPlant.Growth = Plant.BaseSownGrowthPercent;
                sownPlant.sown = true;
            }
        }
    }
}