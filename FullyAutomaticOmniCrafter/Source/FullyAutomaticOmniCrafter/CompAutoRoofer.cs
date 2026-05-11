using System.Collections.Generic;
using System.Linq;
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
            // 绑定对应的执行类
            this.compClass = typeof(CompAutoRoofer);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompAutoRooferTex
    {
        public static readonly Texture2D IconBuildRoof =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_BuildRoof", true) ?? BaseContent.WhiteTex;
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
                defaultLabel = "OmniCrafter_AutoRoofer".Translate(),
                defaultDesc = "OmniCrafter_AutoRooferDesc".Translate(),
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

            // 获取原版的建造屋顶区和移除屋顶区
            Area buildRoofArea = map.areaManager.BuildRoof;
            Area noRoofArea = map.areaManager.NoRoof;

            int builtCount = 0;
            int removedCount = 0;

            // 遍历地图上的所有格子
            foreach (IntVec3 cell in map.AllCells)
            {
                // ====================
                // 1. 处理建造屋顶区
                // ====================
                if (buildRoofArea != null && buildRoofArea[cell])
                {
                    // 如果该格子还没有屋顶
                    if (!map.roofGrid.Roofed(cell))
                    {
                        // 强制生成原版的人造屋顶
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                        builtCount++;
                    }
                }

                // ====================
                // 2. 处理移除屋顶区
                // ====================
                if (noRoofArea != null && noRoofArea[cell])
                {
                    // 如果该格子有屋顶
                    if (map.roofGrid.Roofed(cell))
                    {
                        RoofDef roof = map.roofGrid.RoofAt(cell);
                        // 关键检查：不要移除厚岩顶（山脉），否则会导致逻辑错误或游戏报错
                        if (roof != null && !roof.isThickRoof)
                        {
                            map.roofGrid.SetRoof(cell, null);
                            removedCount++;
                        }
                    }
                }
            }

            // 在左上角发送一条消息反馈结果
            Messages.Message($"屋顶工程完毕：共建造 {builtCount} 个屋顶，拆除 {removedCount} 个屋顶。", this.parent,
                MessageTypeDefOf.PositiveEvent);
        }
    }
}
