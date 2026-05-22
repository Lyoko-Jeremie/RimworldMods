using LudeonTK;
using Verse;
using RimWorld;

namespace FullyAutomaticOmniCrafter
{
    public static class DebugTools_PoisonFood
    {
        // 这会在开发者工具栏的 "General" 类别下生成一个 "Poison Food" 工具
        [DebugAction("General", "Poison Food (Test)", actionType = DebugActionType.ToolMap)]
        private static void PoisonFoodTool()
        {
            IntVec3 mouseCell = UI.MouseCell();
            if (!mouseCell.InBounds(Find.CurrentMap))
            {
                return;
            }

            // 获取鼠标当前点击的格子里的所有物品
            foreach (Thing t in Find.CurrentMap.thingGrid.ThingsAt(mouseCell))
            {
                // 尝试获取食物中毒组件
                CompFoodPoisonable comp = t.TryGetComp<CompFoodPoisonable>();
                if (comp != null)
                {
                    // 使用 SetPoisoned 方法设置中毒，这会自动将概率设为 100% 并设定原因
                    comp.SetPoisoned(FoodPoisonCause.IncompetentCook);

                    Messages.Message($"成功给 {t.Label} 下毒！", MessageTypeDefOf.TaskCompletion, false);
                }
            }
        }
    }
}