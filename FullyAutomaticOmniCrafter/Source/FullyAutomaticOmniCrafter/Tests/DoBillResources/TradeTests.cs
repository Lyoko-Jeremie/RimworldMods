using System;
using System.Collections.Generic;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using RimWorld;
using Verse;

internal static partial class Program
{
    private static void TradeTests()
    {
        Test("logical transfer plan preserves quantities beyond one display stack", () =>
        {
            Pawn pawn = Pawn();
            Thing query = Source(pawn, 10000, out var entry);
            Equal(75, new ThingCount(query, 1000).Count, "vanilla constructor clamps to display stack");
            foreach (int requested in new[] { 1, 75, 76, 1000, 10000 })
            {
                var plan = OuterrealmTransferQuantities.Plan(new List<Thing> { query, Alias(pawn, entry) }, requested);
                Equal(1, plan.Count, "entry deduplicated");
                Equal(requested, plan[0].Count, "logical count preserved");
                Equal(75, query.stackCount, "display remains one stack");
                Equal(10000L, entry.Count, "planning does not withdraw");
            }
        });
        Test("trade counts shared inventory once and preserves trader side", () =>
        {
            Pawn pawn = Pawn(); Thing a = Source(pawn, 10000, out var entry);
            var row = new Tradeable { thingsColony = new List<Thing> { a, Alias(pawn, entry), new Thing { stackCount = 50 } } };
            int count = 0;
            Check(!Patch_OuterrealmTradeQuantity.Prefix(row, Transactor.Colony, ref count), "quantity hook");
            Equal(10050, count, "shared total");
            Equal(75, a.stackCount, "projection unchanged");
            Check(Patch_OuterrealmTradeQuantity.Prefix(row, Transactor.Trader, ref count), "trader untouched");
        });
        foreach (bool fail in new[] { false, true })
        Test("trade stages partial withdrawals before settlement fail=" + fail, () =>
        {
            Pawn pawn = Pawn(); Thing query = Source(pawn, 1000, out var entry);
            Thing ground = new Thing { stackCount = 50, Spawned = true };
            var row = new Tradeable { thingsColony = new List<Thing> { query, ground }, ActionToDo = TradeAction.PlayerSells, CountToTransferToDestination = 1050 };
            var deal = new TradeDeal(); deal.AllTradeables.Add(row);
            Store.WithdrawalLimit = 200;
            if (fail) Store.WithdrawalsBeforeFailure = 2;
            bool traded = false, result = false;
            Patch_OuterrealmTradeExecution.Staging state = null;
            int delivered = 0;
            try
            {
                bool proceed = Patch_OuterrealmTradeExecution.Prefix(deal, ref traded, ref result, out state);
                Equal(!fail, proceed, "preflight result");
                if (proceed)
                {
                    // 模拟原版逐堆 TransferNoSplit，任何投影进入交付都会使测试失败。
                    int remaining = 1050;
                    foreach (Thing thing in row.thingsColony)
                    {
                        int take = Math.Min(remaining, thing.stackCount);
                        if (take == 0) break;
                        Check(!OuterrealmVaultUtil.IsProjection(thing), "projection delivered");
                        Thing actual = thing.SplitOff(take);
                        actual.Spawned = false;
                        actual.holdingOwner = new object();
                        delivered += take; remaining -= take;
                    }
                    Equal(0, remaining, "complete delivery");
                }
            }
            finally { Patch_OuterrealmTradeExecution.Finalizer(deal, state); }
            Equal(fail ? 1000L : 0L, entry.Count, "inventory conservation");
            Equal(fail ? 0 : 1050, delivered, "no settlement before complete staging");
        });
        Test("trade cancelled after staging returns all stock", () =>
        {
            Thing query = Source(Pawn(), 10000, out var entry);
            var deal = new TradeDeal(); deal.AllTradeables.Add(new Tradeable { thingsColony = new List<Thing> { query }, ActionToDo = TradeAction.PlayerSells, CountToTransferToDestination = 1000 });
            bool traded = false, result = false;
            Patch_OuterrealmTradeExecution.Staging state = null;
            try { Check(Patch_OuterrealmTradeExecution.Prefix(deal, ref traded, ref result, out state), "staging"); }
            finally { Patch_OuterrealmTradeExecution.Finalizer(deal, state); }
            Equal(10000L, entry.Count, "cancel rollback");
        });
        Test("trade rejects duplicate rows before withdrawing", () =>
        {
            Thing query = Source(Pawn(), 1000, out var entry);
            var deal = new TradeDeal();
            for (int i = 0; i < 2; i++) deal.AllTradeables.Add(new Tradeable { thingsColony = new List<Thing> { query }, ActionToDo = TradeAction.PlayerSells, CountToTransferToDestination = 600 });
            bool traded = false, result = false;
            Patch_OuterrealmTradeExecution.Staging state = null;
            try { Check(!Patch_OuterrealmTradeExecution.Prefix(deal, ref traded, ref result, out state), "oversell rejected"); }
            finally { Patch_OuterrealmTradeExecution.Finalizer(deal, state); }
            Equal(1000L, entry.Count, "no withdrawal");
        });
        Test("trade rejects changed stock and frozen source", () =>
        {
            Thing query = Source(Pawn(), 1000, out var entry);
            var deal = new TradeDeal();
            var row = new Tradeable { thingsColony = new List<Thing> { query }, ActionToDo = TradeAction.PlayerSells, CountToTransferToDestination = 1100 };
            deal.AllTradeables.Add(row);
            for (int i = 0; i < 2; i++)
            {
                if (i == 1) { row.CountToTransferToDestination = 500; OuterrealmSourceResolver.Sources[query].Vault.Frozen = true; }
                bool traded = false, result = false;
                Patch_OuterrealmTradeExecution.Staging state = null;
                try { Check(!Patch_OuterrealmTradeExecution.Prefix(deal, ref traded, ref result, out state), "invalid sale rejected"); }
                finally { Patch_OuterrealmTradeExecution.Finalizer(deal, state); }
                Equal(1000L, entry.Count, "unchanged stock");
            }
        });
        Test("trade exception returns only undelivered stock", () =>
        {
            Thing query = Source(Pawn(), 1000, out var entry);
            var deal = new TradeDeal(); deal.AllTradeables.Add(new Tradeable { thingsColony = new List<Thing> { query }, ActionToDo = TradeAction.PlayerSells, CountToTransferToDestination = 600 });
            Store.WithdrawalLimit = 200;
            bool traded = false, result = false;
            Patch_OuterrealmTradeExecution.Staging state = null;
            try
            {
                Check(Patch_OuterrealmTradeExecution.Prefix(deal, ref traded, ref result, out state), "staged");
                state.Items[0].holdingOwner = new object();
                throw new InvalidOperationException();
            }
            catch (InvalidOperationException) { }
            finally { Patch_OuterrealmTradeExecution.Finalizer(deal, state); }
            Equal(800L, entry.Count, "delivered stock must not be returned");
        });
    }
}
