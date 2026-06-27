using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 世界级初始化组件。
    /// 它只在世界初始化完成时检查一次传送站网络，确保新世界至少拥有一个可进入的入口传送站。
    /// 不在 Tick 中自动补点，避免长期扫描世界对象。
    /// </summary>
    public class WorldComponent_OuterrealmTeleportStationBootstrap : WorldComponent
    {
        /// <summary>
        /// 存档标记：防止同一个世界反复执行初始传送站补点逻辑。
        /// </summary>
        private bool ensuredInitialStation;

        public WorldComponent_OuterrealmTeleportStationBootstrap(World world)
            : base(world)
        {
        }

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);

            // 读档时如果旧存档已经存在传送站，直接认为初始化完成。
            // 这样玩家手动删除/添加传送站后的状态完全由 WorldObjects 存档决定。
            if (ensuredInitialStation || OuterrealmTeleportNetworkUtility.GetStations().Count > 0)
            {
                ensuredInitialStation = true;
                return;
            }

            // 新世界没有传送站时，尝试按统一选址规则创建一个入口。
            // 初始创建不弹消息，避免开局加载阶段出现不合时宜的提示。
            if (OuterrealmTeleportNetworkUtility.TryFindNewStationTile(out PlanetTile tile))
            {
                OuterrealmTeleportNetworkUtility.TryAddStationAt(tile, out _, out _, false);
            }

            ensuredInitialStation = true;
        }

        public override void ExposeData()
        {
            base.ExposeData();

            // 只保存“是否执行过初始补点”这一最小状态；传送站本身由 WorldObjects 保存。
            Scribe_Values.Look(ref ensuredInitialStation, "ensuredInitialStation");
        }
    }
}
