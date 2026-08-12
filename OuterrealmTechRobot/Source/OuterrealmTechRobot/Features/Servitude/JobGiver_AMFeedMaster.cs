using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 喂食：主人饥饿时，女仆寻找食物喂给主人（复用原版 JobDriver_Feed）。
    /// 触发条件：主人食物需求低于饥饿阈值，且能找到可预留的食物源。
    /// </summary>
    public class JobGiver_AMFeedMaster : ThinkNode_JobGiver_ServitudeBase
    {
        protected override Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr)
        {
            // 主人饥饿才喂食
            if (master.needs?.food == null)
            {
                return null;
            }

            if (master.needs.food.CurLevelPercentage >= master.needs.food.PercentageThreshHungry)
            {
                return null;
            }

            // 主人已倒地且正被他人喂食/治疗时跳过，避免抢单
            if (master.CurJob != null && (master.CurJob.def == JobDefOf.FeedPatient || master.CurJob.def == JobDefOf.TendPatient))
            {
                return null;
            }

            // 寻找食物源（getter=女仆，eater=主人）
            Thing foodSource;
            ThingDef foodDef;
            if (!FoodUtility.TryFindBestFoodSourceFor(pawn, master, false, out foodSource, out foodDef))
            {
                return null;
            }

            if (foodSource == null || !pawn.CanReserve(foodSource))
            {
                return null;
            }

            // targetA = 食物，targetB = 主人（JobDriver_Feed 约定）
            return JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_FeedMaster, foodSource, master);
        }
    }
}
