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
                Gizmo selectGizmo = ContainingSelectionUtility.SelectCarriedThingGizmo(this, innerContainer[0]);
                if (selectGizmo != null)
                {
                    yield return selectGizmo;
                }
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
