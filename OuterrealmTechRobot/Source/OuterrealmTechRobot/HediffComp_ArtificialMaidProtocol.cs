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
            var mapComp = ArtificialMaidMapComponent.Get(pawn.Map);
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
            // Idle 标签也可能被其他 Mod 用于有实际行为的工作，不能据此强制结束当前 Job。
            // 这里只允许原版的纯等待、游荡 Job 参与工作覆盖检查。
            Job curJob = pawn.CurJob;
            if (CanCheckForJobOverride(curJob))
            {
                // 使用原生覆盖流程寻找并切换工作。该流程会正确回收未采用的 Job，
                // 并以 InterruptOptional 结束闲置 Job，避免直接强制清理其他 Mod 的工作。
                pawn.jobs.CheckForJobOverride();

                // 如果仍然没有获得实际工作，则逐步降低扫描频率。
                currentInterval = CanCheckForJobOverride(pawn.CurJob)
                    ? Math.Min(currentInterval + IdlePenalty, MaxInterval)
                    : MinInterval;
            }
            else
            {
                // 【敏锐模式】正在干活中（非纯等待/游荡任务），不主动打断。
                // 将间隔重置为最小，这样手头工作结束后能尽快检查下一个任务。
                // 注意：这里重置的是下一次“检查”的间隔，而不是立刻检查
                currentInterval = MinInterval;
            }

            // 3. 计算最终的冷却时间
            // 引入 popPenalty 防止大量女仆同时寻路导致 TPS 暴跌
            int popPenalty = Math.Max(0, (maidCount - 1) * TickPerMaid);
            
            // 基础间隔 + 数量惩罚 + 随机抖动（防止同步效应）
            ticksUntilNextCheck = currentInterval + popPenalty + Rand.RangeInclusive(-5, 5);
        }

        /// <summary>
        /// 判断当前 Job 是否允许通过原生流程检查工作覆盖。
        /// </summary>
        private static bool CanCheckForJobOverride(Job job)
        {
            if (job == null) return true;

            JobDef def = job.def;
            return def == JobDefOf.Wait ||
                   def == JobDefOf.Wait_Wander ||
                   def == JobDefOf.GotoWander;
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
