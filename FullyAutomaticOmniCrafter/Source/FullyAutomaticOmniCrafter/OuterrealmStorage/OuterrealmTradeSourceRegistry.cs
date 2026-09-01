using System.Runtime.CompilerServices;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 轨道贸易临时来源 → 全局权威条目的弱映射。
    /// 普通物品使用查询投影，唯一物品直接使用权威实例；两者都只在成交 SplitOff 时 Withdraw。
    /// 交易窗口关闭后临时投影无强引用，可由 GC 自动回收，不进入地图、ThingOwner 或存档。
    /// </summary>
    internal static class OuterrealmTradeSourceRegistry
    {
        private sealed class SourceState
        {
            public OuterrealmEntry Entry;
        }

        private static readonly ConditionalWeakTable<Thing, SourceState> Sources =
            new ConditionalWeakTable<Thing, SourceState>();

        public static void Register(Thing thing, OuterrealmEntry entry)
        {
            if (thing == null || entry == null)
            {
                return;
            }
            Sources.Remove(thing);
            Sources.Add(thing, new SourceState { Entry = entry });
        }

        public static bool TryGetEntry(Thing thing, out OuterrealmEntry entry)
        {
            entry = null;
            SourceState state;
            if (thing == null || !Sources.TryGetValue(thing, out state) || state.Entry == null)
            {
                return false;
            }
            entry = state.Entry;
            return true;
        }
    }
}
