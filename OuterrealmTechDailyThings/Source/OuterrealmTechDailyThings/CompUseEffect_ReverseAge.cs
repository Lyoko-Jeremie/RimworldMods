using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace OuterrealmTechDailyThings
{
    /// <summary>
    /// 超维科技返老还童药剂的使用效果：
    /// 将使用者的生物年龄逆转指定年数（默认 10 年），
    /// 但不会低于种族成年年龄；同时自动移除因年龄获得的生日型老年病。
    ///
    /// 参照原版实现：
    /// - CompBiosculpterPod_AgeReversalCycle.CycleCompleted（生物雕塑舱返老还童）
    /// - PsychicRitualToil_Chronophagy.ReverseAgePawn（噬时者仪式的逆转 + 老年病清除）
    /// </summary>
    public class CompUseEffect_ReverseAge : CompUseEffect
    {
        private CompProperties_UseEffect_ReverseAge ReverseAgeProps =>
            (CompProperties_UseEffect_ReverseAge)props;

        /// <summary>一年的 tick 数（RimWorld 约定）。</summary>
        private const long TicksPerYear = 3600000L;

        /// <summary>
        /// 使用前置校验：仅人形生物、且生物年龄必须严格大于种族成年年龄。
        /// 年龄等于或小于成年年龄时（逆转会被下限钳制、无实际效果），拒绝使用。
        /// </summary>
        public override AcceptanceReport CanBeUsedBy(Pawn p)
        {
            if (p == null || p.ageTracker == null)
            {
                return (AcceptanceReport)"OuterTechAgeReversalInvalidTarget".Translate();
            }

            if (ReverseAgeProps.onlyHumanlike && !p.RaceProps.Humanlike)
            {
                return (AcceptanceReport)"OuterTechAgeReversalNotHumanlike".Translate();
            }

            if (p.ageTracker.AgeBiologicalTicks <= p.ageTracker.AdultMinAgeTicks)
            {
                return (AcceptanceReport)"OuterTechAgeReversalTooYoung".Translate();
            }

            return true;
        }

        /// <summary>
        /// 执行返老还童效果。
        /// </summary>
        public override void DoEffect(Pawn usedBy)
        {
            base.DoEffect(usedBy);

            // 1. 计算目标年龄：不低于种族成年年龄（防止变回儿童/婴儿）。
            long minTicks = usedBy.ageTracker.AdultMinAgeTicks;
            long reverseTicks = (long)(ReverseAgeProps.yearsToReverse * TicksPerYear);
            long targetTicks = Math.Max(minTicks, usedBy.ageTracker.AgeBiologicalTicks - reverseTicks);
            long actualDeltaTicks = usedBy.ageTracker.AgeBiologicalTicks - targetTicks;

            // 2. 直接赋值 AgeBiologicalTicks：
            //    setter 会自动重算成长度（CalculateInitialGrowth）与生命阶段（RecalculateLifeStageIndex）。
            usedBy.ageTracker.AgeBiologicalTicks = targetTicks;

            // 3. 联动 Ideology 的“返老还童需求”前兆，视为一次治疗。
            usedBy.ageTracker.ResetAgeReversalDemand(Pawn_AgeTracker.AgeReversalReason.ViaTreatment);

            // 4. 移除生日型老年病（变年轻不会触发生日回调，必须手动清除，
            //    参照 PsychicRitualToil_Chronophagy.ReverseAgePawn 的逻辑）。
            RemoveBirthdayHediffs(usedBy);

            // 5. 反馈消息：显示实际逆转年数与当前年龄。
            if (PawnUtility.ShouldSendNotificationAbout(usedBy))
            {
                string deltaYears = ((float)actualDeltaTicks / TicksPerYear).ToString("0.#");
                string currentAge = usedBy.ageTracker.AgeBiologicalYearsFloat.ToString("0.#");
                string text = "OuterTechAgeReversalCompleted".Translate(
                    usedBy.LabelCap, deltaYears, currentAge);
                Messages.Message(text, new LookTargets(usedBy), MessageTypeDefOf.PositiveEvent);
            }
        }

        /// <summary>
        /// 移除所有“生日型”老年病（如白内障、老年痴呆等）：
        /// 当逆转后的年龄低于该病的发病年龄曲线起点时，说明该病不应再存在。
        /// </summary>
        private static void RemoveBirthdayHediffs(Pawn pawn)
        {
            List<HediffGiverSetDef> giverSets = pawn.RaceProps.hediffGiverSets;
            if (giverSets == null)
            {
                return;
            }

            float newAgeYears = pawn.ageTracker.AgeBiologicalYearsFloat;
            float lifeExpectancy = pawn.RaceProps.lifeExpectancy;
            List<Hediff> toRemove = new List<Hediff>();

            foreach (HediffGiverSetDef set in giverSets)
            {
                List<HediffGiver> givers = set.hediffGivers;
                if (givers == null)
                {
                    continue;
                }

                foreach (HediffGiver giver in givers)
                {
                    HediffGiver_Birthday birthdayGiver = giver as HediffGiver_Birthday;
                    if (birthdayGiver == null || birthdayGiver.ageFractionChanceCurve == null ||
                        birthdayGiver.ageFractionChanceCurve.Points == null ||
                        birthdayGiver.ageFractionChanceCurve.Points.Count == 0 ||
                        birthdayGiver.hediff == null)
                    {
                        continue;
                    }

                    // 新年龄对应的寿命比例低于发病年龄曲线起点 → 该病应被移除。
                    if (newAgeYears / lifeExpectancy < birthdayGiver.ageFractionChanceCurve.Points[0].x)
                    {
                        pawn.health.hediffSet.GetHediffs<Hediff>(ref toRemove,
                            (Predicate<Hediff>)(hd => hd.def == birthdayGiver.hediff));
                    }
                }
            }

            foreach (Hediff hediff in toRemove)
            {
                pawn.health.RemoveHediff(hediff);
            }
        }
    }
}
