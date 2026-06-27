using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 世界地图上的超维科技远古传送站。
    /// 继承 MapParent 是为了让它既能作为世界地标被远行队右键交互，又能拥有一张小型局部地图。
    /// </summary>
    public class OuterrealmArchotechTeleportStationWorldObject : MapParent
    {
        /// <summary>
        /// 为右键点击该地标的玩家远行队追加传送选项。
        /// 保留 base 选项是为了继续使用原版“进入地图”等 MapParent 交互。
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(caravan))
            {
                yield return option;
            }

            // 原版 FloatMenuMakerWorld 理论上只会传入玩家远行队，但这里保留防御式检查，
            // 以兼容其他 Mod 直接调用 GetFloatMenuOptions 的情况。
            if (caravan == null || !caravan.IsPlayerControlled)
            {
                yield break;
            }

            // 第一版要求远行队已经抵达当前传送站 tile，避免右键远处传送站时绕过旅行过程。
            if (caravan.Tile != Tile)
            {
                yield return new FloatMenuOption("OATS_CannotTeleportNotAtStation".Translate(), null);
                yield break;
            }

            // 目标只列出“其他”传送站，且由工具类统一排序和过滤。
            List<OuterrealmArchotechTeleportStationWorldObject> destinations =
                OuterrealmTeleportNetworkUtility.GetDestinationStations(this);
            if (destinations.Count == 0)
            {
                yield return new FloatMenuOption("OATS_CannotTeleportNoDestinations".Translate(), null);
                yield break;
            }

            foreach (OuterrealmArchotechTeleportStationWorldObject destination in destinations)
            {
                // yield + 闭包需要复制局部变量，避免所有菜单项最终都引用循环变量的最后一个值。
                OuterrealmArchotechTeleportStationWorldObject localDestination = destination;
                yield return new FloatMenuOption(
                    "OATS_CommandTeleportToStation".Translate(localDestination.LabelCap),
                    () => OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, this, localDestination));
            }
        }

        /// <summary>
        /// 控制传送站局部地图何时可以卸载。
        /// 返回 true 只表示卸载 Map；alsoRemoveWorldObject 保持 false，确保世界地标不会随空地图一起销毁。
        /// </summary>
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (!HasMap)
            {
                return false;
            }

            // 传送站地标永久保留；只有局部地图空置时允许卸载。
            return !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }
    }
}
