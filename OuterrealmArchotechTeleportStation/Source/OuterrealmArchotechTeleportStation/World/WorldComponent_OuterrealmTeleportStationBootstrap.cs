using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 世界级初始化组件。
    /// 它只在新世界初始化完成时检查一次传送站网络，并按世界覆盖率批量生成初始传送站。
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

            // 读档时不主动批量补点，避免升级旧存档时突然改变世界布局。
            if (fromLoad)
            {
                ensuredInitialStation = true;
                return;
            }

            // 如果旧流程或其他 Mod 已经放置了传送站，直接认为初始化完成。
            // 这样玩家手动删除/添加传送站后的状态完全由 WorldObjects 存档决定。
            if (ensuredInitialStation || OuterrealmTeleportNetworkUtility.GetStations().Count > 0)
            {
                ensuredInitialStation = true;
                return;
            }

            // 新世界生成阶段玩家基地尚未确定，因此不依赖玩家 tile，直接在整个世界均匀撒点。
            OuterrealmTeleportNetworkUtility.AddInitialStationsForNewWorld();

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
