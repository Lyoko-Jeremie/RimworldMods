using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 跟随主人（兜底 + 行为锚定）：
    /// 有主人即持续跟随（保持距离由 JobDriver_AMFollowMaster 控制），
    /// 用跟随 Job 占住思考树（Humanlike_PostDuty 位于 Work/闲逛/娱乐之前），
    /// 从而阻止女仆执行任何其他空闲任务——所有空闲行为都以侍奉主人为目的。
    /// 优先级由 ThinkTree 节点顺序保证：威胁 > 救援 > 喂食 > 陪伴 > 跟随（本节点最后）。
    /// 玩家手动命令（QueuedJob/右键）仍在树中更靠前，永远优先。
    /// </summary>
    public class JobGiver_AMFollowMaster : ThinkNode_JobGiver_ServitudeBase
    {
        protected override Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr)
        {
            // 仅空闲时跟随（有工作/任务在身时不打断；空闲时持续跟随，阻断其他空闲行为）
            if (pawn.mindState == null || !pawn.mindState.IsIdle)
            {
                return null;
            }

            return JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_FollowMaster, master);
        }
    }
}
