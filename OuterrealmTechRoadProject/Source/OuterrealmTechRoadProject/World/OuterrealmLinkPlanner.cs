using System.Collections.Generic;
using OuterrealmTechRoadProject.UI;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmTechRoadProject.World
{
    /// <summary>
    /// 超维链路世界地图规划器。
    /// 当前设计一次只允许存在一个规划会话，因此使用静态状态保存路线和来源建筑。
    /// </summary>
    public static class OuterrealmLinkPlanner
    {
        /// <summary>
        /// 当前正在规划的世界 tile 路线。
        /// 第一个节点由玩家在世界地图上任意选择，后续节点必须与当前终点相邻。
        /// </summary>
        private static readonly List<PlanetTile> Route = new List<PlanetTile>();

        /// <summary>
        /// 发起规划的建筑。用于判断是否仍处于规划状态，并为以后扩展资源/能量消耗保留入口。
        /// </summary>
        private static Buildings.Building_OuterrealmLinkProjector projector;

        /// <summary>
        /// 当前路线节点数量，供规划控制窗口显示。
        /// </summary>
        public static int RouteNodeCount => Route.Count;

        /// <summary>
        /// 当前路线的道路段数量。
        /// 节点数为 0/1 时没有可建造路段。
        /// </summary>
        public static int RouteSegmentCount => Route.Count > 1 ? Route.Count - 1 : 0;

        /// <summary>
        /// 是否处于超维链路规划状态。
        /// 如果世界目标器已经关闭，说明玩家取消或其他系统结束了目标选择，需要同步清空本地状态。
        /// </summary>
        public static bool IsPlanning
        {
            get
            {
                if (projector != null && !Find.WorldTargeter.IsTargeting)
                {
                    Clear();
                }

                return projector != null;
            }
        }

        /// <summary>
        /// 从建筑打开一次新的世界地图规划。
        /// 建筑只提供命令入口，不强制路线从建筑所在 tile 开始。
        /// </summary>
        public static void BeginPlanning(Buildings.Building_OuterrealmLinkProjector source)
        {
            if (source == null || !source.Spawned)
            {
                return;
            }

            projector = source;
            Route.Clear();

            // 只把视角切到建筑所在世界 tile 方便定位；真正起点由玩家第一次点击决定。
            CameraJumper.TryJump(source.Tile, CameraJumper.MovementMode.Cut);

            // WorldTargeter 负责世界地图鼠标点击、悬浮提示和取消按钮。
            Find.WorldTargeter.BeginTargeting(
                ChoseWorldTarget,
                true,
                OuterrealmLinkTex.IconPlanOuterrealmLink,
                false,
                DrawRoute,
                ExtraLabel,
                CanSelectTarget,
                source.Tile,
                true);

            // 规划期间显示独立完成按钮，避免把“点击世界 tile”同时作为确认动作。
            Find.WindowStack.TryRemove(typeof(Window_OuterrealmLinkPlannerControls));
            Find.WindowStack.Add(new Window_OuterrealmLinkPlannerControls());
        }

        /// <summary>
        /// 主动取消当前规划。
        /// </summary>
        public static void CancelPlanning()
        {
            if (Find.WorldTargeter.IsTargeting)
            {
                Find.WorldTargeter.StopTargeting();
            }

            Clear();
        }

        /// <summary>
        /// 玩家在世界地图点击一个目标时的处理逻辑。
        /// 返回 true 会让 WorldTargeter 结束目标选择；返回 false 表示继续规划。
        /// </summary>
        private static bool ChoseWorldTarget(GlobalTargetInfo target)
        {
            PlanetTile tile = target.Tile;
            if (!IsValidSurfaceTile(tile))
            {
                Messages.Message("OuterrealmTechRoadProject_InvalidWorldTile".Translate(), MessageTypeDefOf.RejectInput);
                return false;
            }

            int existingIndex = IndexOf(tile);
            if (existingIndex >= 0)
            {
                // 点击路线中的旧节点表示回退，类似撤销后续节点；点击当前终点不再确认建造。
                TrimRouteAfter(existingIndex);
                return false;
            }

            if (Route.Count == 0)
            {
                // 路线尚未拥有起点时，第一次有效点击即作为任意起始 tile。
                Route.Add(tile);
                return false;
            }

            PlanetTile lastTile = Route[Route.Count - 1];
            // 世界 RoadLink 只能连接相邻 tile，所以规划阶段就拒绝非相邻选择。
            if (!Find.WorldGrid.IsNeighbor(lastTile, tile))
            {
                Messages.Message("OuterrealmTechRoadProject_MustChooseNeighborTile".Translate(), MessageTypeDefOf.RejectInput);
                return false;
            }

            Route.Add(tile);
            return false;
        }

        /// <summary>
        /// 控制当前鼠标目标是否可以被 WorldTargeter 选中。
        /// 这里比 ChoseWorldTarget 更轻量，只做 UI 反馈所需的合法性判断。
        /// </summary>
        private static bool CanSelectTarget(GlobalTargetInfo target)
        {
            PlanetTile tile = target.Tile;
            if (!IsValidSurfaceTile(tile))
            {
                return false;
            }

            if (IndexOf(tile) >= 0)
            {
                return true;
            }

            if (Route.Count == 0)
            {
                return true;
            }

            return Route.Count > 0 && Find.WorldGrid.IsNeighbor(Route[Route.Count - 1], tile);
        }

        /// <summary>
        /// 鼠标悬浮在世界 tile 上时显示的提示文字。
        /// </summary>
        private static TaggedString ExtraLabel(GlobalTargetInfo target)
        {
            PlanetTile tile = target.Tile;
            if (!IsValidSurfaceTile(tile))
            {
                return "OuterrealmTechRoadProject_InvalidWorldTile".Translate();
            }

            int existingIndex = IndexOf(tile);
            if (existingIndex == Route.Count - 1 && Route.Count > 1)
            {
                return "OuterrealmTechRoadProject_UseFinishButtonToConfirm".Translate(Route.Count - 1);
            }

            if (existingIndex >= 0)
            {
                return "OuterrealmTechRoadProject_ClickRouteNodeToTrim".Translate(existingIndex);
            }

            if (Route.Count == 0)
            {
                return "OuterrealmTechRoadProject_SetOuterrealmLinkStartNode".Translate();
            }

            if (Route.Count > 0 && !Find.WorldGrid.IsNeighbor(Route[Route.Count - 1], tile))
            {
                return "OuterrealmTechRoadProject_MustChooseNeighborTile".Translate();
            }

            return "OuterrealmTechRoadProject_AddOuterrealmLinkNode".Translate(Route.Count);
        }

        /// <summary>
        /// 在世界地图上绘制已选路线。
        /// 这只是规划预览，不会写入任何世界道路数据。
        /// </summary>
        private static void DrawRoute()
        {
            if (Route.Count < 2)
            {
                return;
            }

            WorldGrid grid = Find.WorldGrid;
            for (int i = 0; i < Route.Count - 1; i++)
            {
                // 沿 tile 法线稍微抬高，避免预览线贴在星球表面时被遮挡。
                Vector3 from = grid.GetTileCenter(Route[i]) + grid.GetTileCenter(Route[i]).normalized * 0.05f;
                Vector3 to = grid.GetTileCenter(Route[i + 1]) + grid.GetTileCenter(Route[i + 1]).normalized * 0.05f;
                GenDraw.DrawWorldLineBetween(from, to);
            }
        }

        /// <summary>
        /// 由规划控制窗口触发完成规划。
        /// </summary>
        public static void FinishPlanning()
        {
            if (Route.Count < 2)
            {
                Messages.Message("OuterrealmTechRoadProject_NeedAtLeastOneSegment".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }

            if (Find.WorldTargeter.IsTargeting)
            {
                Find.WorldTargeter.StopTargeting();
            }

            ConfirmRoute();
        }

        /// <summary>
        /// 弹出确认框。确认前复制路线，避免 WorldTargeter 结束后 Clear 清掉待建造路线。
        /// </summary>
        private static void ConfirmRoute()
        {
            List<PlanetTile> routeCopy = new List<PlanetTile>(Route);
            string text = "OuterrealmTechRoadProject_ConfirmOuterrealmLinkText".Translate(routeCopy.Count, routeCopy.Count - 1);
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, delegate
            {
                BuildRoute(routeCopy);
            }));
            Clear();
        }

        /// <summary>
        /// 瞬间建造整条路线。
        /// 当前版本不消耗资源、不分段施工，按路线顺序逐段写入 RoadLink。
        /// </summary>
        private static void BuildRoute(List<PlanetTile> route)
        {
            if (route == null || route.Count < 2)
            {
                return;
            }

            // 每两个相邻节点对应一段世界道路。
            for (int i = 0; i < route.Count - 1; i++)
            {
                if (!OuterrealmLinkUtility.TryOverlayOuterrealmLinkSegment(route[i], route[i + 1]))
                {
                    Messages.Message("OuterrealmTechRoadProject_BuildOuterrealmLinkFailed".Translate(i + 1), MessageTypeDefOf.RejectInput);
                    return;
                }
            }

            Find.LetterStack.ReceiveLetter(
                "OuterrealmTechRoadProject_OuterrealmLinkBuilt".Translate(),
                "OuterrealmTechRoadProject_OuterrealmLinkBuiltText".Translate(route.Count - 1),
                LetterDefOf.PositiveEvent,
                new GlobalTargetInfo(route[route.Count - 1]));
        }

        /// <summary>
        /// 规划目标必须是有效地表 tile。
        /// 超维链路允许任意地形，但不处理非地表星球层。
        /// </summary>
        private static bool IsValidSurfaceTile(PlanetTile tile)
        {
            return tile.Valid && Find.WorldGrid.InBounds(tile) && Find.WorldGrid[tile] is SurfaceTile;
        }

        /// <summary>
        /// 在当前路线中查找 tile。
        /// 路线长度通常很短，使用线性查找可以避免额外集合和同步复杂度。
        /// </summary>
        private static int IndexOf(PlanetTile tile)
        {
            for (int i = 0; i < Route.Count; i++)
            {
                if (Route[i] == tile)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 回退路线到指定节点，删除它之后的所有节点。
        /// </summary>
        private static void TrimRouteAfter(int index)
        {
            int removeCount = Route.Count - index - 1;
            if (removeCount > 0)
            {
                Route.RemoveRange(index + 1, removeCount);
            }
        }

        /// <summary>
        /// 清理当前规划状态。
        /// </summary>
        private static void Clear()
        {
            Find.WindowStack.TryRemove(typeof(Window_OuterrealmLinkPlannerControls));
            projector = null;
            Route.Clear();
        }
    }
}
