using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    [HarmonyPatch(typeof(Tradeable), "CountHeldBy")]
    internal static class Patch_OuterrealmTradeQuantity
    {
        internal static bool Prefix(Tradeable __instance, Transactor trans, ref int __result)
        {
            if (trans != Transactor.Colony || !OuterrealmTransferQuantities.HasStorage(__instance.thingsColony))
                return true;
            __result = OuterrealmTransferQuantities.Maximum(__instance.thingsColony);
            return false;
        }
    }

    /// <summary>确认交易时先备齐超维实物，原版结算仍只接收真实 Thing。</summary>
    [HarmonyPatch(typeof(TradeDeal), "TryExecute")]
    internal static class Patch_OuterrealmTradeExecution
    {
        [ThreadStatic] private static bool preparing;

        internal sealed class Staging
        {
            internal readonly List<Thing> Items = new List<Thing>();
        }

        internal static bool Prefix(TradeDeal __instance, ref bool actuallyTraded, ref bool __result,
            out Staging __state)
        {
            __state = null;
            if (preparing)
            {
                actuallyTraded = __result = false;
                return false;
            }
            List<Tradeable> rows = __instance.AllTradeables;
            bool hasStorage = false;
            for (int i = 0; i < rows.Count; i++)
                if (OuterrealmTransferQuantities.HasStorage(rows[i].thingsColony)) { hasStorage = true; break; }
            if (!hasStorage) return true;

            preparing = true;
            __state = new Staging();
            // 白银也属于出售行，必须在建立计划前同步本次应付数量。
            __instance.UpdateCurrencyCount();
            var plans = new List<KeyValuePair<Tradeable, List<ThingCount>>>();
            var totals = new Dictionary<OuterrealmEntry, long>();
            for (int i = 0; i < rows.Count; i++)
            {
                Tradeable row = rows[i];
                if (row.ActionToDo != TradeAction.PlayerSells) continue;
                int requested = row.CountToTransferToDestination;
                List<ThingCount> plan = OuterrealmTransferQuantities.Plan(row.thingsColony, requested);
                int planned = 0;
                for (int j = 0; j < plan.Count; j++)
                {
                    ThingCount part = plan[j];
                    planned += part.Count;
                    OuterrealmEntry entry;
                    if (!TryEntry(part.Thing, out entry)) continue;
                    OuterrealmSource source;
                    if (OuterrealmSourceResolver.TryResolve(part.Thing, out source)
                        && source.Vault != null
                        && (!source.Vault.Spawned || source.Vault.NoWithdraw || !source.Vault.CanShow(part.Thing)))
                        return Reject(ref actuallyTraded, ref __result);
                    long used;
                    totals.TryGetValue(entry, out used);
                    // 跨交易行也共享同一条目预算，避免第三方重复分行造成超卖。
                    if (part.Count > entry.Count - used) return Reject(ref actuallyTraded, ref __result);
                    totals[entry] = used + part.Count;
                }
                if (planned != requested) return Reject(ref actuallyTraded, ref __result);
                plans.Add(new KeyValuePair<Tradeable, List<ThingCount>>(row, plan));
            }

            for (int i = 0; i < plans.Count; i++)
            {
                List<ThingCount> plan = plans[i].Value;
                var stagedForRow = new List<Thing>();
                var ordinary = new List<Thing>();
                var queries = new List<Thing>();
                List<Thing> colony = plans[i].Key.thingsColony;
                for (int j = 0; j < colony.Count; j++)
                {
                    OuterrealmEntry ignored;
                    if (TryEntry(colony[j], out ignored) || OuterrealmVaultUtil.IsProjection(colony[j]))
                        queries.Add(colony[j]);
                    else ordinary.Add(colony[j]);
                }
                for (int j = 0; j < plan.Count; j++)
                {
                    OuterrealmEntry entry;
                    if (!TryEntry(plan[j].Thing, out entry))
                    {
                        if (OuterrealmVaultUtil.IsProjection(plan[j].Thing))
                            return Reject(ref actuallyTraded, ref __result);
                        continue;
                    }
                    int remaining = plan[j].Count;
                    // Withdraw 可能因 Comp 合并拒绝而只返回部分，必须备齐后才进入任何结算。
                    while (remaining > 0)
                    {
                        Thing actual = GameComponent_OuterrealmStorage.Instance.Withdraw(entry, remaining);
                        if (actual == null || actual.Destroyed || actual.stackCount <= 0)
                            return Reject(ref actuallyTraded, ref __result);
                        __state.Items.Add(actual);
                        stagedForRow.Add(actual);
                        remaining -= actual.stackCount;
                    }
                }
                // 实物排在查询投影前；原版 TransferNoSplit 因而不会对单堆投影进行交付。
                // 剩余投影保留为只读余额，原版支付能力复核仍能看到完整总量。
                colony.Clear();
                colony.AddRange(stagedForRow);
                colony.AddRange(ordinary);
                for (int j = 0; j < queries.Count; j++)
                {
                    OuterrealmEntry ignored;
                    // 唯一权威实例取出后已成为实物，不能作为旧查询再加入一次。
                    if (TryEntry(queries[j], out ignored)) colony.Add(queries[j]);
                }
            }
            return true;
        }

        private static bool TryEntry(Thing thing, out OuterrealmEntry entry)
        {
            if (OuterrealmTradeSourceRegistry.TryGetEntry(thing, out entry)) return true;
            OuterrealmSource source;
            if (!OuterrealmSourceResolver.TryResolve(thing, out source)) return false;
            entry = source.Entry;
            return true;
        }

        private static bool Reject(ref bool actuallyTraded, ref bool result)
        {
            actuallyTraded = result = false;
            Messages.Message("ColonyHasNoMore".Translate(), MessageTypeDefOf.RejectInput, false);
            return false;
        }

        internal static void Finalizer(TradeDeal __instance, Staging __state)
        {
            if (__state == null) return;
            try
            {
                var restored = new HashSet<OuterrealmEntry>();
                GameComponent_OuterrealmStorage storage = GameComponent_OuterrealmStorage.Instance;
                for (int i = 0; i < __state.Items.Count; i++)
                {
                    Thing actual = __state.Items[i];
                    // 原版拒绝成交、部分消费或异常时，仅回存仍未交付的实物。
                    if (!actual.Destroyed && !actual.Spawned && actual.holdingOwner == null && actual.stackCount > 0)
                    {
                        OuterrealmEntry entry = storage.Deposit(actual);
                        if (entry != null) restored.Add(entry);
                    }
                }
                // 交易窗口暂停了 Tick，回存后的投影必须立即恢复，不能等待后台同步队列。
                foreach (OuterrealmEntry entry in restored)
                    for (int i = 0; i < storage.VaultsForReading.Count; i++)
                        storage.VaultsForReading[i]?.view?.EnsureCopyFor(entry);
                // 空条目的旧投影可能已退休，重新枚举，不能把旧列表恢复到交易窗口。
                __instance.Reset();
            }
            finally { preparing = false; }
        }
    }
}
