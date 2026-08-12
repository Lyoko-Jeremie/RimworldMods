using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 跟随主人（兜底行为）：空闲且距主人超过触发距离时，走过去保持跟随距离。
    /// 触发距离 15 格（与渡鸦族 Servitude 一致），保持距离 4 格：
    /// 主人附近时女仆正常工作/生活，主人走远后才跟上，避免"粘住主人不工作"。
    /// </summary>
    public class JobGiver_AMFollowMaster : ThinkNode_JobGiver_ServitudeBase
    {
        /// <summary>跟随触发距离（格）。</summary>
        private const float FollowTriggerDistance = 15f;

        protected override Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr)
        {
            // 仅空闲时跟随（有工作/任务在身时不打断）
            if (pawn.mindState == null || !pawn.mindState.IsIdle)
            {
                return null;
            }

            if (pawn.Position.InHorDistOf(master.Position, FollowTriggerDistance))
            {
                return null;
            }

            return JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_FollowMaster, master);
        }
    }
}
