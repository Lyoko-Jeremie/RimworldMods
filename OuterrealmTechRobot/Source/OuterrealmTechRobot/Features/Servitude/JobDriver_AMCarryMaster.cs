using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 抱起主人并护送的工作驱动（借鉴 WolfeinMihoRatkinCarry 的"活物携带"）：
    ///   走到主人 → 抱起（塞入 carryTracker，主人停止自主行为）→ 护送：
    ///   找到主人可用床 → 送床并放到床上（RestUtility.TuckIntoBed）；
    ///   无床 → 抱着主人原地等待/周期性重新找床，绝不丢下主人。
    /// 放下动作经 ArtificialMaidCarryDropGuard 放行（原版 TryDropCarriedThing 会被拦截补丁拦下）。
    /// </summary>
    public class JobDriver_AMCarryMaster : JobDriver
    {
        private const TargetIndex MasterInd = TargetIndex.A;

        /// <summary>视为"已到床边"的距离（格）。</summary>
        private const float BedProximity = 3f;

        private Pawn Master => (Pawn)job.GetTarget(MasterInd).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Master, job, errorOnFailed: errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            bool pickedUp = false;

            // 只检查目标被销毁（被抱主人已 DeSpawn，不能再用 FailOnDespawnedOrNull 的 Spawned 检查）
            this.FailOnDestroyedOrNull(MasterInd);
            // 抱起前：主人须在地图上同图；抱起后主人已 DeSpawn（Map 恒为 null），仅检查存活
            this.FailOn(() => Master == null || Master.Dead || (!pickedUp && (!Master.Spawned || Master.Map != pawn.Map)));
            // 抱起前主人精神崩溃 → 放弃（被抱后不 tick，崩溃不会自行恢复，不能抱着崩溃者）
            this.FailOn(() => !pickedUp && Master.InMentalState);
            // 主人已被其他载体抱走（如原版 Rescue 接手）→ 任务自然结束
            this.FailOn(() => pickedUp && pawn.carryTracker?.CarriedThing != Master);

            yield return Toils_Goto.GotoThing(MasterInd, PathEndMode.Touch);

            Toil pickUp = ToilMaker.MakeToil("CarryMasterPickUp");
            pickUp.initAction = delegate
            {
                Pawn master = Master;
                if (master.CurJob != null)
                {
                    master.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                master.pather?.StopDead();
                if (!ArtificialMaidCarryUtility.TryStartCarryMaster(pawn, master))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                pickedUp = true;
            };
            pickUp.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return pickUp;

            // 护送循环：找床 → 送床；无床 → 原地等待（抱着不放，周期性重找）。
            // Never 模式：永不自动完成，只有到床边才 JumpToToil 进入放下 toil，杜绝"到点放下"。
            Toil escort = ToilMaker.MakeToil("CarryMasterEscort");
            Toil tuckIntoBed = ToilMaker.MakeToil("CarryMasterTuckIntoBed");
            escort.defaultCompleteMode = ToilCompleteMode.Never;
            escort.handlingFacing = true;
            escort.tickAction = delegate
            {
                ArtificialMaidCarryUtility.SyncCarriedMasterPosition(pawn);

                // 防御：被抱后不 tick，理论上不会中途崩溃；若发生则原地等待
                if (Master.InMentalState)
                {
                    return;
                }

                // 节流：每 120 tick 评估一次找床/寻路（FindBedFor 为全图搜索，不能每 tick 调用）
                if (!pawn.IsHashIntervalTick(120))
                {
                    return;
                }

                Building_Bed bed = RestUtility.FindBedFor(Master, pawn, false, guestStatus: Master.GuestStatus);
                if (bed == null || bed.Destroyed)
                {
                    return; // 无床：原地等待，主人抱在怀里，绝不放下
                }

                if (pawn.Position.InHorDistOf(bed.Position, BedProximity))
                {
                    JumpToToil(tuckIntoBed);
                }
                else
                {
                    pawn.pather.StartPath(bed.InteractionCell, PathEndMode.Touch);
                }
            };
            yield return escort;

            // 到床边：放床上（TuckIntoBed 内部经 DropGuard 放行丢弃；主人被塞进床）
            tuckIntoBed.initAction = delegate
            {
                Building_Bed bed = RestUtility.FindBedFor(Master, pawn, false, guestStatus: Master.GuestStatus);
                if (bed != null && !bed.Destroyed)
                {
                    ArtificialMaidCarryDropGuard.AllowDropNow(pawn,
                        () => RestUtility.TuckIntoBed(bed, pawn, Master, true));
                }
                else
                {
                    // 床消失（被拆/被占）→ 就近放下，避免无限等待
                    ArtificialMaidCarryUtility.DropCarriedMaster(pawn);
                }
            };
            tuckIntoBed.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return tuckIntoBed;
        }
    }
}
