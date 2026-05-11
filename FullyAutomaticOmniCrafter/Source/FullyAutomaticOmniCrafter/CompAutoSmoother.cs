using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // 1. 定义 CompProperties
    public class CompProperties_AutoSmoother : CompProperties
    {
        public CompProperties_AutoSmoother()
        {
            this.compClass = typeof(CompAutoSmoother);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompAutoSmootherTex
    {
        public static readonly Texture2D IconSmoothSurface =
            ContentFinder<Texture2D>.Get("UI/Commands/RealityWeaver_SmoothSurface", true) ?? BaseContent.WhiteTex;
    }

    // 2. 定义核心的 ThingComp
    public class CompAutoSmoother : ThingComp
    {
        // 这个方法用于在游戏界面底部生成按钮 (Gizmo)
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // 返回建筑原本自带的其他按钮
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 返回我们的“一键打磨”自定义按钮
            yield return new Command_Action
            {
                defaultLabel = "RealityWeaver_SmoothSurface".Translate(),
                defaultDesc = "RealityWeaver_SmoothSurfaceDesc".Translate(),
                // 使用原版打磨工具的图标
                icon = CompAutoSmootherTex.IconSmoothSurface, 
                action = DoAutoSmooth
            };
        }

        // 核心执行逻辑
        private void DoAutoSmooth()
        {
            Map map = this.parent.Map;
            if (map == null) return;

            DesignationManager desMan = map.designationManager;

            // 关键：获取所有“打磨”规划。
            // 必须使用 .ToList() 创建一个副本，因为我们在接下来的循环中会调用 RemoveDesignation 删除规划。
            // 如果直接遍历原集合并执行删除，会触发“集合已修改”的报错。
            List<Designation> smoothDesignations = desMan.AllDesignations
                .Where(d => d.def == DesignationDefOf.SmoothFloor || d.def == DesignationDefOf.SmoothWall)
                .ToList();

            if (smoothDesignations.Count == 0)
            {
                Messages.Message("RealityWeaver_NoSmoothSurface".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            int completedCount = 0;

            foreach (Designation des in smoothDesignations)
            {
                IntVec3 cell = des.target.Cell;

                // --- 步骤 A：处理地面打磨 (Terrain) ---
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                // 如果地形存在，并且它有对应的打磨后地形（smoothedTerrain）
                if (terrain != null && terrain.smoothedTerrain != null)
                {
                    // 直接将该格子的地形设置为打磨后的地形
                    map.terrainGrid.SetTerrain(cell, terrain.smoothedTerrain);
                    completedCount++;
                }

                // --- 步骤 B：处理岩壁打磨 (Things) ---
                // 使用倒序遍历该格子的物体，因为我们可能需要销毁原来的粗糙岩壁
                List<Thing> thingList = cell.GetThingList(map);
                for (int i = thingList.Count - 1; i >= 0; i--)
                {
                    Thing t = thingList[i];
                    // 检查物体是否可以被打磨
                    if (t.def.IsSmoothable && t.def.building != null && t.def.building.smoothedThing != null)
                    {
                        ThingDef smoothedDef = t.def.building.smoothedThing;
                        ThingDef stuff = t.Stuff;
                        Faction faction = t.Faction;
                        Rot4 rotation = t.Rotation;

                        // 销毁旧的粗糙岩壁 (DestroyMode.Vanish 意味着直接消失，不会掉落碎石)
                        t.Destroy(DestroyMode.Vanish);

                        // 生成新的平滑墙壁并放置到地图上
                        Thing smoothedWall = ThingMaker.MakeThing(smoothedDef, stuff);
                        smoothedWall.SetFaction(faction ?? Faction.OfPlayer);
                        GenSpawn.Spawn(smoothedWall, cell, map, rotation);
                        completedCount++;
                    }
                }

                // --- 步骤 C：移除规划标记 ---
                desMan.RemoveDesignation(des);
            }

            // 弹出提示音效和文字
            Messages.Message("RealityWeaver_OkSmoothSurface".Translate(completedCount), MessageTypeDefOf.PositiveEvent, false);
        }
    }
}