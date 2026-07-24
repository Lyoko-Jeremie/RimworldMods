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
                Map map = box.Map;
                IntVec3 boxPosition = box.Position;

                if (container.Count == 0 || !(container[0] is Pawn maid) ||
                    maid.def != ArtificialMaidDefOf.ArtificialMaid)
                {
                    ArtificialMaidTransferUtility.LogTransferFailure("Unpack.ValidateContents", null,
                        "Transport box does not contain a valid Artificial Maid.");
                    Messages.Message("CannotUnpackArtificialMaid".Translate(), box, MessageTypeDefOf.RejectInput);
                    return;
                }

                // 不提前 Remove；GenSpawn 通过前置检查后会自动从 ThingOwner 移除 Pawn。
                bool spawned = ArtificialMaidTransferUtility.TrySpawnNear(maid, map, boxPosition, out _);
                if (!spawned)
                {
                    // 某些补丁可能改变 GenSpawn 的返回路径；以 Pawn 的实际地图状态为准。
                    spawned = ArtificialMaidTransferUtility.IsSafelySpawned(maid, map);
                }

                if (!spawned)
                {
                    bool recovered = container.Contains(maid) && maid.ParentHolder == box;
                    if (!recovered && !maid.Spawned && maid.ParentHolder == null)
                    {
                        recovered = ArtificialMaidTransferUtility.TryAddToContainer(maid, container, box);
                    }

                    if (!recovered)
                    {
                        recovered = ArtificialMaidTransferUtility.TrySpawnNear(maid, map, boxPosition, out _);
                    }

                    if (!recovered)
                    {
                        recovered = ArtificialMaidTransferUtility.TryKeepInWorld(maid);
                    }

                    ArtificialMaidTransferUtility.LogTransferFailure("Unpack.SpawnMaid", maid,
                        "Unable to spawn maid near the transport box. Recovery=" + recovered +
                        ", " + ArtificialMaidTransferUtility.DescribeSpawnState(maid, map));
                    Messages.Message("CannotUnpackArtificialMaid".Translate(), box, MessageTypeDefOf.RejectInput);
                    return;
                }

                if (maid.Faction != Faction.OfPlayer)
                {
                    maid.SetFaction(Faction.OfPlayer);
                }

                // 只有女仆已经确认生成且箱子已经为空时，才能销毁运输箱。
                if (container.Count != 0)
                {
                    ArtificialMaidTransferUtility.LogTransferFailure("Unpack.Finalize", maid,
                        "Maid spawned but the source container is not empty.");
                    Messages.Message("CannotUnpackArtificialMaid".Translate(), box, MessageTypeDefOf.RejectInput);
                    return;
                }

                box.Destroy();
                Messages.Message("ArtificialMaidUnpacked".Translate(maid.LabelShort), maid,
                    MessageTypeDefOf.PositiveEvent);
            };
            unpack.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return unpack;
        }
    }
}
