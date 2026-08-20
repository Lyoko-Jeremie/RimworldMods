using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // 1. 定义 CompProperties
    // 这是连接 XML 和 C# 的桥梁
    public class CompProperties_InstantResearch : CompProperties
    {
        public CompProperties_InstantResearch()
        {
            // 绑定对应的 ThingComp 类
            this.compClass = typeof(CompInstantResearch);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompInstantResearchTex
    {
        public static readonly Texture2D IconResearch =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_Research", true) ?? BaseContent.WhiteTex;
    }

    // 2. 定义 ThingComp 核心逻辑
    public class CompInstantResearch : ThingComp
    {
        public CompProperties_InstantResearch Props => (CompProperties_InstantResearch)this.props;

        // 生成一个 UI 按钮（Gizmo），当选中带有该 Comp 的物品/建筑时显示
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            // 添加一个动作按钮：打开研究解锁管理界面
            yield return new Command_Action
            {
                defaultLabel = "OmniCrafter_UnlockAvailableResearch".Translate(),
                defaultDesc = "OmniCrafter_UnlockAvailableResearchDesc".Translate(),
                icon = CompInstantResearchTex.IconResearch,
                action = () =>
                {
                    Find.WindowStack.Add(new Dialog_InstantResearchUnlock());
                }
            };
        }
    }
}
