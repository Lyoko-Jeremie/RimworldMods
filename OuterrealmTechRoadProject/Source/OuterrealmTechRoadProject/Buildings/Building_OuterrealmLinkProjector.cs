using System.Collections.Generic;
using OuterrealmTechRoadProject.UI;
using OuterrealmTechRoadProject.World;
using RimWorld;
using Verse;

namespace OuterrealmTechRoadProject.Buildings
{
    public class Building_OuterrealmLinkProjector : Building
    {
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            if (Faction != Faction.OfPlayer)
            {
                yield break;
            }

            Command_Action planCommand = new Command_Action
            {
                defaultLabel = "OuterrealmTechRoadProject_CommandPlanOuterrealmLink".Translate(),
                defaultDesc = "OuterrealmTechRoadProject_CommandPlanOuterrealmLinkDesc".Translate(),
                icon = OuterrealmLinkTex.IconPlanOuterrealmLink,
                action = delegate
                {
                    OuterrealmLinkPlanner.BeginPlanning(this);
                }
            };
            yield return planCommand;

            if (OuterrealmLinkPlanner.IsPlanning)
            {
                Command_Action cancelCommand = new Command_Action
                {
                    defaultLabel = "OuterrealmTechRoadProject_CommandCancelOuterrealmLinkPlan".Translate(),
                    defaultDesc = "OuterrealmTechRoadProject_CommandCancelOuterrealmLinkPlanDesc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = OuterrealmLinkPlanner.CancelPlanning
                };
                yield return cancelCommand;
            }
        }
    }
}
