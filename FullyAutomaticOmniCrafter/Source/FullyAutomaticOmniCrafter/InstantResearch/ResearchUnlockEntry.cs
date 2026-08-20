using System.Collections.Generic;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 单个研究的解锁条目状态。
    /// </summary>
    public enum ResearchEntryState
    {
        /// <summary>已完成 / 已达最大等级，不可再解锁。</summary>
        Unlocked,

        /// <summary>当前可解锁（前置满足，或被「忽略前置」开关强制放开）。</summary>
        Available,

        /// <summary>前置条件未满足。</summary>
        PrerequisiteMissing,

        /// <summary>处于隐藏状态（科技印刷缺失等），不可解锁。</summary>
        Hidden
    }

    /// <summary>
    /// 立即解锁界面中的单个研究条目，由各 Provider 收集生成。
    /// 条目仅存活于界面打开期间，不持久化。
    /// </summary>
    public class ResearchUnlockEntry
    {
        /// <summary>统一的 Def 引用，用于读取名称与描述。</summary>
        public Def Def;

        /// <summary>来源 Provider。</summary>
        public IResearchUnlockProvider Provider;

        /// <summary>当前状态。</summary>
        public ResearchEntryState State;

        /// <summary>预格式化的成本文本（可能为空）。</summary>
        public string CostText;

        /// <summary>前置项目名称列表。</summary>
        public List<string> PrerequisiteLabels = new List<string>();

        /// <summary>与 PrerequisiteLabels 一一对应的满足标记。</summary>
        public List<bool> PrerequisiteMet = new List<bool>();

        /// <summary>UI 勾选状态（仅存在于界面打开期间）。</summary>
        public bool IsSelected;

        /// <summary>Provider 内部的原始项目对象。</summary>
        public object RawProject;

        /// <summary>是否所有前置均已满足。</summary>
        public bool AllPrerequisitesMet
        {
            get
            {
                for (int i = 0; i < PrerequisiteMet.Count; i++)
                {
                    if (!PrerequisiteMet[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
