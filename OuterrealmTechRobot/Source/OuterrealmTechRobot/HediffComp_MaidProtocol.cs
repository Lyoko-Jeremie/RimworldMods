using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class HediffComp_MaidProtocol : HediffComp
    {
        // 动态间隔变量
        private int currentInterval = 30; // 当前判定间隔
        private int ticksUntilNextCheck = 30; // 倒计时

        // 缓存的女仆数量（避免每帧去数有多少女仆）
        private int cachedMaidCount = 1;
        private int tickSinceLastCount = 0;

        // 算法常量配置
        private const int MinInterval = 15; // 最小间隔：0.25秒（干活时保持高频扫描）
        private const int MaxInterval = 240; // 最大间隔：4.00秒（实在没活干时的休眠状态）
        private const int IdlePenalty = 30; // 每次找不到工作，增加 0.5 秒延迟
        private const int TickPerMaid = 5; // 每个额外女仆增加的防卡顿延迟

        public override void CompPostTick(ref float severityAdjustment)
        {
            base.CompPostTick(ref severityAdjustment);

            ticksUntilNextCheck--;
            tickSinceLastCount++;

            // 1. 缓存更新：每 1000 Ticks (约 16 秒) 统计一次地图上的女仆总数，极其节省性能
            if (tickSinceLastCount > 1000)
            {
                UpdateMaidCount();
                tickSinceLastCount = 0;
            }

            // 2. 核心唤醒逻辑
            if (ticksUntilNextCheck <= 0)
            {
                Pawn pawn = this.Pawn;

                // 判断当前状态：是否闲置
                bool isIdle = (pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait_Wander || pawn.mindState.IsIdle);

                if (isIdle)
                {
                    // 【强制唤醒】破解缓存
                    pawn.mindState.nextJobTick = Find.TickManager.TicksGame;
                    if (pawn.CurJob != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }

                    // 【自适应退避】因为被唤醒后依然闲置（说明没活干），增加下次扫描的间隔时间
                    currentInterval = Math.Min(currentInterval + IdlePenalty, MaxInterval);
                }
                else
                {
                    // 【敏锐模式】正在干活中！将间隔重置为最小，这样她手头工作一干完就能立刻接下一个
                    currentInterval = MinInterval;
                }

                // 3. 计算最终的冷却时间：动态间隔 + 女仆数量惩罚
                // 减去 1 是因为不包含自己
                int popPenalty = Math.Max(0, (cachedMaidCount - 1) * TickPerMaid);

                ticksUntilNextCheck = currentInterval + popPenalty;

                // 可选：打乱同一帧唤醒的概率 (引入随机数防止多个女仆 TPS 峰值叠加)
                ticksUntilNextCheck += Rand.RangeInclusive(-2, 2);
            }
        }

        // 统计当前地图拥有该 Hediff（即女仆协议）的小人数量
        private void UpdateMaidCount()
        {
            if (this.Pawn.Map == null) return;

            int count = 0;
            // 遍历当前地图所有殖民者
            foreach (Pawn p in this.Pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                if (p.health != null && p.health.hediffSet.HasHediff(this.Def))
                {
                    count++;
                }
            }

            cachedMaidCount = count;
        }

        // 存档兼容：保存当前的间隔状态
        public override void CompExposeData()
        {
            base.CompExposeData();
            Scribe_Values.Look(ref currentInterval, "currentInterval", 30);
            Scribe_Values.Look(ref ticksUntilNextCheck, "ticksUntilNextCheck", 30);
        }
    }
}