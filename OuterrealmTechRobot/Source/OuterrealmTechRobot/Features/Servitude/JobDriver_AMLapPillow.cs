using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 膝枕：主人休息时，女仆走过去让其枕在腿上。
    /// - 强制打断主人当前工作，改为"维持姿势"（Wait_MaintainPosture，2600 tick 过期），主人呈躺卧姿势；
    /// - 2500 tick 内主人休息值持续回复（女仆版恢复速率 = 渡鸦版的 2 倍，体现超维科技设定）；
    /// - 每 100 tick 双方喷心形粒子；结束时自动释放主人恢复自由。
    /// </summary>
    public class JobDriver_AMLapPillow : JobDriver
    {
        private const int DurationTicks = 2500;
        private const TargetIndex MasterInd = TargetIndex.A;

        /// <summary>休息值每 tick 恢复量（渡鸦约 4.571429E-05，女仆版双倍）。2500 tick 约恢复 22.9%。</summary>
        private const float RestPerTick = 4.571429E-05f * 2f;

        private Pawn Master => (Pawn)job.GetTarget(MasterInd).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Master, job, errorOnFailed: errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(MasterInd);
            this.FailOnDowned(MasterInd);

            // 主人被征召（战斗指挥中）→ 中断膝枕，不打扰
            this.FailOn(() => Master != null && Master.Drafted);

            yield return Toils_Goto.GotoThing(MasterInd, PathEndMode.Touch);

            // 准备：锁定主人姿势
            Toil setup = ToilMaker.MakeToil("SetupLapPillow");
            setup.defaultCompleteMode = ToilCompleteMode.Instant;
            setup.initAction = delegate
            {
                Pawn master = Master;
                if (master == null || master.Dead)
                {
                    return;
                }

                master.pather.StopDead();
                Job waitJob = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                waitJob.expiryInterval = DurationTicks + 100;
                master.jobs.StartJob(waitJob, JobCondition.InterruptForced, keepCarryingThingOverride: true);
                master.rotationTracker.FaceCell(pawn.Position);
                pawn.pather.StopDead();
                pawn.rotationTracker.FaceTarget(master);
            };
            yield return setup;

            // 膝枕主体：持续 2500 tick
            Toil lap = ToilMaker.MakeToil("LapPillow");
            lap.defaultCompleteMode = ToilCompleteMode.Delay;
            lap.defaultDuration = DurationTicks;
            lap.socialMode = RandomSocialMode.Off;
            lap.handlingFacing = true;
            lap.tickAction = delegate
            {
                Pawn master = Master;
                if (master == null || master.Dead)
                {
                    return;
                }

                pawn.rotationTracker.FaceTarget(master);
                master.rotationTracker.FaceCell(pawn.Position);
                master.jobs.posture = PawnPosture.LayingInBed;

                // 主人休息值持续回复
                if (master.needs?.rest != null)
                {
                    master.needs.rest.CurLevel = Mathf.Min(master.needs.rest.CurLevel + RestPerTick, master.needs.rest.MaxLevel);
                }

                // 心形粒子
                if (pawn.IsHashIntervalTick(100))
                {
                    FleckMaker.ThrowMetaIcon(pawn.Position, pawn.Map, FleckDefOf.Heart, 0.42f);
                    FleckMaker.ThrowMetaIcon(master.Position, master.Map, FleckDefOf.Heart, 0.42f);
                }
            };
            lap.AddFinishAction(delegate
            {
                Pawn master = Master;
                if (master != null && !master.Dead && master.CurJobDef == JobDefOf.Wait_MaintainPosture)
                {
                    master.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            });
            yield return lap;
        }
    }
}
