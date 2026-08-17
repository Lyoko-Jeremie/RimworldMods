using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 授权 pawn 右键"放入超维存储"的取货 job（§v3）：走到地面物品 → 拿起 → 吸收进全局库。
    /// TargetA = 地面物品。
    /// </summary>
    public class JobDriver_VaultDepositFromGround : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(TargetA, job, 1, ReservationManager.StackCount_All, errorOnFailed: errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull<JobDriver_VaultDepositFromGround>(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A)
                .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return new Toil
            {
                initAction = DepositCarried,
                defaultCompleteMode = ToilCompleteMode.Instant,
            };
        }

        private void DepositCarried()
        {
            Thing carried = pawn.carryTracker.CarriedThing;
            if (carried == null || carried.Destroyed)
            {
                // 防御：StartCarryThing 之后 carry 仍为空属异常状态（竞态/其他 Mod 干扰），
                // 不应以 Succeeded 结束，改为 Incompletable 让 job 正确失败。
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            // 先从 carry 取出（holdingOwner 置 null），否则 gs.Deposit 内部 Destroy 只通知
            // Notify_ContainedItemDestroyed、不把 item 从 innerContainer 移除，残留已销毁 item。
            pawn.carryTracker.innerContainer.Remove(carried);
            gs.Deposit(carried); // 吸收进全局库（未 Spawned，直接并入条目）
            EndJobWith(JobCondition.Succeeded);
        }
    }
}
