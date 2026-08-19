using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 携带主人（抱起移动）JobGiver：
    ///   触发 A（救援兜底）：主人倒地/昏迷需要救援且未被任何载体携带、女仆自身未携带时，
    ///     抱起并护送（无床也能抱起——原版 Rescue 需先找到床，本节点作为无床兜底）。
    ///   触发 B（持续携带）：女仆已抱着主人 → 恢复护送（如征召/战斗中断后），绝不丢下主人。
    ///
    /// 注意：不继承 ThinkNode_JobGiver_ServitudeBase——其 CanServe 拒绝"主人倒地"
    /// （master.Downed → false），而救援场景恰恰要求主人倒地，故自建守卫并允许倒地主人。
    /// 守卫项与 CanServe 对齐：女仆 def / 存活生成 / 关系同图 / 活动区域 / 留守 / 征召 / 高维。
    /// </summary>
    public class JobGiver_AMCarryFollow : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            // ① def 短路（非女仆零开销返回）
            if (pawn.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return null;
            }

            // ② 存活/生成/地图
            if (pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.Map == null)
            {
                return null;
            }

            // ③ 征召：携带中被打断时主人由掉落拦截守卫兜底，取消征召后本节点恢复护送
            if (pawn.Drafted)
            {
                return null;
            }

            // ④ 关系存在、主人存活且同图（允许主人倒地——救援场景）
            ArtificialMaidServitudeManager mgr = ArtificialMaidServitudeManager.Get();
            if (mgr == null)
            {
                return null;
            }

            Pawn master = mgr.GetMaster(pawn);
            if (master == null || master.Dead)
            {
                return null;
            }

            // 触发 B：已抱着主人 → 恢复携带护送。
            // 注意：被抱主人已 DeSpawn（Map 恒为 null），必须在本分支跳过错位的同图检查。
            if (ArtificialMaidCarryUtility.IsCarryingMaster(pawn))
            {
                return MakeCarryJob(master);
            }

            // 触发 A：主人须在地图上且与女仆同图
            if (master.Map != pawn.Map)
            {
                return null;
            }

            // ⑤ 活动区域尊重
            if (pawn.playerSettings != null && pawn.playerSettings.RespectsAllowedArea)
            {
                Area area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
                if (area != null && !area[master.Position])
                {
                    return null;
                }
            }

            // ⑥ 留守/高维
            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp == null || comp.standbyMode || comp.isHighDim)
            {
                return null;
            }

            // 触发 A：主人需要救援（倒地/昏迷）
            if (!HealthAIUtility.CanRescueNow(pawn, master))
            {
                return null;
            }

            // 主人已被其他载体携带 → 交给对方（避免争抢）
            if (master.carryTracker != null && master.carryTracker.CarriedThing != null)
            {
                return null;
            }

            // 女仆自身已携带他物 → 跳过
            if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
            {
                return null;
            }

            return MakeCarryJob(master);
        }

        private Job MakeCarryJob(Pawn master)
        {
            Job job = JobMaker.MakeJob(ArtificialMaidDefOf.AM_Job_CarryMaster, master);
            job.count = 1;
            return job;
        }
    }
}
