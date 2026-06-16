using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class HediffCompProperties_ArtificialMaidProtocol : HediffCompProperties
    {
        public HediffCompProperties_ArtificialMaidProtocol()
        {
            this.compClass = typeof(HediffComp_ArtificialMaidProtocol);
        }
    }

    public class HediffComp_ArtificialMaidProtocol : HediffComp
    {
        // 动态间隔变量
        private int currentInterval = 30; // 当前判定间隔
        private int ticksUntilNextCheck = 30; // 倒计时

        // 静态计数器和更新标记，用于跨实例共享数据，减少性能损耗
        private static int cachedMaidCount = 1;
        private static int lastUpdateTick = -1;
        private static readonly object Locker = new object();

        // 算法常量配置
        private const int MinInterval = 30; // 最小间隔：0.5秒（避免过于频繁，哪怕在干活时）
        private const int MaxInterval = 300; // 最大间隔：5.00秒（休眠状态）
        private const int IdlePenalty = 60; // 每次找不到工作，增加 1 秒延迟
        private const int TickPerMaid = 3; // 每个额外女仆增加的防卡顿延迟

        public override void CompPostTick(ref float severityAdjustment)
        {
            // 如果倒计时没到，直接跳过，极轻量化
            if (ticksUntilNextCheck > 0)
            {
                ticksUntilNextCheck--;
                return;
            }

            Pawn pawn = this.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead) return;

            // 1. 获取全局女仆数量（每 2000 ticks 更新一次，约 33 秒）
            UpdateGlobalMaidCount(pawn.Map);

            // 2. 核心逻辑判断
            // 判断当前状态：是否真正闲置
            // CurJob == null: 没活干
            // Wait_Wander: 正在漫无目的地走
            // IsIdle: 逻辑层面的闲置
            bool isIdle = (pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait_Wander || pawn.mindState.IsIdle);

            if (isIdle)
            {
                // 【积极抢先】打断闲置状态。
                // 仅在真的闲置时触发重新寻找 Job
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
                // 注意：这里重置的是下一次“检查”的间隔，而不是立刻检查
                currentInterval = MinInterval;
            }

            // 3. 计算最终的冷却时间
            // 引入 popPenalty 防止大量女仆同时寻路导致 TPS 暴跌
            int popPenalty = Math.Max(0, (cachedMaidCount - 1) * TickPerMaid);
            
            // 基础间隔 + 数量惩罚 + 随机抖动（防止同步效应）
            ticksUntilNextCheck = currentInterval + popPenalty + Rand.RangeInclusive(-5, 5);
        }

        private static void UpdateGlobalMaidCount(Map map)
        {
            if (map == null) return;
            
            int currentTick = Find.TickManager.TicksGame;
            // 如果当前 tick 和上次更新 tick 距离太近，就不更新
            if (lastUpdateTick > 0 && currentTick - lastUpdateTick < 2000) return;

            lock (Locker)
            {
                // 双重检查锁定
                if (lastUpdateTick > 0 && currentTick - lastUpdateTick < 2000) return;
                
                int count = 0;
                // 使用更高效的遍历方式。FreeColonistsSpawned 已经相对较快
                var colonists = map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < colonists.Count; i++)
                {
                    Pawn p = colonists[i];
                    // 检查是否拥有 ArtificialMaid 特有的 Hediff
                    if (p.health?.hediffSet?.HasHediff(ArtificialMaidDefOf.ArtificialMaidRecovery) ?? false)
                    {
                        count++;
                    }
                }
                cachedMaidCount = count;
                lastUpdateTick = currentTick;
            }
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