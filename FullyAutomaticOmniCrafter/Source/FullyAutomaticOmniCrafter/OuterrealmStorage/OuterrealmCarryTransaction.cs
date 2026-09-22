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
            // 跨图入口搬运守护（建筑级开关，默认关闭；见 OuterrealmVaultUtil.IsPortalBoundJob）。
            // vault 的查询投影是"伪 Spawned"并注册进 listerThings —— 这是有意的第三方取料兼容，
            // 但第三方若自己扫 listerThings 统计房间存量（如 RV Auto-Embark 的「溢出导出」），
            // 就会把库存当成地上的实物、加进 MapPortal 的待装载清单，再由搬运 Job 取走；
            // 一旦放行，库存真的会被搬出本地图（表现为物品从车辆出口掉到车外地上）。
            // 返回 0 = "没有搬到"，调用方（Patch_Pawn_CarryTracker_TryStartCarry）按原语义跳过后续。
            if (source.Vault != null && !source.Vault.AllowPortalTransfer
                && OuterrealmVaultUtil.IsPortalBoundJob(carry.pawn.CurJob))
            {
                return 0;
            }
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
