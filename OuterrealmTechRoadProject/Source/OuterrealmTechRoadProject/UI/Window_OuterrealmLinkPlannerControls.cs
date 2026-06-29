using OuterrealmTechRoadProject.World;
using UnityEngine;
using Verse;

namespace OuterrealmTechRoadProject.UI
{
    /// <summary>
    /// 世界地图规划时显示的小型控制窗口。
    /// 完成建造从这里触发，避免“再次点击终点”造成操作歧义或误操作。
    /// </summary>
    public class Window_OuterrealmLinkPlannerControls : Window
    {
        public override Vector2 InitialSize => new Vector2(300f, 128f);

        public Window_OuterrealmLinkPlannerControls()
        {
            doCloseX = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            preventCameraMotion = false;
            draggable = true;
            forcePause = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Rect labelRect = new Rect(inRect.x, inRect.y, inRect.width, 28f);
            Widgets.Label(labelRect, "OuterrealmTechRoadProject_PlannerControlStatus".Translate(OuterrealmLinkPlanner.RouteNodeCount, OuterrealmLinkPlanner.RouteSegmentCount));

            Rect finishRect = new Rect(inRect.x, labelRect.yMax + 8f, inRect.width, 32f);
            bool canFinish = OuterrealmLinkPlanner.RouteSegmentCount > 0;
            if (!canFinish)
            {
                GUI.color = Color.gray;
            }

            if (Widgets.ButtonText(finishRect, "OuterrealmTechRoadProject_CommandFinishOuterrealmLinkPlan".Translate()) && canFinish)
            {
                OuterrealmLinkPlanner.FinishPlanning();
            }

            GUI.color = Color.white;

            Rect cancelRect = new Rect(inRect.x, finishRect.yMax + 8f, inRect.width, 32f);
            if (Widgets.ButtonText(cancelRect, "OuterrealmTechRoadProject_CommandCancelOuterrealmLinkPlan".Translate()))
            {
                OuterrealmLinkPlanner.CancelPlanning();
            }
        }
    }
}
