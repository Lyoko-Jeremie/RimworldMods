using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class FloatMenuOptionProvider_DismissArtificialMaidEscort : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (!ArtificialMaidEscortUtility.CanDismissEscortLeader(clickedPawn))
            {
                yield break;
            }

            Pawn actor = context.FirstSelectedPawn;
            if (!actor.IsColonistPlayerControlled)
            {
                yield break;
            }

            if (!actor.CanReach(clickedPawn, PathEndMode.Touch, Danger.Deadly))
            {
                yield return new FloatMenuOption("CannotDismissArtificialMaidEscort".Translate() + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                yield break;
            }

            if (actor.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
            {
                yield return new FloatMenuOption("CannotPrioritizeWorkTypeDisabled".Translate(SkillDefOf.Social.LabelCap), null);
                yield break;
            }

            Action action = () =>
            {
                Job job = JobMaker.MakeJob(ArtificialMaidDefOf.DismissArtificialMaidEscort, clickedPawn);
                job.playerForced = true;
                actor.jobs.TryTakeOrderedJob(job);
            };

            FloatMenuOption option = new FloatMenuOption("DismissArtificialMaidEscort".Translate(), action, MenuOptionPriority.InitiateSocial, revalidateClickTarget: clickedPawn);
            yield return FloatMenuUtility.DecoratePrioritizedTask(option, actor, clickedPawn);
        }
    }
}
