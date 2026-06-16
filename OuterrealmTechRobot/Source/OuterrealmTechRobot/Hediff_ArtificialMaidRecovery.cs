using RimWorld;
using Verse;

namespace OuterrealmTechRobot
{
    // Method 2: Automatic Resurrection Hediff Logic
    public class Hediff_ArtificialMaidRecovery : HediffWithComps
    {
        public override void PostTick()
        {
            base.PostTick();
            if (pawn.IsHashIntervalTick(250))
            {
                this.ManualTickRare();
            }
        }

        public void ManualTickRare()
        {
            if (pawn.Dead)
            {
                ResurrectionUtility.TryResurrect(pawn, new ResurrectionParams
                {
                    gettingScarsChance = 0f,
                    canKidnap = false,
                    canTimeoutOrFlee = false,
                    useAvoidGridSmart = true,
                    canSteal = false,
                    invisibleStun = false
                });
                pawn.health.Reset();
                Find.LetterStack.ReceiveLetter("ArtificialMaid_ResurrectionLetter_Label".Translate(),
                    "ArtificialMaid_ResurrectionLetter_Text".Translate(pawn.LabelShort), LetterDefOf.PositiveEvent,
                    pawn);
            }
            else
            {
                // 修复伤害和肢体缺失
                bool changed = false;

                // 恢复缺失的肢体
                bool missingFound = false;
                foreach (var mp in pawn.health.hediffSet.GetMissingPartsCommonAncestors())
                {
                    pawn.health.RestorePart(mp.Part);
                    missingFound = true;
                }

                if (missingFound)
                {
                    changed = true;
                }

                // 治愈所有伤口（包括永久性伤害）
                var hediffs = pawn.health.hediffSet.hediffs;
                for (int i = hediffs.Count - 1; i >= 0; i--)
                {
                    if (hediffs[i] is Hediff_Injury injury)
                    {
                        pawn.health.RemoveHediff(injury);
                        changed = true;
                    }
                }

                if (changed)
                {
                    pawn.health.Notify_HediffChanged(null);
                    Messages.Message("ArtificialMaid_RepairMessage".Translate(pawn.LabelShort), pawn,
                        MessageTypeDefOf.PositiveEvent);
                }
            }
        }

        public override bool ShouldRemove => false;
    }
}