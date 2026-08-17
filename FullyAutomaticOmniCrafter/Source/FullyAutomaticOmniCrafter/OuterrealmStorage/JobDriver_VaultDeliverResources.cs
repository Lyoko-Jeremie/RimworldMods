using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 施工配送（从超维存储取料送往蓝图/Frame）。
    /// TargetA = vault 建筑（行走目标，副本未 Spawned 不可作为行走目标）；
    /// TargetB = vault 视图副本（取料目标，未 Spawned）；
    /// TargetC = 蓝图/Frame（送达目标）。
    /// 取料复用 Patch_Pawn_CarryTracker_TryStartCarry 的 vault 分支（Boost + SplitOff + 入 carry），
    /// 送达复用原版 Toils_Construct.MakeSolidThingFromBlueprintIfNecessary + Toils_Haul.DepositHauledThingInContainer。
    ///
    /// 防御设计（针对 "tried to place hauled thing in container but is not hauling anything." 一类竞态，
    /// 并考虑其他 Mod 可能 patch 同一流程）：
    /// 1. job 级 FailOn：取料成功后任何时刻 carry 为空 / 已销毁 / def 被调包 → 立即 Incompletable
    ///    （原版 JobDriver_HaulToContainer 对 carry 状态有 FailOn 防御，此前我们缺失）；
    /// 2. 取料后即时校验 TryStartCarry 返回值与 carry 实际状态一致（防其他 Mod 的 TryStartCarry patch
    ///    返回成功却未真正放入 / 放入后又被移走）；
    /// 3. 送达前 Validate toil：pawn 状态（Dead/Downed/carry 已掉落）、carry 状态、目标有效性、
    ///    目标可交互投放性（Blueprint→Frame 转换后才有 resourceContainer；被抢先建成普通建筑时
    ///    原版 Deposit 会静默滞留物品，这里显式失败）全检；
    /// 4. 送达目标在途中被销毁/禁止 → 失败（对齐原版 JobDriver_HaulToContainer 的防御）。
    /// 所有异常路径均静默 Incompletable 结束：不触发原版 Log.Error 刷屏，不 NRE，不产生错误投放。
    /// </summary>
    public class JobDriver_VaultDeliverResources : JobDriver
    {
        /// <summary>取料时记录的期望 def，用于识别 carry 物品被其他 Mod 中途调包。</summary>
        private ThingDef expectedDef;

        /// <summary>是否已从 vault 取料成功（取料前 carry 为空是合法的，取料后必须非空）。</summary>
        private bool tookFromVault;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 预留取料副本（数量 job.count；#8 预留检查按全局可用量 G−R 校验）
            if (!pawn.Reserve(TargetB, job, 1, job.count, errorOnFailed: errorOnFailed))
            {
                return false;
            }
            // 预留送达目标（蓝图/Frame），与 JobDriver_HaulToContainer 对 Container 的预留一致
            return pawn.Reserve(TargetC, job, 1, 1, errorOnFailed: errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // 取料副本失效 → 失败
            this.FailOnDestroyedOrNull<JobDriver_VaultDeliverResources>(TargetIndex.B);
            // 送达目标在途中被销毁/禁止 → 失败（GotoBuild 自带 FailOnDespawnedOrNull 之外的
            // job 级兜底，覆盖 GotoBuild 完成到 Deposit 之间的竞态窗口）
            this.FailOnDestroyedOrNull<JobDriver_VaultDeliverResources>(TargetIndex.C);
            this.FailOnForbidden<JobDriver_VaultDeliverResources>(TargetIndex.C);
            // 取料成功后，carry 必须在整个配送途中保持有效（防御其他 Mod 清空/调包 carry）
            this.FailOn(() => tookFromVault && CarryIsInvalid());
            // 走到 vault 建筑交互格
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A);
            // 取料：从视图副本 SplitOff 入 carry
            yield return new Toil
            {
                initAction = TakeFromVault
            };
            // 送到蓝图/Frame
            yield return Toils_Goto.GotoBuild(TargetIndex.C);
            yield return Toils_Construct.MakeSolidThingFromBlueprintIfNecessary(TargetIndex.C, TargetIndex.C);
            // 送达前最后一道防御：任何异常静默 Incompletable，
            // 避免原版 DepositHauledThingInContainer 的 Log.Error / 目标为 null 的 NRE / 静默滞留
            yield return new Toil
            {
                initAction = ValidateBeforeDeposit
            };
            yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.C, TargetIndex.C);
        }

        private void TakeFromVault()
        {
            Thing copy = job.GetTarget(TargetIndex.B).Thing;
            if (copy == null || copy.Destroyed || copy.stackCount <= 0)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            expectedDef = copy.def;
            int want = job.count > 0 ? job.count : copy.stackCount;
            // TryStartCarry 对 vault 副本已由 Patch_Pawn_CarryTracker_TryStartCarry 接管：
            // Boost + SplitOff + 入 carry，单趟取量受全局剩余量与 carry 空间共同约束。
            int took = pawn.carryTracker.TryStartCarry(copy, want, true);
            if (took <= 0 || CarryIsInvalid())
            {
                // took>0 但 carry 无效（其他 Mod 的 TryStartCarry patch 返回成功却未真正放入，
                // 或放入后又被同 tick 移除）：不可继续，静默结束，避免走到 Deposit 报错或错误投放。
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            tookFromVault = true;
        }

        /// <summary>carry 是否处于"不可配送"状态：为空 / 已销毁 / 堆叠数异常 / def 与取料时不一致。</summary>
        private bool CarryIsInvalid()
        {
            Pawn_CarryTracker tracker = pawn?.carryTracker;
            Thing carried = tracker?.CarriedThing;
            if (carried == null || carried.Destroyed || carried.stackCount <= 0)
            {
                return true;
            }
            return expectedDef != null && carried.def != expectedDef;
        }

        private void ValidateBeforeDeposit()
        {
            // 1. pawn 状态：死亡/倒地（carry 物品已随倒地掉落）→ 不可投放
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            // 2. carry 必须持有有效货物（核心：杜绝 "is not hauling anything"）
            if (CarryIsInvalid())
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            // 3. 送达目标必须仍存在（GotoBuild 到 Deposit 之间的竞态窗口兜底）
            Thing target = job.GetTarget(TargetIndex.C).Thing;
            if (target == null || target.Destroyed)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            // 4. 目标必须可交互投放：Blueprint 完成 → Frame 转换后才有 resourceContainer；
            //    若目标被抢先建成无容器的普通建筑/不可投放对象，原版 Deposit 会静默滞留物品
            //    （job 结束但物品留在 pawn 手里且 vault 计数已扣），此处显式失败结束。
            if (target.TryGetInnerInteractableThingOwner() == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
        }
    }
}
