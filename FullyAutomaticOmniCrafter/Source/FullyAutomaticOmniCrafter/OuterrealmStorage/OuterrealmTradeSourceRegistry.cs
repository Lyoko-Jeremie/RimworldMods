using System.Runtime.CompilerServices;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 轨道贸易临时来源 → 全局权威条目的弱映射。
    /// 普通物品使用查询投影，唯一物品直接使用权威实例；两者都只在成交 SplitOff 时 Withdraw。
    /// 临时投影在交易窗口关闭后可由 GC 回收；唯一物品的权威实例仍由全局条目强引用，
    /// 因此其映射必须在成交消费或任何正式 Checkout 前显式注销。
    /// </summary>
    internal static class OuterrealmTradeSourceRegistry
    {
        private sealed class SourceState
        {
            public OuterrealmEntry Entry;
            public bool UsesCanonical;
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
            Sources.Add(thing, new SourceState
            {
                Entry = entry,
                UsesCanonical = ReferenceEquals(thing, entry.Proto)
            });
        }

        public static bool TryGetEntry(Thing thing, out OuterrealmEntry entry)
        {
            entry = null;
            SourceState state;
            if (thing == null || !Sources.TryGetValue(thing, out state))
            {
                return false;
            }
            OuterrealmEntry candidate = state.Entry;
            if (candidate == null || candidate.Count <= 0 || candidate.Proto == null
                || thing.Destroyed || thing.def != candidate.Proto.def
                || (state.UsesCanonical && !ReferenceEquals(thing, candidate.Proto)))
            {
                // 唯一物品 Checkout 后权威实例会脱离原条目；此时旧交易映射绝不能继续
                // 把普通携带/穿戴中的 SplitOff 识别成第二次交易扣库。
                Sources.Remove(thing);
                return false;
            }
            entry = candidate;
            return true;
        }

        /// <summary>成交 SplitOff 只能消费一次来源身份，避免同一临时来源被重复结算。</summary>
        public static bool TryTakeEntry(Thing thing, out OuterrealmEntry entry)
        {
            if (!TryGetEntry(thing, out entry))
            {
                return false;
            }
            Sources.Remove(thing);
            return true;
        }

        /// <summary>权威实例离开全局账本前立即撤销可能残留的交易身份。</summary>
        public static void Unregister(Thing thing)
        {
            if (thing != null)
            {
                Sources.Remove(thing);
            }
        }
    }
}
