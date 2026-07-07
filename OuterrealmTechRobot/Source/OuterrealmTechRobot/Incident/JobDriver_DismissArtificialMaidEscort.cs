using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class JobDriver_DismissArtificialMaidEscort : JobDriver
    {
        private Pawn Leader => (Pawn)TargetThingA;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !ArtificialMaidEscortUtility.CanDismissEscortLeader(Leader));

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.WaitWith(TargetIndex.A, 120, true, face: TargetIndex.A);
            yield return Toils_General.Do(() => ArtificialMaidEscortUtility.TryDismissEscort(Leader));
        }
    }
}
