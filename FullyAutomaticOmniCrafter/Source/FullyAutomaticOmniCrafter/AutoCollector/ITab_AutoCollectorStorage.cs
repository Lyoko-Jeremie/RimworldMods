using RimWorld;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 自动收集器的存储筛选器页。
    ///
    /// 直接复用原版 ITab_Storage 的 ThingFilterUI，只隐藏对本建筑无意义的优先级下拉
    /// （本建筑不是 IHaulDestination，优先级不参与原版搬运）。
    /// </summary>
    public class ITab_AutoCollectorStorage : ITab_Storage
    {
        protected override bool IsPrioritySettingVisible => false;
    }
}
