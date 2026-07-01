using System.Collections.Generic;
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
        /// 建筑生成后登记到地图追踪器，读档重建建筑时也会执行。
        /// </summary>
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<MapComponent_OuterrealmTeleportPortalTracker>()?.Register(this);
        }

        /// <summary>
        /// 拆除、卸载和重装建筑前从地图追踪器移除。
        /// </summary>
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            map?.GetComponent<MapComponent_OuterrealmTeleportPortalTracker>()?.Unregister(this);
            base.DeSpawn(mode);
        }

        /// <summary>
        /// 判断该传送门是否应作为网络目的地出现。
        /// 当前只允许玩家基地地图中的玩家传送门；未来的未启用状态可在 IsTeleportEndpointEnabled 中扩展。
        /// </summary>
        public virtual bool CanUseAsTeleportDestination(out TaggedString reason)
        {
            if (Destroyed || !Spawned || Map == null || Map.Parent is OuterrealmArchotechTeleportStationWorldObject)
            {
                reason = "OATS_CannotTeleportInvalidDestination".Translate();
                return false;
            }

            if (!Map.IsPlayerHome || Faction != Faction.OfPlayer)
            {
                reason = "OATS_CannotTeleportInvalidDestination".Translate();
                return false;
            }

            if (!IsTeleportEndpointEnabled)
            {
                reason = "OATS_CannotTeleportPortalInactive".Translate();
                return false;
            }

            reason = TaggedString.Empty;
            return true;
        }

        /// <summary>
        /// 后续如果加入未启用、断电或冷却状态，可覆盖此属性而不改变追踪和菜单代码。
        /// </summary>
        protected virtual bool IsTeleportEndpointEnabled => true;

        /// <summary>
        /// 在建筑默认 gizmo 后追加本 Mod 的传送和追加传送站命令。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            // 地图内传送：打开目的地菜单，之后按当前地图类型决定全图传送或弹出选择窗口。
            yield return new Command_Action
            {
                defaultLabel = "OATS_SelectTeleportDestination".Translate(),
                defaultDesc = "OATS_CommandTeleportToStationDesc".Translate(),
                icon = OuterrealmTeleportStationTex.Teleport2Site,
                action = ShowTeleportDestinationMenu
            };

            // 世界投送：不要求目标地格存在传送站，直接在目标 tile 外侧形成远行队。
            yield return new Command_Action
            {
                defaultLabel = "OATS_CommandTeleportToWorldTile".Translate(),
                defaultDesc = "OATS_CommandTeleportToWorldTileDesc".Translate(),
                icon = OuterrealmTeleportStationTex.Teleport2Tile,
                action = BeginSelectWorldTeleportTile
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
            List<OuterrealmTeleportDestination> destinations =
                OuterrealmTeleportNetworkUtility.GetDestinations(origin, this);

            if (destinations.Count == 0)
            {
                Messages.Message("OATS_CannotTeleportNoDestinations".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (OuterrealmTeleportDestination destination in destinations)
            {
                // 复制循环变量，避免闭包捕获导致所有菜单项指向同一个目的地。
                OuterrealmTeleportDestination localDestination = destination;
                options.Add(new FloatMenuOption(localDestination.GetMenuLabel(Map.Tile), () => TeleportMapContents(localDestination)));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>
        /// 传送站地图会自动传送全部玩家内容并关闭来源图；普通地图需要玩家先选择内容。
        /// </summary>
        private void TeleportMapContents(OuterrealmTeleportDestination destination)
        {
            if (Map == null)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (Map.Parent is OuterrealmArchotechTeleportStationWorldObject)
            {
                List<TransferableOneWay> transferables = OuterrealmMapTeleportUtility.CreateTransferables(Map, selectAll: true);
                OuterrealmMapTeleportUtility.TryTeleportTransferables(
                    Map,
                    destination,
                    transferables,
                    removeSourceMap: true);
                return;
            }

            Find.WindowStack.Add(new Dialog_OuterrealmTeleportContents(Map, destination));
        }

        /// <summary>
        /// 进入世界地图选点流程，选择任意可通行 tile 作为远行队投送落点。
        /// </summary>
        private void BeginSelectWorldTeleportTile()
        {
            if (Map == null)
            {
                Messages.Message("OATS_CannotTeleportNoMapPawns".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            CameraJumper.TryJump(new GlobalTargetInfo(Map.Tile));
            Find.WorldSelector.ClearSelection();
            Find.TilePicker.StartTargeting_NewTemp(
                validator: tile =>
                {
                    if (OuterrealmTeleportNetworkUtility.CanTeleportToWorldTile(tile, out TaggedString reason))
                    {
                        return true;
                    }

                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    return false;
                },
                tileChosen: tile =>
                {
                    Find.World.renderer.wantedMode = WorldRenderMode.None;
                    TeleportMapContents(OuterrealmTeleportDestination.ForWorldTile(tile));
                },
                onGuiAction: DrawWorldTeleportTileMouseAttachment,
                title: "OATS_SelectWorldTeleportTile".Translate(),
                showRandomButton: false,
                selectTileBehindObject: true,
                hideFormCaravanGizmo: true,
                canCancel: true,
                noTileChosenMessage: "OATS_SelectWorldTeleportTile".Translate());
        }

        /// <summary>
        /// 打开追加传送站方式选择菜单。
        /// 随机追加和指定追加最终都会走 OuterrealmTeleportNetworkUtility.TryAddStationAt。
        /// </summary>
        private void ShowAddStationMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption("OATS_AddTeleportStationRandom".Translate(), () => AddRandomStations(1)),
                new FloatMenuOption("OATS_AddTeleportStationRandom5".Translate(), () => AddRandomStations(5)),
                new FloatMenuOption("OATS_AddTeleportStationRandom10".Translate(), () => AddRandomStations(10)),
                new FloatMenuOption("OATS_AddTeleportStationSelectTile".Translate(), BeginSelectStationTile)
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>
        /// 自动寻找合法 tile 并创建指定数量的新传送站。
        /// </summary>
        private void AddRandomStations(int count)
        {
            if (OuterrealmTeleportNetworkUtility.TryAddRandomStations(
                    count,
                    out TaggedString reason,
                    ignoreStationCountLimit: true) == 0)
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
                    if (OuterrealmTeleportNetworkUtility.CanPlaceStationAt(
                            tile,
                            out TaggedString reason,
                            ignoreStationCountLimit: true,
                            ignoreStationDistanceLimit: true))
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
                    if (!OuterrealmTeleportNetworkUtility.TryAddStationAt(
                            tile,
                            out _,
                            out TaggedString reason,
                            ignoreStationCountLimit: true,
                            ignoreStationDistanceLimit: true))
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

        /// <summary>
        /// 投送选点模式下跟随鼠标绘制传送图标，和追加传送站模式区分。
        /// </summary>
        private static void DrawWorldTeleportTileMouseAttachment()
        {
            Vector2 mousePosition = Event.current.mousePosition;
            GUI.DrawTexture(new Rect(mousePosition.x + 8f, mousePosition.y + 8f, 32f, 32f), OuterrealmTeleportStationTex.Teleport2Tile);
        }
    }
}
