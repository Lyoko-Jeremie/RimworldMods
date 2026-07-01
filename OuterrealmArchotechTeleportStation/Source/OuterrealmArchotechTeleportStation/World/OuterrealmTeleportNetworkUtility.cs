using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 传送站网络的集中工具类。
    /// 这里统一处理传送站枚举、目标过滤、远行队传送和新传送站选址，
    /// 避免世界地图右键菜单与建筑 gizmo 各自维护一套规则。
    /// </summary>
    public static class OuterrealmTeleportNetworkUtility
    {
        /// <summary>
        /// 两座传送站之间允许的最小世界地图近似距离，防止手动追加时扎堆。
        /// </summary>
        private const int MinStationDistance = 12;

        /// <summary>
        /// 全局随机选址时每座传送站抽样的候选数量。
        /// 抽样后选取离现有网络最远的合法 tile，避免全图排序带来的开销。
        /// </summary>
        private const int StationCandidateSampleCount = 512;

        /// <summary>
        /// 临时列表只在主线程菜单/命令执行时使用，不跨 Tick 缓存世界对象状态。
        /// 返回给调用者前会复制一份，避免外部持有静态临时列表。
        /// </summary>
        private static readonly List<OuterrealmArchotechTeleportStationWorldObject> TmpStations = new List<OuterrealmArchotechTeleportStationWorldObject>();
        private static readonly List<Building_OuterrealmArchotechTeleportPortal> TmpPortals =
            new List<Building_OuterrealmArchotechTeleportPortal>();

        /// <summary>
        /// 扫描当前世界中所有未销毁的传送站。
        /// 该方法不在 Tick 中调用，按需扫描能避免长期维护缓存带来的同步风险。
        /// </summary>
        public static List<OuterrealmArchotechTeleportStationWorldObject> GetStations()
        {
            TmpStations.Clear();
            List<WorldObject> objects = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is OuterrealmArchotechTeleportStationWorldObject station && !station.Destroyed)
                {
                    TmpStations.Add(station);
                }
            }

            // 固定排序让菜单顺序稳定，减少每次打开菜单时选项跳动。
            TmpStations.SortBy(station => station.Tile.tileId);
            return new List<OuterrealmArchotechTeleportStationWorldObject>(TmpStations);
        }

        /// <summary>
        /// 获取从 origin 可传送到的其他传送站。
        /// 结果会排除起点、无效 tile 和已销毁对象；如果有起点，则按距离从近到远排序。
        /// </summary>
        public static List<OuterrealmArchotechTeleportStationWorldObject> GetDestinationStations(
            OuterrealmArchotechTeleportStationWorldObject origin)
        {
            List<OuterrealmArchotechTeleportStationWorldObject> stations = GetStations();
            stations.RemoveAll(station => station == origin || station.Destroyed || !station.Tile.Valid);
            if (origin != null && origin.Tile.Valid)
            {
                stations.SortBy(station => Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, station.Tile));
            }

            return stations;
        }

        /// <summary>
        /// 获取当前可用的传送网络目的地。
        /// 包含世界地图传送站和玩家基地内已生成、已启用的传送门。
        /// </summary>
        public static List<OuterrealmTeleportDestination> GetDestinations(
            OuterrealmArchotechTeleportStationWorldObject originStation,
            Building_OuterrealmArchotechTeleportPortal originPortal = null)
        {
            List<OuterrealmTeleportDestination> destinations = new List<OuterrealmTeleportDestination>();

            List<OuterrealmArchotechTeleportStationWorldObject> stations = GetStations();
            for (int i = 0; i < stations.Count; i++)
            {
                OuterrealmArchotechTeleportStationWorldObject station = stations[i];
                if (station == originStation || station.Destroyed || !station.Tile.Valid)
                {
                    continue;
                }

                destinations.Add(OuterrealmTeleportDestination.ForStation(station));
            }

            AppendPlayerMapPortals(TmpPortals);
            for (int i = 0; i < TmpPortals.Count; i++)
            {
                Building_OuterrealmArchotechTeleportPortal portal = TmpPortals[i];
                if (portal == originPortal)
                {
                    continue;
                }

                destinations.Add(OuterrealmTeleportDestination.ForPortal(portal));
            }

            TmpPortals.Clear();
            PlanetTile originTile = GetOriginTile(originStation, originPortal);
            if (originTile.Valid)
            {
                destinations.SortBy(destination => DistanceFrom(originTile, destination.Tile), destination => destination.LabelCap.ToString());
            }
            else
            {
                destinations.SortBy(destination => destination.Tile.Valid ? destination.Tile.tileId : int.MaxValue, destination => destination.LabelCap.ToString());
            }

            return destinations;
        }

        /// <summary>
        /// 判断远行队是否位于传送站本格或相邻一格内。
        /// 世界地图地标 tile 可能无法直接停留，因此相邻 tile 也允许使用传送站网络。
        /// </summary>
        public static bool CaravanInStationRange(Caravan caravan, OuterrealmArchotechTeleportStationWorldObject station)
        {
            if (caravan == null || station == null || !caravan.Tile.Valid || !station.Tile.Valid)
            {
                return false;
            }

            if (caravan.Tile == station.Tile)
            {
                return true;
            }

            if (caravan.Tile.Layer != station.Tile.Layer)
            {
                return false;
            }

            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(station.Tile, neighbors);
            return neighbors.Contains(caravan.Tile);
        }

        /// <summary>
        /// 从指定起点传送远行队到目标传送站。
        /// 此重载用于世界地图右键菜单，会额外验证远行队仍然位于起点或相邻 tile。
        /// </summary>
        public static void TeleportCaravan(
            Caravan caravan,
            OuterrealmArchotechTeleportStationWorldObject origin,
            OuterrealmArchotechTeleportStationWorldObject destination)
        {
            // 菜单打开到玩家点击之间，世界对象可能被其他系统销毁或改变，所以执行前必须重新校验。
            if (caravan == null || destination == null || destination.Destroyed || !destination.Tile.Valid)
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (origin != null && !CaravanInStationRange(caravan, origin))
            {
                Messages.Message("OATS_CannotTeleportNotAtStation".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            TeleportCaravan(caravan, destination);
        }

        /// <summary>
        /// 从世界地图起点传送远行队到任意网络目的地。
        /// </summary>
        public static void TeleportCaravan(
            Caravan caravan,
            OuterrealmArchotechTeleportStationWorldObject origin,
            OuterrealmTeleportDestination destination)
        {
            if (caravan == null || destination == null || !destination.IsValid())
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (origin != null && !CaravanInStationRange(caravan, origin))
            {
                Messages.Message("OATS_CannotTeleportNotAtStation".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            destination.TryTeleportCaravan(caravan);
        }

        /// <summary>
        /// 执行实际的世界地图瞬移。
        /// 参考原版 Farskip 的做法：停止当前路径、直接改 tile，并通知远行队刷新路径和显示缓存。
        /// </summary>
        public static void TeleportCaravan(Caravan caravan, OuterrealmArchotechTeleportStationWorldObject destination)
        {
            if (caravan == null || destination == null || destination.Destroyed || !destination.Tile.Valid)
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            // StopDead 先清空已有路径，避免 teleport 后仍沿旧路径继续移动。
            caravan.pather.StopDead();
            caravan.Tile = destination.Tile;

            // Notify_Teleported 会重置 tweener 与 pather 的内部传送状态，是直接改 Tile 后必要的收尾。
            caravan.Notify_Teleported();
            Messages.Message(
                "OATS_MessageCaravanTeleported".Translate(caravan.Name, destination.LabelCap),
                new LookTargets(caravan, destination),
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 将远行队投送到任意可形成远行队的世界 tile。
        /// 不要求目标 tile 存在传送站或其他世界对象。
        /// </summary>
        public static void TeleportCaravan(Caravan caravan, PlanetTile destinationTile)
        {
            if (caravan == null)
            {
                Messages.Message("OATS_CannotTeleportInvalidDestination".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!CanTeleportToWorldTile(destinationTile, out TaggedString reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }

            // 与传送站间瞬移一致，先停止旧路径再改写 tile。
            caravan.pather.StopDead();
            caravan.Tile = destinationTile;
            caravan.Notify_Teleported();

            TaggedString label = "OATS_WorldTileDestinationLabel".Translate(destinationTile.ToString());
            Messages.Message(
                "OATS_MessageCaravanTeleported".Translate(caravan.Name, label),
                new LookTargets((GlobalTargetInfo)caravan, new GlobalTargetInfo(destinationTile)),
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 判断指定世界 tile 是否能作为远行队投送落点。
        /// 这里不排除已有世界对象，因为玩家可以把远行队投送到据点、任务点或其他可通行地格外侧。
        /// </summary>
        public static bool CanTeleportToWorldTile(PlanetTile tile, out TaggedString reason)
        {
            if (!tile.Valid || !Find.WorldGrid.InBounds(tile))
            {
                reason = "OATS_CannotTeleportInvalidWorldTile".Translate();
                return false;
            }

            if (Find.World.Impassable(tile) || !tile.LayerDef.canFormCaravans)
            {
                reason = "OATS_CannotTeleportInvalidWorldTile".Translate();
                return false;
            }

            reason = TaggedString.Empty;
            return true;
        }

        /// <summary>
        /// 为随机追加传送站在整个世界范围内寻找一个合法 tile。
        /// </summary>
        public static bool TryFindNewStationTile(out PlanetTile tile, bool ignoreStationCountLimit = false)
        {
            StationPlacementContext context = new StationPlacementContext();
            return TryFindBestGlobalStationTile(
                context,
                out tile,
                ignoreStationCountLimit,
                ignoreStationDistanceLimit: false);
        }

        /// <summary>
        /// 根据世界覆盖率确定新世界初始传送站数量。
        /// </summary>
        public static int InitialStationTargetCount()
        {
            float coverage = Find.World?.PlanetCoverage ?? 0.3f;
            if (coverage < 0.2f)
            {
                return 6;
            }

            if (coverage < 0.4f)
            {
                return 10;
            }

            return coverage < 0.75f ? 16 : 32;
        }

        /// <summary>
        /// 新世界初始化时按覆盖率批量生成传送站。
        /// </summary>
        public static int AddInitialStationsForNewWorld()
        {
            return TryAddRandomStations(
                InitialStationTargetCount(),
                out _,
                sendMessage: false,
                ignoreStationCountLimit: true);
        }

        /// <summary>
        /// 在整个世界范围内批量随机追加传送站。
        /// </summary>
        public static int TryAddRandomStations(
            int count,
            out TaggedString reason,
            bool sendMessage = true,
            bool ignoreStationCountLimit = false)
        {
            reason = TaggedString.Empty;
            if (count <= 0)
            {
                return 0;
            }

            StationPlacementContext context = new StationPlacementContext();
            int added = 0;
            OuterrealmArchotechTeleportStationWorldObject lastStation = null;
            for (int i = 0; i < count; i++)
            {
                if (!TryFindBestGlobalStationTile(
                        context,
                        out PlanetTile tile,
                        ignoreStationCountLimit,
                        ignoreStationDistanceLimit: false))
                {
                    reason = "OATS_CannotAddTeleportStationHere".Translate();
                    break;
                }

                lastStation = AddStationAtUnchecked(tile);
                context.RegisterStation(tile);
                added++;
            }

            if (sendMessage && added > 0)
            {
                if (added == 1 && lastStation != null)
                {
                    Messages.Message(
                        "OATS_MessageTeleportStationAdded".Translate(lastStation.LabelCap),
                        new LookTargets(lastStation),
                        MessageTypeDefOf.PositiveEvent);
                }
                else
                {
                    Messages.Message(
                        "OATS_MessageTeleportStationsAdded".Translate(added),
                        MessageTypeDefOf.PositiveEvent,
                        false);
                }
            }

            return added;
        }

        /// <summary>
        /// 在指定 tile 创建新的传送站世界对象。
        /// 所有入口（初始生成、随机追加、指定追加）都通过此方法创建，保证校验规则一致。
        /// </summary>
        public static bool TryAddStationAt(
            PlanetTile tile,
            out OuterrealmArchotechTeleportStationWorldObject station,
            out TaggedString reason,
            bool sendMessage = true,
            bool ignoreStationCountLimit = false,
            bool ignoreStationDistanceLimit = false)
        {
            station = null;
            StationPlacementContext context = new StationPlacementContext();
            if (!CanPlaceStationAt(tile, context, out reason, ignoreStationCountLimit, ignoreStationDistanceLimit))
            {
                return false;
            }

            station = AddStationAtUnchecked(tile);
            if (sendMessage)
            {
                Messages.Message(
                    "OATS_MessageTeleportStationAdded".Translate(station.LabelCap),
                    new LookTargets(station),
                    MessageTypeDefOf.PositiveEvent);
            }

            return true;
        }

        /// <summary>
        /// 判断指定世界 tile 是否可以放置传送站。
        /// 这里是随机追加和玩家手动选点共用的唯一规则入口。
        /// </summary>
        public static bool CanPlaceStationAt(
            PlanetTile tile,
            out TaggedString reason,
            bool ignoreStationCountLimit = false,
            bool ignoreStationDistanceLimit = false)
        {
            return CanPlaceStationAt(
                tile,
                new StationPlacementContext(),
                out reason,
                ignoreStationCountLimit,
                ignoreStationDistanceLimit);
        }

        /// <summary>
        /// 判断指定世界 tile 是否可以放置传送站。
        /// 批量生成时通过 context 复用已有扫描结果，避免每个候选 tile 都重新扫描 WorldObjects。
        /// </summary>
        private static bool CanPlaceStationAt(
            PlanetTile tile,
            StationPlacementContext context,
            out TaggedString reason,
            bool ignoreStationCountLimit = false,
            bool ignoreStationDistanceLimit = false)
        {
            if (!tile.Valid)
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            if (!Find.WorldGrid.InBounds(tile))
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            if (!ignoreStationCountLimit && context.StationTiles.Count >= MaxStationCount())
            {
                reason = "OATS_CannotAddTeleportStationMaxCount".Translate(MaxStationCount());
                return false;
            }

            if (Find.World.Impassable(tile))
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            Tile tileInfo = Find.WorldGrid[tile];
            if (tileInfo?.PrimaryBiome == null || !tileInfo.PrimaryBiome.canBuildBase)
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            // 不允许放在已有世界对象的 tile 上，避免和定居点、任务地点、其他传送站重叠。
            if (context.OccupiedTiles.Contains(tile))
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            if (!ignoreStationDistanceLimit)
            {
                // 控制自动追加的网络空间分布；手动选点允许玩家自行决定距离。
                List<PlanetTile> stationTiles = context.StationTiles;
                for (int i = 0; i < stationTiles.Count; i++)
                {
                    if (stationTiles[i].Layer == tile.Layer &&
                        Find.WorldGrid.ApproxDistanceInTiles(stationTiles[i], tile) < MinStationDistance)
                    {
                        reason = "OATS_CannotAddTeleportStationHere".Translate();
                        return false;
                    }
                }
            }

            reason = TaggedString.Empty;
            return true;
        }

        /// <summary>
        /// 根据世界大小估算传送站数量上限。
        /// 使用 tile 总数粗略缩放，保证小世界不会刷太多，大世界仍有足够网络节点。
        /// </summary>
        public static int MaxStationCount()
        {
            return InitialStationTargetCount();
        }

        private static bool TryFindBestGlobalStationTile(
            StationPlacementContext context,
            out PlanetTile tile,
            bool ignoreStationCountLimit,
            bool ignoreStationDistanceLimit)
        {
            PlanetTile bestTile = PlanetTile.Invalid;
            int tileCount = Find.WorldGrid.TilesCount;
            float bestScore = -1f;

            for (int i = 0; i < StationCandidateSampleCount; i++)
            {
                PlanetTile candidate = new PlanetTile(Rand.Range(0, tileCount));
                TryUseCandidate(candidate);
            }

            if (bestTile.Valid)
            {
                tile = bestTile;
                return true;
            }

            int startTile = Rand.Range(0, tileCount);
            for (int i = 0; i < tileCount; i++)
            {
                PlanetTile candidate = new PlanetTile((startTile + i) % tileCount);
                if (TryUseCandidate(candidate))
                {
                    tile = bestTile;
                    return true;
                }
            }

            tile = PlanetTile.Invalid;
            return false;

            bool TryUseCandidate(PlanetTile candidate)
            {
                if (!CanPlaceStationAt(
                        candidate,
                        context,
                        out _,
                        ignoreStationCountLimit,
                        ignoreStationDistanceLimit))
                {
                    return false;
                }

                float score = DistanceToNearestStation(candidate, context);
                if (!bestTile.Valid || score > bestScore || Mathf.Approximately(score, bestScore) && Rand.Chance(0.5f))
                {
                    bestTile = candidate;
                    bestScore = score;
                }

                return true;
            }
        }

        private static float DistanceToNearestStation(PlanetTile tile, StationPlacementContext context)
        {
            List<PlanetTile> stationTiles = context.StationTiles;
            if (stationTiles.Count == 0)
            {
                return Rand.Value;
            }

            float bestDistance = float.MaxValue;
            for (int i = 0; i < stationTiles.Count; i++)
            {
                if (stationTiles[i].Layer != tile.Layer)
                {
                    continue;
                }

                float distance = Find.WorldGrid.ApproxDistanceInTiles(stationTiles[i], tile);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                }
            }

            return bestDistance;
        }

        private static OuterrealmArchotechTeleportStationWorldObject AddStationAtUnchecked(PlanetTile tile)
        {
            // WorldObjectMaker 会按 WorldObjectDef.worldObjectClass 实例化自定义 MapParent。
            OuterrealmArchotechTeleportStationWorldObject station =
                (OuterrealmArchotechTeleportStationWorldObject)WorldObjectMaker.MakeWorldObject(
                    OuterrealmDefOf.OuterrealmArchotechTeleportStation);
            station.Tile = tile;
            Find.WorldObjects.Add(station);
            return station;
        }

        private sealed class StationPlacementContext
        {
            public readonly List<PlanetTile> StationTiles = new List<PlanetTile>();
            public readonly HashSet<PlanetTile> OccupiedTiles = new HashSet<PlanetTile>();

            public StationPlacementContext()
            {
                List<WorldObject> objects = Find.WorldObjects.AllWorldObjects;
                for (int i = 0; i < objects.Count; i++)
                {
                    WorldObject worldObject = objects[i];
                    if (worldObject.Destroyed || !worldObject.Tile.Valid)
                    {
                        continue;
                    }

                    OccupiedTiles.Add(worldObject.Tile);
                    if (worldObject is OuterrealmArchotechTeleportStationWorldObject)
                    {
                        StationTiles.Add(worldObject.Tile);
                    }
                }
            }

            public void RegisterStation(PlanetTile tile)
            {
                StationTiles.Add(tile);
                OccupiedTiles.Add(tile);
            }
        }

        private static void AppendPlayerMapPortals(List<Building_OuterrealmArchotechTeleportPortal> result)
        {
            result.Clear();
            List<Map> maps = Current.Game.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                MapComponent_OuterrealmTeleportPortalTracker tracker =
                    maps[i].GetComponent<MapComponent_OuterrealmTeleportPortalTracker>();
                if (tracker != null)
                {
                    tracker.AppendActiveDestinations(result);
                }
            }
        }

        private static PlanetTile GetOriginTile(
            OuterrealmArchotechTeleportStationWorldObject originStation,
            Building_OuterrealmArchotechTeleportPortal originPortal)
        {
            if (originStation != null && originStation.Tile.Valid)
            {
                return originStation.Tile;
            }

            return originPortal?.Map?.Tile ?? PlanetTile.Invalid;
        }

        private static float DistanceFrom(PlanetTile originTile, PlanetTile destinationTile)
        {
            if (!destinationTile.Valid || originTile.Layer != destinationTile.Layer)
            {
                return float.MaxValue;
            }

            return Find.WorldGrid.ApproxDistanceInTiles(originTile, destinationTile);
        }
    }
}
