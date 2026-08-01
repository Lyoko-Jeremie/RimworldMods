using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public enum AutoOrderPasteMode
    {
        OverwriteExisting,
        KeepExisting
    }

    public struct AutoOrderPasteResult
    {
        public int added;
        public int overwritten;
        public int skipped;
    }

    /// <summary>
    /// 万能制造机订单剪贴板。剪贴板只在当前游戏进程中保留，不写入存档。
    /// </summary>
    public static class OmniCrafterOrderClipboard
    {
        private static readonly List<AutoOrder> Clipboard = new List<AutoOrder>();
        private static bool copied;

        public static bool HasCopiedOrders => copied;

        public static void CopyFrom(Building_OmniCrafter source)
        {
            Clipboard.Clear();

            HashSet<ThingDef> copiedDefs = new HashSet<ThingDef>();
            if (source?.autoOrders != null)
            {
                for (int i = 0; i < source.autoOrders.Count; i++)
                {
                    AutoOrder order = source.autoOrders[i];
                    if (order?.thingDef == null || !copiedDefs.Add(order.thingDef))
                        continue;

                    Clipboard.Add(order.Clone());
                }
            }

            copied = true;
            Messages.Message(
                "OmniCrafter_OrdersCopied".Translate(Clipboard.Count),
                source,
                MessageTypeDefOf.NeutralEvent,
                false);
        }

        public static AutoOrderPasteResult PasteInto(
            Building_OmniCrafter target,
            AutoOrderPasteMode mode)
        {
            AutoOrderPasteResult result = new AutoOrderPasteResult();
            if (!copied || target == null)
                return result;

            if (target.autoOrders == null)
                target.autoOrders = new List<AutoOrder>();

            Dictionary<ThingDef, AutoOrder> targetByDef =
                new Dictionary<ThingDef, AutoOrder>(target.autoOrders.Count);

            for (int i = 0; i < target.autoOrders.Count; i++)
            {
                AutoOrder order = target.autoOrders[i];
                if (order?.thingDef != null && !targetByDef.ContainsKey(order.thingDef))
                    targetByDef.Add(order.thingDef, order);
            }

            for (int i = 0; i < Clipboard.Count; i++)
            {
                AutoOrder source = Clipboard[i];
                if (source?.thingDef == null)
                    continue;

                if (targetByDef.TryGetValue(source.thingDef, out AutoOrder existing))
                {
                    if (mode == AutoOrderPasteMode.OverwriteExisting)
                    {
                        // 就地更新，保持订单位置和界面中 selectedAutoOrder 的引用有效。
                        existing.CopyFrom(source);
                        result.overwritten++;
                    }
                    else
                    {
                        result.skipped++;
                    }

                    continue;
                }

                AutoOrder added = source.Clone();
                target.autoOrders.Add(added);
                targetByDef.Add(added.thingDef, added);
                result.added++;
            }

            target.NotifyAutoOrdersChanged();
            Messages.Message(
                "OmniCrafter_OrdersPasted".Translate(result.added, result.overwritten, result.skipped),
                target,
                MessageTypeDefOf.NeutralEvent,
                false);
            return result;
        }
    }
}
