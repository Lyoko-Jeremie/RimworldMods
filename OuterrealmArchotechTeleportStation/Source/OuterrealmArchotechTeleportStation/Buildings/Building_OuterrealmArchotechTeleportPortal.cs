using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 传送站局部地图内的主交互建筑。
    /// 玩家选中该建筑后，可以把当前地图上的玩家单位传送到其他传送站，
    /// 也可以通过它向世界地图追加新的传送站节点。
    /// </summary>
    public class Building_OuterrealmArchotechTeleportPortal : Building
    {
        /// <summary>
        /// 在建筑默认 gizmo 后追加本 Mod 的传送和追加传送站命令。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            // 地图内传送：打开目的地菜单，选择后把当前地图玩家单位组成远行队并瞬移。
            yield return new Command_Action
            {
                defaultLabel = "OATS_SelectTeleportDestination".Translate(),
                defaultDesc = "OATS_CommandTeleportToStationDesc".Translate(),
                icon = OuterrealmTeleportStationTex.Teleport,
                action = ShowTeleportDestinationMenu
            };

            // 网络扩展：允许玩家随机或指定世界 tile 激活新的传送站。
            yield return new Command_Action
            {
                defaultLabel = "OATS_CommandAddTeleportStation".Translate(),
                defaultDesc = "OATS_CommandAddTeleportStationDesc".Translate(),
                icon = OuterrealmTeleportStationTex.AddStation,
                action = ShowAddStationMenu
            };
        }

        /// <summary>
        /// 打开目标传送站选择菜单。
        /// 当前建筑所在地图的父对象应当是传送站世界对象；如果不是，也允许工具类退化为列出所有其他站点。
        /// </summary>
        private void ShowTeleportDestinationMenu()
        {
            OuterrealmArchotechTeleportStationWorldObject origin = Map?.Parent as OuterrealmArchotechTeleportStationWorldObject;
            List<OuterrealmArchotechTeleportStationWorldObject> destinations =
                OuterrealmTeleportNetworkUtility.GetDestinationStations(origin);

            if (destinations.Count == 0)
            {
                Messages.Message("OATS_CannotTeleportNoDestinations".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (OuterrealmArchotechTeleportStationWorldObject destination in destinations)
            {
                // 复制循环变量，避免闭包捕获导致所有菜单项指向同一个目的地。
                OuterrealmArchotechTeleportStationWorldObject localDestination = destination;
                options.Add(new FloatMenuOption(localDestination.LabelCap, () => TeleportMapPawns(localDestination)));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>
        /// 将当前传送站局部地图中的玩家派系单位组成远行队，并传送到目标传送站所在 tile。
        /// 第一版不直接把单位生成到目标局部地图，避免目标地图未加载时产生额外地图生成和落点问题。
        /// </summary>
        private void TeleportMapPawns(OuterrealmArchotechTeleportStationWorldObject destination)
        {
            // 只传送当前地图内仍然存活、未倒地的玩家派系单位，避免把敌人、尸体或不可控单位卷入传送。
            List<Pawn> pawns = Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                .Where(pawn => !pawn.Downed && !pawn.Dead)
                .ToList();

            if (pawns.Count == 0)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            PlanetTile originTile = Map.Tile;

            // 先使用原版离图工具把 Pawn 从 Map 转为 Caravan。
            // destinationTile 传 Invalid，避免原版工具自动按普通路径下达移动命令；之后由本 Mod 统一瞬移。
            Caravan caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(
                pawns,
                Faction.OfPlayer,
                originTile,
                originTile,
                PlanetTile.Invalid,
                false);

            if (caravan == null)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            // 远行队创建成功后复用世界传送逻辑，统一处理 StopDead、Tile、Notify_Teleported 和消息。
            OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, destination);
            Messages.Message(
                "OATS_MessagePawnsTeleportedFromMap".Translate(destination.LabelCap),
                new LookTargets(caravan, destination),
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 打开追加传送站方式选择菜单。
        /// 随机追加和指定追加最终都会走 OuterrealmTeleportNetworkUtility.TryAddStationAt。
        /// </summary>
        private void ShowAddStationMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption("OATS_AddTeleportStationRandom".Translate(), AddRandomStation),
                new FloatMenuOption("OATS_AddTeleportStationSelectTile".Translate(), BeginSelectStationTile)
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>
        /// 自动寻找合法 tile 并创建新的传送站。
        /// </summary>
        private void AddRandomStation()
        {
            if (!OuterrealmTeleportNetworkUtility.TryFindNewStationTile(out PlanetTile tile))
            {
                Messages.Message("OATS_CannotAddTeleportStationHere".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!OuterrealmTeleportNetworkUtility.TryAddStationAt(tile, out _, out TaggedString reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
            }
        }

        /// <summary>
        /// 进入世界地图选点流程，让玩家手动指定新传送站位置。
        /// 使用原版 TilePicker 可以获得和迁城/飞船降落类似的世界地图交互。
        /// </summary>
        private void BeginSelectStationTile()
        {
            CameraJumper.TryJump(new GlobalTargetInfo(Map.Tile));
            Find.WorldSelector.ClearSelection();
            Find.TilePicker.StartTargeting_NewTemp(
                validator: tile =>
                {
                    // TilePicker 的 validator 会在点击“下一步”时执行；
                    // 非法时直接显示统一选址规则返回的 i18n 原因。
                    if (OuterrealmTeleportNetworkUtility.CanPlaceStationAt(tile, out TaggedString reason))
                    {
                        return true;
                    }

                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    return false;
                },
                tileChosen: tile =>
                {
                    // 选定后关闭世界渲染高亮模式，再通过统一创建入口实际添加世界对象。
                    Find.World.renderer.wantedMode = WorldRenderMode.None;
                    if (!OuterrealmTeleportNetworkUtility.TryAddStationAt(tile, out _, out TaggedString reason))
                    {
                        Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    }
                },
                onGuiAction: DrawSelectTileMouseAttachment,
                title: "OATS_SelectTeleportStationTile".Translate(),
                showRandomButton: false,
                selectTileBehindObject: true,
                hideFormCaravanGizmo: true,
                canCancel: true,
                noTileChosenMessage: "OATS_SelectTeleportStationTile".Translate());
        }

        /// <summary>
        /// 指定 tile 模式下跟随鼠标绘制命令图标，给玩家明确的选点状态反馈。
        /// </summary>
        private static void DrawSelectTileMouseAttachment()
        {
            Vector2 mousePosition = Event.current.mousePosition;
            GUI.DrawTexture(new Rect(mousePosition.x + 8f, mousePosition.y + 8f, 32f, 32f), OuterrealmTeleportStationTex.AddStation);
        }
    }
}
