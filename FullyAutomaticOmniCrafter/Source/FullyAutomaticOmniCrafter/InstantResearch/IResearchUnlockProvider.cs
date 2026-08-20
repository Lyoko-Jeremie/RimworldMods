using System.Collections.Generic;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 研究解锁来源 Provider。
    /// 每个支持的研究系统（原版 / 渡鸦 / Rimatomics 等）实现一个 Provider，
    /// 负责枚举研究条目、判定可解锁状态并完成单个研究。
    /// </summary>
    public interface IResearchUnlockProvider
    {
        /// <summary>当前是否可用（对应 Mod 是否已加载且可初始化）。</summary>
        bool IsActive { get; }

        /// <summary>分组标题的翻译 Key。</summary>
        string GroupNameKey { get; }

        /// <summary>
        /// 收集全部研究条目并计算状态。
        /// </summary>
        /// <param name="ignorePrerequisites">为 true 时忽略前置条件，未满足前置的条目也标记为可解锁。</param>
        List<ResearchUnlockEntry> CollectEntries(bool ignorePrerequisites);

        /// <summary>完成单个研究条目，返回是否实际完成。</summary>
        bool TryComplete(ResearchUnlockEntry entry);
    }
}
