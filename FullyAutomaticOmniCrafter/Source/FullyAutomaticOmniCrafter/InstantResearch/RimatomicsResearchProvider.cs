using System.Collections.Generic;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Rimatomics（边缘核能）研究 Provider。
    /// Rimatomics 研究无前置链概念，未完成的项目即为可解锁。
    /// </summary>
    public class RimatomicsResearchProvider : IResearchUnlockProvider
    {
        public bool IsActive => RimatomicsResearchCompat.IsModActive;

        public string GroupNameKey => "OmniCrafter_Research_GroupRimatomics";

        public List<ResearchUnlockEntry> CollectEntries(bool ignorePrerequisites)
        {
            List<ResearchUnlockEntry> result = new List<ResearchUnlockEntry>();
            if (!IsActive)
            {
                return result;
            }

            List<Def> projects = RimatomicsResearchCompat.CollectProjects();
            for (int i = 0; i < projects.Count; i++)
            {
                Def def = projects[i];
                ResearchUnlockEntry entry = new ResearchUnlockEntry
                {
                    Def = def,
                    Provider = this,
                    RawProject = def,
                    State = RimatomicsResearchCompat.IsFinished(def)
                        ? ResearchEntryState.Unlocked
                        : ResearchEntryState.Available
                };

                int price = RimatomicsResearchCompat.GetProjectPrice(def);
                if (price > 0)
                {
                    entry.CostText = price.ToString("N0");
                }

                result.Add(entry);
            }

            return result;
        }

        public bool TryComplete(ResearchUnlockEntry entry)
        {
            Def def = entry?.RawProject as Def;
            return def != null && RimatomicsResearchCompat.TryComplete(def);
        }
    }
}
