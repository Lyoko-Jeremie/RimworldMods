using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class JobDriver_UnpackArtificialMaid : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            
            yield return Toils_General.Wait(30).WithProgressBarToilDelay(TargetIndex.A);

            Toil unpack = new Toil();
            unpack.initAction = delegate
            {
                ArtificialMaidTransportBox box = (ArtificialMaidTransportBox)unpack.actor.CurJob.targetA.Thing;
                ThingOwner container = box.GetDirectlyHeldThings();
                if (container.Count > 0)
                {
                    Pawn maid = (Pawn)container[0];
                    container.Remove(maid);
                    GenSpawn.Spawn(maid, box.Position, box.Map);
                    
                    if (maid.Faction != Faction.OfPlayer)
                    {
                        maid.SetFaction(Faction.OfPlayer);
                    }

                    Messages.Message("ArtificialMaidUnpacked".Translate(maid.LabelShort), maid, MessageTypeDefOf.PositiveEvent);
                }
                box.Destroy();
            };
            unpack.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return unpack;
        }
    }
}
