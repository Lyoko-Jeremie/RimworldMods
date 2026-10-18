using System;
using System.Collections.Generic;
using System.Text;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储预留拒绝诊断（仅开发者模式）。
    ///
    /// 存在意义：多个 Pawn 同时取同一种物品时，条目级记账（Count − 预留）决定了只有"量够"的那几个
    /// 能成功预留，后到者必然被拒。被拒本身是正确语义，但原版 <c>Reserve</c>（errorOnFailed=true）
    /// 会打红字、随后 <c>StartJob</c> 打黄字，看起来像 bug。本类把"谁、要什么、要多少、账本怎么算、
    /// 被哪一条规则拒"打成一条静默日志，用来定位真正的口径不一致或残留孤儿副本。
    ///
    /// 刻意只被 <c>Patch_OuterrealmStorage</c> 使用：<c>OuterrealmBillJobUtility</c> 会被 Tests 工程
    /// 直接编译，不能在那边引入依赖本类的调用（失败原因只用字符串经 Ledger.Block 输出）。
    ///
    /// 约束：
    ///  · 只在 <see cref="Prefs.DevMode"/> 下输出，正常运行零输出；
    ///  · 使用 ThreadStatic 节流表（CanReserve 可能在非主线程被调用，Dictionary 不能共享）；
    ///  · 同一 (Pawn, Def, 原因) 在 300 tick 内最多一条，避免玩家日志被刷屏。
    /// </summary>
    internal static class OuterrealmReservationDiagnostics
    {
        // ── 拒绝原因（字符串常量，便于在日志里检索分组） ──
        internal const string QuantityShort = "quantity-short";
        internal const string Transferring = "transferring";
        internal const string NoSubspaceAccess = "no-subspace-access";
        internal const string ForeignReservation = "foreign-reservation";
        internal const string ExternalRejected = "external-rejected";
        internal const string NoMap = "no-map";
        internal const string OrphanCopy = "orphan-copy";

        private const int ThrottleTicks = 300;
        private const int MaxEntries = 512;

        private static readonly StringBuilder builder = new StringBuilder(320);

        [ThreadStatic] private static Dictionary<string, int> lastTickByKey;

        internal static bool Enabled => Prefs.DevMode;

        /// <summary>原版 CanReserve / Reserve 拒绝一条超维预留。</summary>
        internal static void Refusal(
            string reason, Pawn claimant, Job job, Thing target, OuterrealmEntry entry,
            long requested, long available, long reserved, long ledgerOwn, bool transferring)
        {
            if (!Enabled)
            {
                return;
            }
            string key = KeyOf(reason, claimant, target);
            if (!ShouldLog(key))
            {
                return;
            }
            builder.Length = 0;
            builder.Append("[OuterrealmStorage][diag] 预留被拒 reason=").Append(reason)
                .Append(" req=").Append(requested)
                .Append(" available=").Append(available)
                .Append(" count=").Append(entry != null ? entry.Count : -1L)
                .Append(" reserved=").Append(reserved)
                .Append(" ledgerOwn=").Append(ledgerOwn)
                .Append(" transferring=").Append(transferring ? 1 : 0)
                .Append(" pawn=").Append(DescribePawn(claimant))
                .Append(" job=").Append(DescribeJob(job))
                .Append(" target=").Append(DescribeThing(target))
                .Append(" entry=").Append(entry == null ? "null" : entry.Key.ToString());
            Log.Message(builder.ToString());
        }

        private static string KeyOf(string reason, Pawn pawn, Thing target)
        {
            builder.Length = 0;
            builder.Append(reason).Append('|')
                .Append(pawn != null ? pawn.thingIDNumber : 0).Append('|')
                .Append(target?.def != null ? target.def.shortHash : (ushort)0);
            return builder.ToString();
        }

        /// <summary>同一 key 在 300 tick 内只放行一次；表超过 512 条直接清空（诊断用途，丢弃历史无害）。</summary>
        private static bool ShouldLog(string key)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            Dictionary<string, int> table = lastTickByKey;
            if (table == null)
            {
                table = lastTickByKey = new Dictionary<string, int>();
            }
            int last;
            if (table.TryGetValue(key, out last) && tick >= last && tick - last < ThrottleTicks)
            {
                return false;
            }
            if (table.Count > MaxEntries)
            {
                table.Clear();
            }
            table[key] = tick;
            return true;
        }

        private static string DescribePawn(Pawn pawn)
        {
            return pawn != null ? pawn.LabelShortCap + "#" + pawn.thingIDNumber : "null";
        }

        private static string DescribeJob(Job job)
        {
            if (job == null)
            {
                return "null";
            }
            string def = job.def != null ? job.def.defName : "null";
            string giver = job.workGiverDef != null ? job.workGiverDef.defName : null;
            return giver != null ? def + "/" + giver : def;
        }

        private static string DescribeThing(Thing thing)
        {
            if (thing == null)
            {
                return "null";
            }
            string def = thing.def != null ? thing.def.defName : "null";
            string kind;
            if (thing is Building_OuterrealmVault)
            {
                kind = "vault";
            }
            else if (thing.holdingOwner is OuterrealmVaultViewThingOwner)
            {
                kind = "projection";
            }
            else if (OuterrealmIdentityRouting.IsAnchor(thing))
            {
                kind = "anchor";
            }
            else
            {
                kind = "plain";
            }
            return def + "[" + kind + "]x" + thing.stackCount;
        }
    }
}
