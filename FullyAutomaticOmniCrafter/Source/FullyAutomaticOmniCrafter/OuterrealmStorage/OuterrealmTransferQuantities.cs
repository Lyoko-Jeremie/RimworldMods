using System;
using System.Collections.Generic;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>运输清单的只读数量快照：同一条目的多个路线只提供一份库存。</summary>
    internal static class OuterrealmTransferQuantities
    {
        [ThreadStatic] private static HashSet<object> reusableSeen;

        private static HashSet<object> RentSeen()
        {
            HashSet<object> seen = reusableSeen ?? new HashSet<object>();
            reusableSeen = null;
            return seen;
        }

        private static void ReturnSeen(HashSet<object> seen)
        {
            seen.Clear();
            reusableSeen = seen;
        }

        internal static bool HasStorage(List<Thing> things)
        {
            if (things == null) return false;
            for (int i = 0; i < things.Count; i++)
            {
                OuterrealmSource source;
                if (OuterrealmVaultUtil.IsProjection(things[i]) || OuterrealmSourceResolver.TryResolve(things[i], out source)) return true;
            }
            return false;
        }

        private static long Quantity(Thing thing, HashSet<object> seen)
        {
            if (thing == null || thing.Destroyed) return 0;
            OuterrealmSource source;
            if (OuterrealmSourceResolver.TryResolve(thing, out source))
                return seen.Add(source.Entry) ? Math.Max(0, source.Entry.Count) : 0;
            // 交易预览也使用 TransferNoSplit，但其展示投影归交易注册表，不归建筑视图。
            OuterrealmEntry tradeEntry;
            if (OuterrealmTradeSourceRegistry.TryGetEntry(thing, out tradeEntry))
                return seen.Add(tradeEntry) ? Math.Max(0, tradeEntry.Count) : 0;
            // 退休投影不是普通物品，即使展示数量尚未归零也不能计入。
            if (OuterrealmVaultUtil.IsProjection(thing)) return 0;
            return seen.Add(thing) ? Math.Max(0, thing.stackCount) : 0;
        }

        internal static int Maximum(List<Thing> things)
        {
            HashSet<object> seen = RentSeen();
            try
            {
                long total = 0;
                for (int i = 0; i < things.Count; i++)
                {
                    long quantity = Quantity(things[i], seen);
                    // 在加法之前饱和，多个 long.MaxValue 条目也不会溢出。
                    if (quantity >= int.MaxValue - total) return int.MaxValue;
                    total += quantity;
                }
                return (int)total;
            }
            finally { ReturnSeen(seen); }
        }

        internal static List<ThingCount> Plan(List<Thing> things, int count)
        {
            var result = new List<ThingCount>();
            if (count <= 0) return result;
            HashSet<object> seen = RentSeen();
            try
            {
                for (int i = 0; i < things.Count && count > 0; i++)
                {
                    int take = (int)Math.Min(count, Quantity(things[i], seen));
                    if (take <= 0) continue;
                    result.Add(new ThingCount(things[i], take));
                    count -= take;
                }
                return result;
            }
            finally { ReturnSeen(seen); }
        }
    }
}
