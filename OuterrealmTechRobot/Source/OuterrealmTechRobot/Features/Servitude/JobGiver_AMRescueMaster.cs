using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 携带救援（v1）：主人倒地/昏迷时，女仆将其抱起并送往可用休息点（复用原版 JobDriver_CarryToBed）。
    /// 无可用床时不发起（等待医疗床/玩家安排）。
    /// 配合 Patch_AutoBlink_CarrySync：抱起途中的 AutoBlink 会连主人一起瞬移（战场救援瞬移）。
    /// </summary>
    public class JobGiver_AMRescueMaster : ThinkNode_JobGiver_ServitudeBase
    {
        protected override Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr)
        {
            // 主人倒地/昏迷且需要救援时
            if (!HealthAIUtility.CanRescueNow(pawn, master))
            {
                return null;
            }

            // 主人已被携带（自己或他人）→ 跳过
            if (master.carryTracker != null && master.carryTracker.CarriedThing != null)
            {
                return null;
            }

            // 女仆自己已携带他物 → 跳过
            if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                return null;
            }

            // 寻找主人可用的休息点（sleeper=主人，traveler=女仆），与 JobGiver_RescueNearby 同构
            Building_Bed bed = RestUtility.FindBedFor(master, pawn, false, guestStatus: master.GuestStatus);
            if (bed == null || !pawn.CanReserve(bed))
            {
                return null;
            }

            Job job = JobMaker.MakeJob(JobDefOf.Rescue, master, bed);
            job.count = 1;
            return job;
        }
    }
}
