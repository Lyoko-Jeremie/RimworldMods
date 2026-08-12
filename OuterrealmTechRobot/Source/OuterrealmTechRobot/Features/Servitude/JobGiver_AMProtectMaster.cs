using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 保卫主人（未征召）：主人周围威胁半径内出现敌对 Pawn 时，优先处理。
    /// - 近距（≤5 格）：直接近战攻击；
    /// - 远距：先 AutoBlink 跳脸再攻击（复用 CompArtificialMaid.TryBlinkToTarget，冷却由 AutoBlink 内置）。
    /// 性能：思考树节拍触发 + 250 tick 分频 + 12 格小半径径向扫描（ArtificialMaidServitudeUtility），不做全图扫描。
    /// </summary>
    public class JobGiver_AMProtectMaster : ThinkNode_JobGiver_ServitudeBase
    {
        /// <summary>威胁扫描半径（格）。</summary>
        private const float ThreatRadius = 12f;

        /// <summary>近战直接攻击距离阈值平方（5 格）。</summary>
        private const float MeleeDistSq = 5f * 5f;

        protected override Job TryGiveServitudeJob(Pawn pawn, Pawn master, ArtificialMaidServitudeManager mgr)
        {
            // 分频：思考树节拍之外再限 250 tick，避免频繁全向扫描
            if (!pawn.IsHashIntervalTick(250))
            {
                return null;
            }

            Pawn threat = ArtificialMaidServitudeUtility.FindHostileThreatNear(master, ThreatRadius);
            if (threat == null)
            {
                return null;
            }

            // 远距 → blink 跳脸（成功则直接攻击）
            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp != null && pawn.Position.DistanceToSquared(threat.Position) > MeleeDistSq)
            {
                if (comp.TryBlinkToTarget(threat))
                {
                    return JobMaker.MakeJob(JobDefOf.AttackMelee, threat);
                }
            }

            // 近距或 blink 失败 → 常规近战（可到达才发 Job）
            if (!pawn.CanReserveAndReach(threat, PathEndMode.Touch, Danger.Deadly))
            {
                return null;
            }

            return JobMaker.MakeJob(JobDefOf.AttackMelee, threat);
        }
    }
}
