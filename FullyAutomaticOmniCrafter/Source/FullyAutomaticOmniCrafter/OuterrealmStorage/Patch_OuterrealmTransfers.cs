using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    [HarmonyPatch(typeof(TransferableUtility), "TransferNoSplit")]
    internal static class Patch_OuterrealmTransferPreview
    {
        internal static bool Prefix(List<Thing> things, int count, Action<Thing, int> transfer,
            bool removeIfTakingEntireThing, bool errorIfNotEnoughThings)
        {
            // 原版质量/容量/腐败预估使用 false,false；交易及尸体交付保留原版所有权协议。
            if (removeIfTakingEntireThing || errorIfNotEnoughThings || !OuterrealmTransferQuantities.HasStorage(things)) return true;
            List<ThingCount> plan = OuterrealmTransferQuantities.Plan(things, count);
            for (int i = 0; i < plan.Count; i++) transfer(plan[i].Thing, plan[i].Count);
            return false;
        }
    }

    [HarmonyPatch(typeof(TransferableUtility), "Transfer")]
    internal static class Patch_OuterrealmImmediateTransfer
    {
        internal static bool Prefix(List<Thing> things, int count, Action<Thing, IThingHolder> transferred)
        {
            if (!OuterrealmTransferQuantities.HasStorage(things)) return true;
            List<ThingCount> plan = OuterrealmTransferQuantities.Plan(things, count);
            int remaining = count;
            for (int i = 0; i < plan.Count; i++)
            {
                Thing query = plan[i].Thing;
                IThingHolder holder = query.ParentHolder;
                OuterrealmSource source;
                bool storage = OuterrealmSourceResolver.TryResolve(query, out source);
                if (!storage && OuterrealmVaultUtil.IsProjection(query)) continue;
                OuterrealmBillResourceLedger ledger = OuterrealmBillJobUtility.Ledger;
                if (storage && (ledger == null || !ledger.TryBeginTransfer(source.Entry))) continue;
                Thing actual = null;
                try
                {
                    actual = storage ? OuterrealmSourceResolver.Checkout(source, plan[i].Count) : query.SplitOff(plan[i].Count);
                    if (actual == null || actual.Destroyed) continue;
                    int obtained = actual.stackCount;
                    if (actual == query) things.Remove(query);
                    transferred(actual, holder);
                    // 超维来源拒收或部分接收时，只计算真实交付量；finally 回收未持有余量。
                    int leftover = storage && !actual.Destroyed && actual.holdingOwner == null && !actual.Spawned ? actual.stackCount : 0;
                    remaining -= Math.Max(0, obtained - leftover);
                }
                finally
                {
                    if (storage)
                    {
                        try
                        {
                            if (actual != null && !actual.Destroyed && actual.holdingOwner == null && !actual.Spawned && actual.stackCount > 0)
                                GameComponent_OuterrealmStorage.Instance.Deposit(actual);
                        }
                        finally { ledger.EndTransfer(source.Entry); }
                    }
                }
            }
            if (remaining > 0) Log.Error("[OuterrealmStorage] Immediate transfer incomplete: remaining=" + remaining);
            return false;
        }
    }
}
