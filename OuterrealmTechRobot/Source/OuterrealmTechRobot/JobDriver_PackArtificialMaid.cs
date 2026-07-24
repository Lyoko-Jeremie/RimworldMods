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
                Map originalMap = actor.Map;
                IntVec3 originalPosition = actor.Position;

                // 创建运输箱
                ArtificialMaidTransportBox box = (ArtificialMaidTransportBox)ThingMaker.MakeThing(ArtificialMaidDefOf.ArtificialMaidTransportBox);

                // 必须先让空运输箱安全落地，再移动女仆。
                if (originalMap == null ||
                    fabricator == null ||
                    fabricator.Map != originalMap ||
                    !GenPlace.TryPlaceThing(box, fabricator.Position, originalMap, ThingPlaceMode.Near) ||
                    !box.Spawned ||
                    box.Destroyed)
                {
                    ArtificialMaidTransferUtility.LogTransferFailure("Pack.PlaceBox", actor,
                        "Unable to place an empty transport box.");
                    Messages.Message("CannotPackArtificialMaid".Translate(), actor, MessageTypeDefOf.RejectInput);
                    return;
                }

                // 运输箱已成为地图对象，此后即使接收失败也可以安全回滚。
                actor.DeSpawnOrDeselect();
                ThingOwner container = box.GetDirectlyHeldThings();
                bool accepted = ArtificialMaidTransferUtility.TryAddToContainer(actor, container, box);
                if (!accepted)
                {
                    bool recovered = actor.ParentHolder != null ||
                                     ArtificialMaidTransferUtility.TrySpawnNear(actor, originalMap,
                                         originalPosition, out _);

                    // 地图恢复失败时，最后尝试重新放回已落地的运输箱。
                    if (!recovered && actor.ParentHolder == null)
                    {
                        recovered = ArtificialMaidTransferUtility.TryAddToContainer(actor, container, box);
                    }

                    if (!recovered)
                    {
                        recovered = ArtificialMaidTransferUtility.TryKeepInWorld(actor);
                    }

                    ArtificialMaidTransferUtility.LogTransferFailure("Pack.StoreMaid", actor,
                        "Transport box rejected the maid. Recovery=" + recovered);

                    // 仅当女仆已经恢复到其他安全根对象时，才移除空运输箱。
                    if (recovered && !container.Contains(actor) && !box.Destroyed)
                    {
                        box.Destroy();
                    }

                    Messages.Message("CannotPackArtificialMaid".Translate(), actor, MessageTypeDefOf.RejectInput);
                    return;
                }

                Messages.Message("ArtificialMaidPacked".Translate(actor.LabelShort), box, MessageTypeDefOf.PositiveEvent);
            };
            pack.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return pack;
        }
    }
}
