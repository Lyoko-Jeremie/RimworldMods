using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 跨 Game 静态缓存的统一清理门面。
    ///
    /// 触发背景：读档与地图扩张（Map Expander / Seamless Map Expansion）都会走
    /// Verse.SavedGameLoaderNow.LoadGameFromSaveFileNow，它直接执行
    /// Current.Game = new Game() 再 LoadGame()，不会调用 Game.DeinitAndRemoveMap，
    /// 因此 MapComponent.MapRemoved 与相关 Harmony 补丁都不会触发。旧 Game 的 Map
    /// 及其全部 Thing 只会被静态缓存继续强引用，造成整图泄漏。
    ///
    /// 清理分两档：
    ///  • ClearAll：新 Game 构造期（GameComponent 构造函数）调用，逻辑上等价于"换 Game"，
    ///    所有按对象引用键的缓存全部失效；
    ///  • ClearForMap：MapComponent.MapRemoved 调用，只清当前这张图，覆盖正常删图。
    ///
    /// 注意：SavedGameLoaderNow 会执行两次 new Game()（InitLoading 前 / EnterNode("game") 后），
    /// 因此 ClearAll 会被调用两次。它必须幂等，并且不得依赖 Current.Game——两次执行时
    /// Current.Game 分别指向旧 Game 与第一个空 Game。
    ///
    /// 新增按 Map / Area / Pawn 键的静态缓存时，必须同时在两档里登记。
    /// </summary>
    internal static class OmniStaticCacheCleaner
    {
        /// <summary>清空全部跨 Game 静态缓存。只在新 Game 构造期调用。</summary>
        public static void ClearAll()
        {
            CompBiosphereManager.ClearAllStaticCaches();
            RegionTypeUtility_GetExpectedRegionType_Patch.ClearAllStaticCaches();
            OmniWorkProxyEntryAuthorization.InvalidateAll();
            OmniWorkProxyUtility.ClearAllStaticCaches();
            GameComponent_OmniWorkProxyRegistry.ClearPendingArrivalsAll();
            Building_MatterEnergyConverter.ClearAllStaticCaches();
            // 判定 Pawn 与填充需求缓存都按 Map / building 键持有对象引用，换 Game 必须清。
            OmniHaulJudge.ClearAll();
            HaulDemandRunner.ClearAllCaches();
        }

        /// <summary>只清与指定地图相关的静态缓存。正常删图（MapRemoved）时调用。</summary>
        public static void ClearForMap(Map map)
        {
            if (map == null) return;

            CompBiosphereManager.ClearForMapStaticCaches(map);
            RegionTypeUtility_GetExpectedRegionType_Patch.ClearForMapStaticCaches(map);
            OmniWorkProxyEntryAuthorization.Invalidate(map);
            OmniWorkProxyUtility.ClearForMapStaticCaches(map);
            GameComponent_OmniWorkProxyRegistry.ClearPendingArrivalsForMap(map);
            Building_MatterEnergyConverter.ClearForMapStaticCaches(map);
            OmniHaulJudge.ReleaseMap(map);
        }
    }
}
