using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
// 1. 属性类：用于在XML中定义和传递参数
    public class CompProperties_AutoRoofer : CompProperties
    {
        public CompProperties_AutoRoofer()
        {
            this.compClass = typeof(CompAutoRoofer);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompAutoRooferTex
    {
        public static readonly Texture2D IconBuildRoof =
            ContentFinder<Texture2D>.Get("UI/Commands/RealityWeaver_BuildRoof", true) ?? BaseContent.WhiteTex;
    }

    // 2. 逻辑类：继承自ThingComp，处理按钮UI和实际建造逻辑
    public class CompAutoRoofer : ThingComp
    {
        public CompProperties_AutoRoofer Props => (CompProperties_AutoRoofer)this.props;

        // 生成底部操作面板的按钮 (Gizmos)
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // 继承原有的Gizmos
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 创建一个执行动作的按钮
            Command_Action action = new Command_Action
            {
                action = ExecuteRoofing,
                defaultLabel = "RealityWeaver_AutoRoofer".Translate(),
                defaultDesc = "RealityWeaver_AutoRooferDesc".Translate(),
                // 使用原版“建造屋顶”的图标
                icon = CompAutoRooferTex.IconBuildRoof,
            };

            yield return action;
        }

        // 按钮点击后执行的实际逻辑
        private void ExecuteRoofing()
        {
            Map map = this.parent.Map;
            if (map == null) return;

            Area buildRoofArea = map.areaManager.BuildRoof;
            Area noRoofArea = map.areaManager.NoRoof;

            int builtCount = 0;
            int removedCount = 0;

            // 软依赖：检查 ExpandedRoofing 的厚屋顶移除研究是否完成。
            // 若 ExpandedRoofing 未加载，GetNamed 返回 null，视为无限制。
            ResearchProjectDef thickRoofRemovalResearch =
                DefDatabase<ResearchProjectDef>.GetNamed("ThickStoneRoofRemoval", false);
            bool canRemoveThickRoof = thickRoofRemovalResearch == null || thickRoofRemovalResearch.IsFinished;

            foreach (IntVec3 cell in map.AllCells)
            {
                // ====================
                // 1. 处理建造屋顶区（原版人造屋顶）
                // ====================
                if (buildRoofArea != null && buildRoofArea[cell] && !map.roofGrid.Roofed(cell))
                {
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    builtCount++;
                }

                // ====================
                // 2. 处理移除屋顶区
                //    - 天然岩顶（isThickRoof && isNatural）：永不移除
                //    - ExpandedRoofing 玩家建厚屋顶（isThickRoof && !isNatural）：
                //        需要 ThickStoneRoofRemoval 研究完成才可移除
                //    - 普通屋顶 / ER 透明 / ER Solar：直接移除
                // ====================
                if (noRoofArea != null && noRoofArea[cell] && map.roofGrid.Roofed(cell))
                {
                    RoofDef roof = map.roofGrid.RoofAt(cell);
                    if (roof == null) continue;

                    if (roof.isThickRoof && roof.isNatural)
                        continue; // 天然岩顶，永不触碰

                    if (roof.isThickRoof && !roof.isNatural && !canRemoveThickRoof)
                        continue; // ExpandedRoofing 玩家建造的厚屋顶，研究未完成则跳过

                    map.roofGrid.SetRoof(cell, null);
                    removedCount++;
                }
            }

            Messages.Message("RealityWeaver_AutoRooferOk".Translate(builtCount, removedCount),
                this.parent, MessageTypeDefOf.PositiveEvent);
        }
    }
}
