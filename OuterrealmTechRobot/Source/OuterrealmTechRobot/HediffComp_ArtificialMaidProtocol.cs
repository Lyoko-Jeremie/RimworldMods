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

            // 1. 获取地图女仆数量
            int maidCount = 1;
            var mapComp = pawn.Map?.GetComponent<ArtificialMaidMapComponent>();
            if (mapComp != null)
            {
                maidCount = mapComp.MaidCount;
                // 如果计数器为 0 (极端情况，例如 SpawnSetup 没被调用或被其他 mod 覆盖)，则补充注册
                if (maidCount == 0)
                {
                    mapComp.RegisterMaid(pawn);
                    maidCount = mapComp.MaidCount;
                }
            }

            // 2. 核心逻辑判断
            // 判定当前状态：
            // - isIdleTag: 逻辑层面的闲置（根据 lastJobTag == Idle 判定，涵盖游荡移动和等待）
            // - isWaitingIdle: 真正的等待闲置（没活干，或者正在原地游荡等待）
            Job curJob = pawn.CurJob;
            bool isIdleTag = pawn.mindState.IsIdle;
            bool isMoving = pawn.pather != null && pawn.pather.Moving;
            bool isIdle = (curJob == null || curJob.def == JobDefOf.Wait_Wander || isIdleTag);
            bool isWaitingIdle = isIdle && !isMoving;

            if (isWaitingIdle)
            {
                // 【积极抢先】打断正在进行的闲置等待。
                // 仅在真的原地闲置时触发重新寻找 Job
                if (curJob != null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                // 【自适应退避】因为被唤醒后依然闲置（说明没活干），增加下次扫描的间隔时间
                currentInterval = Math.Min(currentInterval + IdlePenalty, MaxInterval);
            }
            else if (isIdle && isMoving)
            {
                // 【游荡保护】如果正在游荡的移动过程中，不要打断，也不重置间隔。
                // 这样可以避免“走走停停”的现象，让女仆能流畅地完成游荡。
            }
            else
            {
                // 【敏锐模式】正在干活中（非闲置任务）！将间隔重置为最小，这样她手头工作一干完就能立刻接下一个
                // 注意：这里重置的是下一次“检查”的间隔，而不是立刻检查
                currentInterval = MinInterval;
            }

            // 3. 计算最终的冷却时间
            // 引入 popPenalty 防止大量女仆同时寻路导致 TPS 暴跌
            int popPenalty = Math.Max(0, (maidCount - 1) * TickPerMaid);
            
            // 基础间隔 + 数量惩罚 + 随机抖动（防止同步效应）
            ticksUntilNextCheck = currentInterval + popPenalty + Rand.RangeInclusive(-5, 5);
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