using System;
using System.Collections.Generic;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>条目级数量账本。预留与选料共用，绝不从投影显示量反推预算。</summary>
    internal sealed class OuterrealmQuantityBudget<TKey>
    {
        private readonly Dictionary<TKey, long> amounts = new Dictionary<TKey, long>();
        public IEnumerable<KeyValuePair<TKey, long>> Entries => amounts;
        public int Count => amounts.Count;

        public long Get(TKey key)
        {
            long value;
            return amounts.TryGetValue(key, out value) ? value : 0;
        }

        public void Add(TKey key, long amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            amounts[key] = checked(Get(key) + amount);
        }

        public bool TrySpend(TKey key, long amount)
        {
            long current = Get(key);
            if (amount <= 0 || amount > current) return false;
            amounts[key] = current - amount;
            return true;
        }

        public static long Available(long inventory, long allReserved, long ownReserved = 0)
        {
            return Math.Max(0, inventory - Math.Max(0, allReserved - ownReserved));
        }
    }
}
