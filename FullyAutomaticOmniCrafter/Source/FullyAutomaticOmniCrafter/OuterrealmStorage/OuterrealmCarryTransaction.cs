using System;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>普通搬运的所有权事务；投影和唯一锚点都只交付 Checkout 返回的权威实物。</summary>
    internal static class OuterrealmCarryTransaction
    {
        private static long Available(Pawn pawn, OuterrealmSource source)
        {
            long own = 0;
            Job job = pawn.CurJob;
            // 通用旧任务没有逐槽预算；只在真正取用时读本地图自己的桥接预留。
            var reservations = pawn.Map.reservationManager.ReservationsReadOnly;
            for (int i = 0; i < reservations.Count; i++)
            {
                var reservation = reservations[i];
                OuterrealmSource reservedSource;
                if (reservation.Claimant != pawn || reservation.Job != job
                    || !OuterrealmSourceResolver.TryResolve(reservation.Target.Thing, out reservedSource)
                    || reservedSource.Entry != source.Entry) continue;
                own += reservation.StackCount < 0 ? Math.Min(source.Entry.Count, source.QueryThing.def.stackLimit) : reservation.StackCount;
            }
            return OuterrealmQuantityBudget<OuterrealmEntry>.Available(source.Entry.Count,
                GameComponent_OuterrealmStorage.Instance.ReservedCountOf(source.Entry), own);
        }

        internal static int Transfer(Pawn_CarryTracker carry, OuterrealmSource source, int count)
        {
            if (carry.pawn.Dead || carry.pawn.Downed || !OuterrealmBillJobUtility.CanUse(source, carry.pawn)
                || (carry.CarriedThing != null && !carry.CarriedThing.CanStackWith(source.QueryThing))) return 0;
            count = (int)Math.Min(Math.Min(count, carry.AvailableStackSpace(source.QueryThing.def)), Available(carry.pawn, source));
            OuterrealmBillResourceLedger ledger = OuterrealmBillJobUtility.Ledger;
            if (count <= 0 || ledger == null || !ledger.TryBeginTransfer(source.Entry)) return 0;
            int before = carry.CarriedThing?.stackCount ?? 0;
            Thing actual = null;
            try
            {
                actual = OuterrealmSourceResolver.Checkout(source, count);
                if (actual == null || actual.Destroyed || actual.stackCount <= 0) return 0;
                // bool 重载不会再次 SplitOff；不能把返回 false 当作完全没有交付。
                carry.innerContainer.TryAdd(actual, true);
                return Math.Max(0, Math.Min(count, (carry.CarriedThing?.stackCount ?? 0) - before));
            }
            finally
            {
                try
                {
                    // 部分合并后只回收余量；交付后的异常也不能重复入库。
                    if (actual != null && !actual.Destroyed && actual.holdingOwner == null
                        && !actual.Spawned && actual.stackCount > 0)
                        GameComponent_OuterrealmStorage.Instance.Deposit(actual);
                }
                finally { ledger.EndTransfer(source.Entry); }
            }
        }
    }
}
