using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 侍奉行为公共守卫基类：所有非征召侍奉 JobGiver 的唯一入口。
    /// 通用守卫（def/存活/关系/同图/活动区/留守）由 ArtificialMaidServitudeUtility.CanServe 提供，
    /// 本类在其上叠加模式判定：
    ///   ① 征召维度：draft 时暂停全部侍奉（守卫行为由 JobGiver_AMGuardMaster 负责）；
    ///   ② 高维维度：高维 + 未征召 = 只跟随主人。
    /// 子类实现 TryGiveServitudeJob 即可获得以上全部守卫，零侵入。
    /// </summary>
    public abstract class ThinkNode_JobGiver_ServitudeBase : ThinkNode_JobGiver
    {
        protected sealed override Job TryGiveJob(Pawn pawn)
        {
            // 通用守卫（快速失败链，全 O(1)/廉价判定）
            if (!ArtificialMaidServitudeUtility.CanServe(pawn, out CompArtificialMaid comp, out Pawn master, out ArtificialMaidServitudeManager mgr))
            {
                return null;
            }

            // ① 征召维度：draft 时暂停全部侍奉（守卫行为由 JobGiver_AMGuardMaster 负责）
            if (pawn.Drafted)
            {
                return null;
            }

            // ② 高维维度：高维 + 未征召 = 只跟随主人（持续跟随，阻断其他空闲行为）
            if (comp.isHighDim)
            {
                return TryGiveHighDimFollowJob(pawn, master);
            }

            return TryGiveServitudeJob(pawn, master, mgr);
        }

        /// <summary>高维模式下的跟随行为（复用跟随 Job，持续跟随）。</summary>
        private Job TryGiveHighDimFollowJob(Pawn pawn, Pawn master)
        {
            // 仅空闲时跟随（空闲时持续跟随）
            if (pawn.mindState == null || !pawn.mindState.IsIdle)
            {
                return null;
            }

            return JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_FollowMaster, master);
        }

        /// <summary>子类实现具体侍奉行为。守卫基类已保证：女仆、存活、同图、有主人、非留守、未征召、非高维。</summary>
        protected abstract Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr);
    }
}
