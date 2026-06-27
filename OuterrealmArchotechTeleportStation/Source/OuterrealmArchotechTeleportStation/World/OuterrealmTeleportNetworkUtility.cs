using System.Collections.Generic;
using System.Linq;
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
        /// 使用原版随机地点查找时，优先在玩家附近这个距离范围内寻找候选 tile。
        /// </summary>
        private const int RandomSiteMinDistance = 10;
        private const int RandomSiteMaxDistance = 80;

        /// <summary>
        /// 临时列表只在主线程菜单/命令执行时使用，不跨 Tick 缓存世界对象状态。
        /// 返回给调用者前会复制一份，避免外部持有静态临时列表。
        /// </summary>
        private static readonly List<OuterrealmArchotechTeleportStationWorldObject> TmpStations = new List<OuterrealmArchotechTeleportStationWorldObject>();

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
            return TmpStations.ToList();
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
        /// 为随机追加传送站寻找一个合法 tile。
        /// 优先使用原版 TileFinder 的站点查找逻辑；如果当前世界还没有玩家 tile 或原版查找失败，
        /// 再使用本 Mod 的保底随机/线性扫描。
        /// </summary>
        public static bool TryFindNewStationTile(out PlanetTile tile)
        {
            PlanetTile nearTile;
            bool hasPlayerTile = TileFinder.TryFindRandomPlayerTile(out nearTile, allowCaravans: true);
            if (hasPlayerTile && TileFinder.TryFindNewSiteTile(
                    out tile,
                    nearTile,
                    RandomSiteMinDistance,
                    RandomSiteMaxDistance,
                    allowCaravans: true,
                    validator: candidate => CanPlaceStationAt(candidate, out _)))
            {
                return true;
            }

            return TileFinder.TryFindNewSiteTile(
                out tile,
                RandomSiteMinDistance,
                RandomSiteMaxDistance,
                allowCaravans: true,
                validator: candidate => CanPlaceStationAt(candidate, out _)) ||
                TryFindRandomValidTile(out tile);
        }

        /// <summary>
        /// 原版随机地点查找失败时的保底方案。
        /// 先随机抽样减少平均耗时；抽样失败后再线性扫描，尽量保证新世界至少能生成一个入口。
        /// </summary>
        private static bool TryFindRandomValidTile(out PlanetTile tile)
        {
            int tileCount = Find.WorldGrid.TilesCount;
            for (int i = 0; i < 2000; i++)
            {
                PlanetTile candidate = new PlanetTile(Rand.Range(0, tileCount));
                if (CanPlaceStationAt(candidate, out _))
                {
                    tile = candidate;
                    return true;
                }
            }

            for (int i = 0; i < tileCount; i++)
            {
                PlanetTile candidate = new PlanetTile(i);
                if (CanPlaceStationAt(candidate, out _))
                {
                    tile = candidate;
                    return true;
                }
            }

            tile = PlanetTile.Invalid;
            return false;
        }

        /// <summary>
        /// 在指定 tile 创建新的传送站世界对象。
        /// 所有入口（初始生成、随机追加、指定追加）都通过此方法创建，保证校验规则一致。
        /// </summary>
        public static bool TryAddStationAt(
            PlanetTile tile,
            out OuterrealmArchotechTeleportStationWorldObject station,
            out TaggedString reason,
            bool sendMessage = true)
        {
            station = null;
            if (!CanPlaceStationAt(tile, out reason))
            {
                return false;
            }

            // WorldObjectMaker 会按 WorldObjectDef.worldObjectClass 实例化自定义 MapParent。
            station = (OuterrealmArchotechTeleportStationWorldObject)WorldObjectMaker.MakeWorldObject(
                OuterrealmDefOf.OuterrealmArchotechTeleportStation);
            station.Tile = tile;
            Find.WorldObjects.Add(station);
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
        public static bool CanPlaceStationAt(PlanetTile tile, out TaggedString reason)
        {
            if (!tile.Valid)
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            if (GetStations().Count >= MaxStationCount())
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
            if (Find.WorldObjects.AnyWorldObjectAt(tile))
            {
                reason = "OATS_CannotAddTeleportStationHere".Translate();
                return false;
            }

            // 控制传送网络空间分布，避免玩家连续追加时全都贴在同一区域。
            List<OuterrealmArchotechTeleportStationWorldObject> stations = GetStations();
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i].Tile.Layer == tile.Layer &&
                    Find.WorldGrid.ApproxDistanceInTiles(stations[i].Tile, tile) < MinStationDistance)
                {
                    reason = "OATS_CannotAddTeleportStationHere".Translate();
                    return false;
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
            int tileCount = Find.WorldGrid.TilesCount;
            return Mathf.Clamp(Mathf.RoundToInt(tileCount / 1200f), 6, 24);
        }
    }
}
