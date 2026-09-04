using System;
using HarmonyLib;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>第三方直接 AddEquipment 的最终防线；原版装备 Job 已取得实物时自然放行。</summary>
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), "AddEquipment")]
    internal static class Patch_OuterrealmEquipmentCheckout
    {
        private sealed class State
        {
            public OuterrealmEntry Entry;
            public Thing Actual;
        }

        private static bool Prefix(Pawn ___pawn, ref ThingWithComps newEq, out State __state)
        {
            __state = null;
            OuterrealmSource source;
            if (!OuterrealmSourceResolver.TryResolve(newEq, out source)) return !OuterrealmVaultUtil.IsProjection(newEq);
            if (!OuterrealmBillJobUtility.CanUse(source, ___pawn)
                || OuterrealmBillJobUtility.Ledger?.TryBeginTransfer(source.Entry) != true) return false;
            __state = new State { Entry = source.Entry };
            __state.Actual = OuterrealmSourceResolver.Checkout(source, 1);
            ThingWithComps actual = __state.Actual as ThingWithComps;
            if (actual == null) return false;
            OuterrealmSourceResolver.ReplaceJobTarget(___pawn.CurJob, source, actual);
            newEq = actual;
            return true;
        }

        private static Exception Finalizer(State __state, Exception __exception)
        {
            if (__state == null) return __exception;
            try
            {
                Thing actual = __state.Actual;
                // 装备槽拒收、其他前缀跳过或抛异常：只回存仍无真实持有者的物品。
                if (actual != null && !actual.Destroyed && actual.holdingOwner == null && !actual.Spawned && actual.stackCount > 0)
                    GameComponent_OuterrealmStorage.Instance?.Deposit(actual);
            }
            finally { OuterrealmBillJobUtility.Ledger?.EndTransfer(__state.Entry); }
            return __exception;
        }
    }
}
