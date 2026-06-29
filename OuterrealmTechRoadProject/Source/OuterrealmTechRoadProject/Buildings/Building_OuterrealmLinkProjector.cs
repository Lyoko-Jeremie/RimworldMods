using System.Collections.Generic;
using OuterrealmTechRoadProject.UI;
using OuterrealmTechRoadProject.World;
using RimWorld;
using Verse;

namespace OuterrealmTechRoadProject.Buildings
{
    /// <summary>
    /// 超维链路投射器建筑。
    /// 当前版本它只负责提供世界地图规划入口；资源、能量、施工时间后续再扩展。
    /// </summary>
    public class Building_OuterrealmLinkProjector : Building
    {
        /// <summary>
        /// 给玩家选中建筑时显示额外命令。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            // 保留原版建筑自带命令，例如重装、复制建造等。
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            // 非玩家阵营建筑不提供规划能力，避免敌对或无主建筑被玩家直接使用。
            if (Faction != Faction.OfPlayer)
            {
                yield break;
            }

            // 主命令：进入世界地图路线规划模式。
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

            // 如果当前已经在规划，显示一个显式取消按钮；世界目标器自身也有取消按钮。
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
