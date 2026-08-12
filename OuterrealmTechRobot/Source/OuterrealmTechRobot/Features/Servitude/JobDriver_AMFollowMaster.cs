using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 跟随主人的工作驱动：每 250 tick 评估一次，距主人超过保持距离（4 格）则寻路靠近，否则停步。
    /// 主人死亡/换图/消失即任务失败。
    /// </summary>
    public class JobDriver_AMFollowMaster : JobDriver
    {
        private const TargetIndex MasterInd = TargetIndex.A;

        /// <summary>跟随保持距离（格）：距离内停步。</summary>
        private const float FollowRadius = 4f;

        private Pawn Master => (Pawn)job.GetTarget(MasterInd).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 跟随不占用预留（主人可能被其他行为同时交互）
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(MasterInd);
            this.FailOn(() => Master == null || !Master.Spawned || Master.Dead || Master.Map != pawn.Map);

            Toil follow = ToilMaker.MakeToil("FollowMaster");
            follow.defaultCompleteMode = ToilCompleteMode.Delay;
            follow.defaultDuration = 250;
            follow.tickAction = delegate
            {
                Pawn master = Master;
                if (master == null || !master.Spawned || master.Dead || master.Map != pawn.Map)
                {
                    return;
                }

                if (!pawn.Position.InHorDistOf(master.Position, FollowRadius))
                {
                    pawn.pather.StartPath(master.Position, PathEndMode.ClosestTouch);
                }
                else
                {
                    pawn.pather.StopDead();
                }
            };
            yield return follow;
        }
    }
}
