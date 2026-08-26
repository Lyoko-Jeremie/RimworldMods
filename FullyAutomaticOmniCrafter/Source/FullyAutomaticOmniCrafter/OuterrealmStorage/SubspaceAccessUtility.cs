using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储访问能力工具（§v3）：授权判定 + 制作选料的全局索引注入。
    /// 注入点：Patch_WorkGiver_DoBill_TryFindBestIngredientsHelper 的 Transpiler 在原版
    /// relevantThings.Clear() 之后调用 InjectGlobalEntries，把权威 Thing 作为只读候选加入
    /// relevantThings。正式预约成功后才从全局账本取出并 Spawn，候选扫描不再创建 Thing。
    /// </summary>
    public static class SubspaceAccessUtility
    {
        private static HediffDef cachedAccessDef;
        private static JobDef cachedDepositJobDef;
        private static readonly HashSet<Thing> PendingCheckouts = new HashSet<Thing>();
        private static readonly List<Thing> PendingReturnBuffer = new List<Thing>();

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

        /// <summary>该 Pawn 当前是否允许自动从全局库存取用。</summary>
        public static bool CanAutoTake(Pawn pawn)
        {
            Hediff_SubspaceAccess hediff = GetAccessHediff(pawn);
            return hediff != null && hediff.autoTake;
        }

        /// <summary>
        /// 制作选料注入（由 Transpiler 在原版 relevantThings.Clear() 后调用）：
        /// 授权 pawn 直接遍历全局条目，把条目的权威主堆作为只读候选加入 relevantThings。
        /// 不建立 Pawn 视图、不生成副本；Thing 的真实状态可供原版及第三方筛选器直接检查。
        /// 仅当 pawn 的"自动取用"开关开启时注入（关闭后制作走原版选料，不自动从身上取料）。
        /// </summary>
        public static void InjectGlobalEntries(Predicate<Thing> thingValidator, Pawn pawn, Thing billGiver, List<Thing> relevantThings)
        {
            Hediff_SubspaceAccess hediff = GetAccessHediff(pawn);
            if (hediff == null || !hediff.autoTake)
            {
                // 未授权，或该 pawn 关闭了"自动取用"开关：不注入随身副本（手动取用不受影响）。
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                return;
            }
            List<OuterrealmEntry> entries = gs.EntriesForReading;
            for (int i = 0; i < entries.Count; i++)
            {
                OuterrealmEntry entry = entries[i];
                Thing candidate = entry?.Proto;
                if (entry == null || entry.Count <= 0 || candidate == null || candidate.Destroyed
                    || candidate.Spawned || candidate.holdingOwner != null)
                {
                    continue;
                }
                // AllowMix 等原版选料分支直接读取 Position；权威物未 Spawn，不注册地图对象，
                // 这里只写一个值类型坐标，不会使其进入 tick、渲染、区域或资源列表。
                candidate.Position = pawn.Position;
                if ((thingValidator == null || thingValidator(candidate)) && pawn.CanReserve(candidate))
                {
                    relevantThings.Add(candidate);
                }
            }
        }

        /// <summary>新游戏/读档时清理仅运行期存在的预约物化跟踪。</summary>
        public static void ResetRuntimeState()
        {
            foreach (Thing thing in PendingCheckouts)
            {
                OuterrealmVaultUtil.UnmarkOuterrealmBorrowed(thing);
            }
            PendingCheckouts.Clear();
            PendingReturnBuffer.Clear();
        }

        /// <summary>登记随身访问在正式预约后生成、尚未被 Pawn 取得的真实物品。</summary>
        public static void MarkPendingCheckout(Thing thing)
        {
            if (thing != null && !thing.Destroyed && PendingCheckouts.Add(thing))
            {
                OuterrealmVaultUtil.MarkOuterrealmBorrowed(thing);
            }
        }

        /// <summary>取物成功后解除整堆跟踪；部分取走时原堆仍在地图上，留待 reservation 释放后回收余量。</summary>
        public static void NotifyCarryResult(Thing source, int carriedCount)
        {
            if (carriedCount <= 0 || source == null || !PendingCheckouts.Contains(source))
            {
                return;
            }
            if (source.Destroyed || source.holdingOwner != null || !source.Spawned)
            {
                PendingCheckouts.Remove(source);
                OuterrealmVaultUtil.UnmarkOuterrealmBorrowed(source);
            }
        }

        /// <summary>若目标已无 reservation，则把尚未被取走的真实物品退回全局库存。</summary>
        public static void TryReturnPendingCheckout(Thing thing)
        {
            if (thing == null || !PendingCheckouts.Contains(thing))
            {
                return;
            }
            if (thing.Destroyed || thing.holdingOwner != null)
            {
                PendingCheckouts.Remove(thing);
                OuterrealmVaultUtil.UnmarkOuterrealmBorrowed(thing);
                return;
            }
            Map map = thing.Map;
            if (thing.Spawned && map != null && map.reservationManager.IsReserved(thing))
            {
                return;
            }
            PendingCheckouts.Remove(thing);
            OuterrealmVaultUtil.UnmarkOuterrealmBorrowed(thing);
            if (!thing.Destroyed && thing.stackCount > 0)
            {
                GameComponent_OuterrealmStorage.Instance?.Deposit(thing);
            }
        }

        /// <summary>批量回收已取消/中断 Job 的物化余量；只扫描 pending 集合，与库存条目数无关。</summary>
        public static void ReturnUnreservedPending()
        {
            if (PendingCheckouts.Count == 0)
            {
                return;
            }
            PendingReturnBuffer.Clear();
            foreach (Thing thing in PendingCheckouts)
            {
                if (thing == null || thing.Destroyed || thing.holdingOwner != null
                    || !thing.Spawned || thing.Map == null || !thing.Map.reservationManager.IsReserved(thing))
                {
                    PendingReturnBuffer.Add(thing);
                }
            }
            for (int i = 0; i < PendingReturnBuffer.Count; i++)
            {
                TryReturnPendingCheckout(PendingReturnBuffer[i]);
            }
            PendingReturnBuffer.Clear();
        }
    }
}
