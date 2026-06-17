using RimWorld;
using Verse;
using Verse.AI;
using System.Collections.Generic;

namespace OuterrealmTechRobot
{
    public class CompProperties_ArtificialMaidPacker : CompProperties
    {
        public CompProperties_ArtificialMaidPacker()
        {
            this.compClass = typeof(CompArtificialMaidPacker);
        }
    }

    public class CompArtificialMaidPacker : ThingComp
    {
        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            // 当选中女仆时，右键点击制造机显示打包选项
            if (selPawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                if (!selPawn.CanReach(this.parent, PathEndMode.Touch, Danger.Deadly))
                {
                    yield return new FloatMenuOption("CannotPackArtificialMaid".Translate() + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                }
                else
                {
                    yield return new FloatMenuOption("PackArtificialMaid".Translate(), () =>
                    {
                        Job job = JobMaker.MakeJob(ArtificialMaidDefOf.PackArtificialMaid, this.parent);
                        selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    });
                }
            }
        }
    }
}
