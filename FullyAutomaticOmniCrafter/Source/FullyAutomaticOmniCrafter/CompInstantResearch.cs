using System.Collections.Generic;
using System.Linq;
using RimWorld;
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

            // 添加一个动作按钮
            yield return new Command_Action
            {
                defaultLabel = "OmniCrafter_UnlockAvailableResearch".Translate(),
                defaultDesc = "OmniCrafter_UnlockAvailableResearchDesc".Translate(),
                icon = CompInstantResearchTex.IconResearch,
                action = () =>
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "OmniCrafter_UnlockAvailableResearchDesc".Translate(),
                        UnlockAvailableResearch));
                }
            };
        }

        // 核心解锁逻辑
        private void UnlockAvailableResearch()
        {
            bool unlockedAny;
            int unlockCount = 0;

            // 【新增】：用于记录在本次点击中，已经解锁过的研究
            HashSet<ResearchProjectDef> processedProjects = new HashSet<ResearchProjectDef>();

            do
            {
                unlockedAny = false;

                // 【修改】：在筛选条件中增加 !processedProjects.Contains(res)
                List<ResearchProjectDef> availableProjects = DefDatabase<ResearchProjectDef>.AllDefs
                    .Where(res => res.CanStartNow
                                  && !res.IsFinished
                                  && !processedProjects.Contains(res)) // 防止循环研究卡死
                    .ToList();

                foreach (ResearchProjectDef proj in availableProjects)
                {
                    // 完成研究
                    Find.ResearchManager.FinishProject(proj, false, null);

                    // 【新增】：将其加入已处理名单
                    processedProjects.Add(proj);

                    unlockedAny = true;
                    unlockCount++;
                }
            } while (unlockedAny);

            // Rimatomics 使用独立于原版 ResearchManager 的研究系统。
            // 兼容层会动态处理全部已加载项目，因此也支持其他 Mod 添加的 Rimatomics 研究。
            int rimatomicsUnlockCount = RimatomicsResearchCompat.FinishAllResearch();

            // 发送提示
            if (unlockCount > 0 || rimatomicsUnlockCount > 0)
            {
                string message = RimatomicsResearchCompat.IsModActive
                    ? "OmniCrafter_OkUnlockAvailableResearchWithRimatomics"
                        .Translate(unlockCount, rimatomicsUnlockCount)
                    : "OmniCrafter_OkUnlockAvailableResearch".Translate(unlockCount);

                Messages.Message(message, MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                Messages.Message("OmniCrafter_NoUnlockAvailableResearch".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}
