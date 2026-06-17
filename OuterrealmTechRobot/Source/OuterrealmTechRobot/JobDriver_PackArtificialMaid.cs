using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    public class JobDriver_PackArtificialMaid : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            
            Toil pack = new Toil();
            pack.initAction = delegate
            {
                Pawn actor = pack.actor;
                Thing fabricator = actor.CurJob.targetA.Thing;
                
                // 创建运输箱
                ArtificialMaidTransportBox box = (ArtificialMaidTransportBox)ThingMaker.MakeThing(ArtificialMaidDefOf.ArtificialMaidTransportBox);
                
                // 放入女仆
                actor.DeSpawn();
                box.GetDirectlyHeldThings().TryAdd(actor);
                
                // 生成运输箱在制造机附近
                GenPlace.TryPlaceThing(box, fabricator.Position, fabricator.Map, ThingPlaceMode.Near);
                
                Messages.Message("ArtificialMaidPacked".Translate(actor.LabelShort), box, MessageTypeDefOf.PositiveEvent);
            };
            pack.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return pack;
        }
    }
}
