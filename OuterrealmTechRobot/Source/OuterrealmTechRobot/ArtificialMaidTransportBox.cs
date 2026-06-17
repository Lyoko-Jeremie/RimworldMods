using RimWorld;
using Verse;
using Verse.AI;
using System.Collections.Generic;
using UnityEngine;

namespace OuterrealmTechRobot
{
    public class ArtificialMaidTransportBox : ThingWithComps, IThingHolder
    {
        private ThingOwner innerContainer;

        public ArtificialMaidTransportBox()
        {
            innerContainer = new ThingOwner<Thing>(this);
        }

        public new IThingHolder ParentHolder => base.ParentHolder;

        public ThingOwner GetDirectlyHeldThings() => innerContainer;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            if (innerContainer.Count > 0)
            {
                Thing containedThing = innerContainer[0];
                Command_Action selectContained = new Command_Action();
                selectContained.defaultLabel = "CommandSelectContainedPawn".Translate(containedThing.LabelCap);
                selectContained.defaultDesc = "CommandSelectContainedPawnDesc".Translate();
                
                if (containedThing is Pawn pawn)
                {
                    selectContained.icon = (Texture)PortraitsCache.Get(pawn, new Vector2(75f, 75f), Rot4.South);
                }
                else
                {
                    selectContained.icon = containedThing.def.uiIcon;
                }
                
                selectContained.action = () =>
                {
                    CameraJumper.TryJumpAndSelect(containedThing);
                };
                yield return selectContained;
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (var opt in base.GetFloatMenuOptions(selPawn)) yield return opt;

            if (innerContainer.Count > 0)
            {
                if (!selPawn.CanReach(this, Verse.AI.PathEndMode.Touch, Danger.Deadly))
                {
                    yield return new FloatMenuOption("CannotUnpackArtificialMaid".Translate() + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                }
                else
                {
                    yield return new FloatMenuOption("UnpackArtificialMaid".Translate(), () =>
                    {
                        Job job = JobMaker.MakeJob(ArtificialMaidDefOf.UnpackArtificialMaid, this);
                        selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    });
                }
            }
        }
        
        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (innerContainer.Count > 0)
            {
                if (!text.NullOrEmpty()) text += "\n";
                text += "ContainsArtificialMaid".Translate(innerContainer[0].LabelCap);
            }
            return text;
        }
    }
}
