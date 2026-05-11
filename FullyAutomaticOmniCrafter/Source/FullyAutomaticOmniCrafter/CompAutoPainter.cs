using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // 1. 定义Comp的属性类 (用于在XML中挂载)
    public class CompProperties_AutoPainter : CompProperties
    {
        public CompProperties_AutoPainter()
        {
            this.compClass = typeof(CompAutoPainter);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompAutoPainterTex
    {
        public static readonly Texture2D IconPaint =
            ContentFinder<Texture2D>.Get("UI/Commands/RealityWeaver_Paint", true) ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconRemovePaint =
            ContentFinder<Texture2D>.Get("UI/Commands/RealityWeaver_RemovePaint", true) ?? BaseContent.WhiteTex;
    }

    // 2. 核心Comp类
    public class CompAutoPainter : ThingComp
    {
        public CompProperties_AutoPainter Props => (CompProperties_AutoPainter)this.props;

        // 生成底部操作按钮 (Gizmos)
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // 继承原有的按钮
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 按钮：一键粉饰
            yield return new Command_Action
            {
                defaultLabel = "RealityWeaver_Paint".Translate(),
                defaultDesc = "RealityWeaver_PaintDesc".Translate(),
                // 你可以替换为你自己的贴图路径，这里使用游戏原版的设计器图标
                icon = CompAutoPainterTex.IconPaint, 
                action = CompletePaintDesignations
            };

            // 按钮：一键移除涂料
            yield return new Command_Action
            {
                defaultLabel = "RealityWeaver_RemovePaint".Translate(),
                defaultDesc = "RealityWeaver_RemovePaintDesc".Translate(),
                icon = CompAutoPainterTex.IconRemovePaint,
                action = CompleteRemovePaintDesignations
            };
        }

        // 自动完成粉饰的逻辑
        private void CompletePaintDesignations()
        {
            if (this.parent.Map == null) return;
            Map map = this.parent.Map;

            // 获取原版的粉饰地板蓝图Def
            DesignationDef paintDef = DefDatabase<DesignationDef>.GetNamedSilentFail("PaintFloor");
            if (paintDef == null) return;

            // 使用 ToList() 复制一份列表，防止在遍历时因为移除元素导致集合修改异常
            List<Designation> designations = map.designationManager.SpawnedDesignationsOfDef(paintDef).ToList();
            int successCount = 0;

            foreach (Designation des in designations)
            {
                IntVec3 cell = des.target.Cell;
                ColorDef color = des.colorDef; // 玩家选择的颜色存在蓝图中

                if (cell.InBounds(map) && color != null)
                {
                    // 1. 修改地形颜色
                    map.terrainGrid.SetTerrainColor(cell, color);
                    // 2. 移除规划标记
                    map.designationManager.RemoveDesignation(des);
                    successCount++;
                }
            }

            // 在左上角发送提示消息
            Messages.Message("RealityWeaver_PaintOk".Translate(successCount), MessageTypeDefOf.TaskCompletion, false);
        }

        // 自动完成移除涂料的逻辑
        private void CompleteRemovePaintDesignations()
        {
            if (this.parent.Map == null) return;
            Map map = this.parent.Map;

            // 获取原版的移除涂料蓝图Def
            DesignationDef removeDef = DefDatabase<DesignationDef>.GetNamedSilentFail("RemovePaintFloor");
            if (removeDef == null) return;

            List<Designation> designations = map.designationManager.SpawnedDesignationsOfDef(removeDef).ToList();
            int successCount = 0;

            foreach (Designation des in designations)
            {
                IntVec3 cell = des.target.Cell;

                if (cell.InBounds(map))
                {
                    // 1. 将颜色设置为 null 即可移除涂料
                    map.terrainGrid.SetTerrainColor(cell, null);
                    // 2. 移除规划标记
                    map.designationManager.RemoveDesignation(des);
                    successCount++;
                }
            }

            Messages.Message("RealityWeaver_RemovePaintOk".Translate(successCount), MessageTypeDefOf.TaskCompletion, false);
        }
    }
}