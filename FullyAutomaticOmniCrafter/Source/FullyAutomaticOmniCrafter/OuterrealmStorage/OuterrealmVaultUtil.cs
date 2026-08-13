using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储通用工具。
    /// </summary>
    public static class OuterrealmVaultUtil
    {
        /// <summary>
        /// 安全的物品显示名：Corpse 在 Bugged 状态（Corpse.LabelNoCount 会 Log.Error
        /// "LabelNoCount on Corpse while Bugged" 并返回空串）——用 def 标签兜底；其余走原版 LabelCapNoCount。
        /// </summary>
        public static string SafeLabelCapNoCount(Thing t)
        {
            if (t == null)
            {
                return "";
            }
            if (t is Corpse corpse && corpse.Bugged)
            {
                return corpse.def.label.CapitalizeFirst();
            }
            return t.LabelCapNoCount;
        }
    }
}
