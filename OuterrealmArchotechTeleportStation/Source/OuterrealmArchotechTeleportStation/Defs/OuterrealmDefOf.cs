using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 本 Mod 使用的核心 Def 缓存。
    /// RimWorld 会在所有 XML Def 加载后为带有 <see cref="DefOfAttribute"/> 的静态字段绑定同名 Def，
    /// 这样运行时代码可以避免反复按字符串查找 Def。
    /// </summary>
    [DefOf]
    public static class OuterrealmDefOf
    {
        /// <summary>
        /// 世界地图上的超维科技远古传送站地标。
        /// </summary>
        public static WorldObjectDef OuterrealmArchotechTeleportStation;

        /// <summary>
        /// 传送站局部地图使用的轻量地图生成器。
        /// </summary>
        public static MapGeneratorDef OuterrealmArchotechTeleportStationMap;

        /// <summary>
        /// 负责铺设传送站小地图和生成传送门建筑的 GenStep。
        /// </summary>
        public static GenStepDef OuterrealmArchotechTeleportStationMapLayout;

        /// <summary>
        /// 小地图内可交互的主传送门建筑。
        /// </summary>
        public static ThingDef OuterrealmArchotechTeleportPortal;

        static OuterrealmDefOf()
        {
            // 让 RimWorld 在 DefOf 未初始化时给出明确警告，便于定位 XML defName 拼写问题。
            DefOfHelper.EnsureInitializedInCtor(typeof(OuterrealmDefOf));
        }
    }
}
