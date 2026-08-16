using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储访问能力工具（§v3）：授权判定 + 制作选料的随身副本注入。
    /// 注入点：Patch_WorkGiver_DoBill_TryFindBestIngredientsHelper 的 Transpiler 在原版
    /// relevantThings.Clear() 之后调用 InjectPawnCopies，把授权 pawn 随身视图的副本加入
    /// relevantThings，使原版 TryFindBestBillIngredientsInSet 照常匹配（副本 PositionHeld = pawn.Position，排最前）。
    /// </summary>
    public static class SubspaceAccessUtility
    {
        private static HediffDef cachedAccessDef;
        private static JobDef cachedDepositJobDef;

        /// <summary>右键"放入超维存储"的取货 job（§v3）。</summary>
        public static JobDef DepositFromGroundJobDef
        {
            get
            {
                if (cachedDepositJobDef == null)
                {
                    cachedDepositJobDef = DefDatabase<JobDef>.GetNamedSilentFail("FAOC_VaultDepositFromGround");
                }
                return cachedDepositJobDef;
            }
        }

        public static HediffDef AccessHediffDef
        {
            get
            {
                if (cachedAccessDef == null)
                {
                    cachedAccessDef = DefDatabase<HediffDef>.GetNamedSilentFail("FAOC_SubspaceAccess");
                }
                return cachedAccessDef;
            }
        }

        /// <summary>该 pawn 是否已授权（携带超维存储访问能力 Hediff）。</summary>
        public static bool IsAuthorized(Pawn pawn)
        {
            return GetAccessHediff(pawn) != null;
        }

        /// <summary>取访问能力 Hediff 实例（for 循环，避免 LINQ 分配）。</summary>
        public static Hediff_SubspaceAccess GetAccessHediff(Pawn pawn)
        {
            if (pawn?.health?.hediffSet == null)
            {
                return null;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i] is Hediff_SubspaceAccess access)
                {
                    return access;
                }
            }
            return null;
        }

        /// <summary>取随身视图（无则 null）。</summary>
        public static OuterrealmVaultViewThingOwner GetView(Pawn pawn)
        {
            Hediff_SubspaceAccess hediff = GetAccessHediff(pawn);
            return hediff != null ? hediff.EnsureView() : null;
        }

        /// <summary>
        /// 制作选料注入（由 Transpiler 在原版 relevantThings.Clear() 后调用）：
        /// 授权 pawn 把随身视图副本（提升为全局剩余量）加入 relevantThings，供原版选料匹配。
        /// </summary>
        public static void InjectPawnCopies(Pawn pawn, Thing billGiver, List<Thing> relevantThings)
        {
            Hediff_SubspaceAccess hediff = GetAccessHediff(pawn);
            if (hediff == null)
            {
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            OuterrealmVaultViewThingOwner view = hediff.EnsureView();
            view.RebuildView(); // 同步全局库最新状态（物化/更新副本，移除已空条目）
            List<Thing> copies = view.InnerListForReading;
            for (int i = 0; i < copies.Count; i++)
            {
                Thing copy = copies[i];
                OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(copy));
                if (e == null || e.Count <= 0)
                {
                    continue;
                }
                // 提升为全局剩余量（数量感知：单份需求 > stackLimit 也能匹配）
                copy.stackCount = (int)Mathf.Min(e.Count, int.MaxValue);
                // 保证 Position 有效（AllowMix 选料变体用 t.Position 而非 PositionHeld）
                copy.Position = pawn.Position;
                relevantThings.Add(copy);
            }
        }
    }
}
