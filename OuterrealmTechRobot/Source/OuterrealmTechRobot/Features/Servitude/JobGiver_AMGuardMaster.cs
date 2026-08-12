using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 征召守卫（模式 C）：仅在「女仆被征召 + guardModeEnabled」时工作，优先级最高（ThinkTree 首节点）。
    /// - ① 拦截：主人周围威胁 → 近战攻击（远距先 blink 跳脸，复用 TryBlinkToTarget）；威胁扫描 120 tick 分频；
    /// - ② 无威胁 → 紧跟主人（每思考节拍判定：距主人 >4 格即跟随，主人移动时伴随移动）。
    /// 与玩家指挥的优先级：原版 JobGiver_Orders（玩家右键，playerForced）在本节点之前评估，
    /// 守卫是"兜底行为"，玩家右键移动/攻击永远优先。
    /// 性能：威胁扫描分频 + 12 格小半径，紧跟判定 O(1)，无全图遍历。
    /// </summary>
    public class JobGiver_AMGuardMaster : ThinkNode_JobGiver
    {
        /// <summary>威胁扫描半径（格）。</summary>
        private const float ThreatRadius = 12f;

        /// <summary>紧跟保持距离（格）：主人移动时持续追至该距离内。</summary>
        private const float FollowRadius = 4f;

        /// <summary>近战直接攻击距离阈值平方（5 格）。</summary>
        private const float MeleeDistSq = 5f * 5f;

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!ArtificialMaidServitudeUtility.CanServe(pawn, out CompArtificialMaid comp, out Pawn master, out ArtificialMaidServitudeManager mgr))
            {
                return null;
            }

            // 仅征召 + 守卫开关（未征召时守卫不生效，交给常规侍奉）
            if (!pawn.Drafted || !comp.guardModeEnabled)
            {
                return null;
            }

            // ① 拦截威胁（分频：120 tick，避免频繁径向扫描）
            if (pawn.IsHashIntervalTick(120))
            {
                Pawn threat = ArtificialMaidServitudeUtility.FindHostileThreatNear(master, ThreatRadius);
                if (threat != null)
                {
                    if (pawn.Position.DistanceToSquared(threat.Position) > MeleeDistSq && comp.TryBlinkToTarget(threat))
                    {
                        return JobMaker.MakeJob(JobDefOf.AttackMelee, threat);
                    }

                    if (pawn.CanReserveAndReach(threat, PathEndMode.Touch, Danger.Deadly))
                    {
                        return JobMaker.MakeJob(JobDefOf.AttackMelee, threat);
                    }

                    return null;
                }
            }

            // ② 无威胁 → 紧跟主人（每思考节拍判定，主人移动时伴随移动）
            if (pawn.Position.InHorDistOf(master.Position, FollowRadius))
            {
                return null;
            }

            return JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_FollowMaster, master);
        }
    }
}
