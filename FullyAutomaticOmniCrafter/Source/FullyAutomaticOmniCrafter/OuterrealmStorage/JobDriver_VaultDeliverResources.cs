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
    /// </summary>
    public class JobDriver_VaultDeliverResources : JobDriver
    {
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
            this.FailOnDestroyedOrNull<JobDriver_VaultDeliverResources>(TargetIndex.B);
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
            int want = job.count > 0 ? job.count : copy.stackCount;
            // TryStartCarry 对 vault 副本已由 Patch_Pawn_CarryTracker_TryStartCarry 接管：
            // Boost + SplitOff + 入 carry，单趟取量受全局剩余量与 carry 空间共同约束。
            int took = pawn.carryTracker.TryStartCarry(copy, want, true);
            if (took <= 0)
            {
                EndJobWith(JobCondition.Incompletable);
            }
        }
    }
}
