using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 原版研究 Provider。
    /// 覆盖全部 ResearchProjectDef，包括其他 Mod 以原版类型添加的研究
    /// （例如渡鸦 Mod 的 26 个原版类型研究）。
    /// 可解锁判断放宽了「研究台 / 机械师」要求：万能制造机直接完成研究，无需这些前置设施。
    /// </summary>
    public class VanillaResearchProvider : IResearchUnlockProvider
    {
        public bool IsActive => true;

        public string GroupNameKey => "OmniCrafter_Research_GroupVanilla";

        public List<ResearchUnlockEntry> CollectEntries(bool ignorePrerequisites)
        {
            List<ResearchUnlockEntry> result = new List<ResearchUnlockEntry>();
            List<ResearchProjectDef> allDefs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;

            for (int i = 0; i < allDefs.Count; i++)
            {
                ResearchProjectDef proj = allDefs[i];
                ResearchUnlockEntry entry = new ResearchUnlockEntry
                {
                    Def = proj,
                    Provider = this,
                    RawProject = proj
                };

                if (proj.IsFinished)
                {
                    entry.State = ResearchEntryState.Unlocked;
                }
                else
                {
                    // 与 CanStartNow 相比，放宽了 requiredResearchBuilding 与 requiresMechanitor 两项：
                    // 万能制造机直接完成研究，不依赖玩家是否拥有研究台或机械师。
                    bool prereqOk = proj.PrerequisitesCompleted;
                    bool techprintOk = proj.TechprintRequirementMet;
                    bool hidden = proj.IsHidden;
                    bool analyzedOk = proj.AnalyzedThingsRequirementsMet;
                    bool inspectionOk = proj.InspectionRequirementsMet;

                    bool canUnlock = prereqOk && techprintOk && !hidden && analyzedOk && inspectionOk;
                    if (canUnlock || ignorePrerequisites)
                    {
                        entry.State = ResearchEntryState.Available;
                    }
                    else if (hidden || !techprintOk)
                    {
                        entry.State = ResearchEntryState.Hidden;
                    }
                    else
                    {
                        entry.State = ResearchEntryState.PrerequisiteMissing;
                    }
                }

                // 前置信息（含隐藏前置，供 UI 标红展示）
                if (proj.prerequisites != null)
                {
                    for (int j = 0; j < proj.prerequisites.Count; j++)
                    {
                        ResearchProjectDef prereq = proj.prerequisites[j];
                        entry.PrerequisiteLabels.Add(prereq.LabelCap.ToString());
                        entry.PrerequisiteMet.Add(prereq.IsFinished);
                    }
                }
                if (proj.hiddenPrerequisites != null)
                {
                    for (int j = 0; j < proj.hiddenPrerequisites.Count; j++)
                    {
                        ResearchProjectDef prereq = proj.hiddenPrerequisites[j];
                        entry.PrerequisiteLabels.Add(prereq.LabelCap.ToString());
                        entry.PrerequisiteMet.Add(prereq.IsFinished);
                    }
                }

                // 成本（研究点数）
                entry.CostText = proj.Cost.ToString("N0");

                result.Add(entry);
            }

            return result;
        }

        public bool TryComplete(ResearchUnlockEntry entry)
        {
            ResearchProjectDef proj = entry.RawProject as ResearchProjectDef;
            if (proj == null || proj.IsFinished)
            {
                return false;
            }

            Find.ResearchManager.FinishProject(proj, false, null);
            return true;
        }
    }
}
