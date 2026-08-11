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
                    // 重生后退出高维状态（重生是用户指定的高维退出例外之一）
                    ArtificialMaidHighDimUtility.ExitHighDim(pawn, force: true);
                    CompArtificialMaid.GetCompCached(pawn)?.FullRepair();
                    Find.LetterStack.ReceiveLetter("ArtificialMaid_ResurrectionLetter_Label".Translate(),
                        "ArtificialMaid_ResurrectionLetter_Text".Translate(pawn.LabelShort),
                        LetterDefOf.PositiveEvent, pawn);
                }
                else
                {
                    Find.LetterStack.ReceiveLetter("ArtificialMaid_ResurrectionFailedLetter_Label".Translate(),
                        "ArtificialMaid_ResurrectionFailedLetter_Text".Translate(pawn.LabelShort),
                        LetterDefOf.NegativeEvent, pawn);
                }

                return;
            }

            // 常规循环只处理健康和资源，避免每 250 tick 重复修改背景、特质及第三方组件。
            CompArtificialMaid.GetCompCached(pawn)?.ReplenishResources();
        }

        public override bool ShouldRemove => false;
    }
}
