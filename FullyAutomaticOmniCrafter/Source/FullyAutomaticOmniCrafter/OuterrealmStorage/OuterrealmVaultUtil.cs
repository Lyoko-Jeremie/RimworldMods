using RimWorld;
using UnityEngine;
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
            if (t is MinifiedThing minified && minified.InnerThing == null)
            {
                // MinifiedThing 内物丢失（InnerThing == null，原版非法状态，如打包箱曾
                // 被 Destroy / 内物被转移）：原版 LabelNoCount => InnerThing.LabelNoCount
                // 会直接 NRE——用 def 标签兜底（与 Corpse.Bugged 同模式）。
                return t.def.label.CapitalizeFirst();
            }
            return t.LabelCapNoCount;
        }

        /// <summary>
        /// 安全的物品图标：Corpse 在 Bugged 状态（InnerPawn==null）时，原版 Widgets.GetIconFor
        /// 会执行 thing = corpse.InnerPawn 然后访问 thing.StyleDef 而 NRE——用 def 图标兜底；其余走原版 ThingIcon。
        /// </summary>
        public static void ThingIconSafe(Rect rect, Thing thing)
        {
            if (thing == null)
            {
                return;
            }
            if (thing is Corpse corpse && corpse.Bugged)
            {
                Widgets.ThingIcon(rect, thing.def);
                return;
            }
            Widgets.ThingIcon(rect, thing);
        }
    }
}
