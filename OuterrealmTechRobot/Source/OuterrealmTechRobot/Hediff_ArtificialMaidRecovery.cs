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
                bool resurrected = ResurrectionUtility.TryResurrect(pawn, new ResurrectionParams
                {
                    gettingScarsChance = 0f,
                    canKidnap = false,
                    canTimeoutOrFlee = false,
                    useAvoidGridSmart = true,
                    canSteal = false,
                    invisibleStun = false
                });

                if (resurrected)
                {
                    CompArtificialMaid.GetCompCached(pawn)?.FullRepair();
                    Find.LetterStack.ReceiveLetter("ArtificialMaid_ResurrectionLetter_Label".Translate(),
                        "ArtificialMaid_ResurrectionLetter_Text".Translate(pawn.LabelShort),
                        LetterDefOf.PositiveEvent, pawn);
                }

                return;
            }

            // 统一使用人造人女仆组件中的完整修复逻辑
            CompArtificialMaid.GetCompCached(pawn)?.FullRepair();
        }

        public override bool ShouldRemove => false;
    }
}
