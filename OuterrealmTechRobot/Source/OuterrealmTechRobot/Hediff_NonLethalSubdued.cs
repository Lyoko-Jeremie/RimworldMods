using Verse;

namespace OuterrealmTechRobot
{
    // 非致命制服状态：在状态存续期间持续刷新“被制服”思绪，保证思绪不会因过期而消失。
    public class Hediff_NonLethalSubdued : HediffWithComps
    {
        public override void PostTick()
        {
            base.PostTick();
            if (pawn.IsHashIntervalTick(250) && pawn.needs?.mood != null &&
                pawn.needs.mood.thoughts?.memories != null)
            {
                pawn.needs.mood.thoughts.memories.TryGainMemory(ArtificialMaidDefOf.ArtificialMaidSubdued_Mood);
            }
        }

        // 兜底：若本状态被其他路径（治疗药、其他 Mod、开发模式等）移除，
        // 同步清理残留的特性、思绪与强制倒地标记，避免状态残留。
        // 注意：RemoveHediff 会先移除 hediff 再调用 PostRemoved，此处不会再触碰 hediff 列表，无递归风险。
        public override void PostRemoved()
        {
            base.PostRemoved();
            if (pawn == null || pawn.Destroyed)
            {
                return;
            }
            NonLethalSubdueUtility.CleanupSubdueRemnants(pawn);
        }
    }
}
