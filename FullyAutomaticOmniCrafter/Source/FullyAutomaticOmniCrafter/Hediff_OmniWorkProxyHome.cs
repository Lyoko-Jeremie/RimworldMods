using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 代理归属的 Hediff 镜像。
    ///
    /// 权威归属表在 GameComponent_OmniWorkProxyRegistry —— 它能回答"某个池现在有哪些代理"
    /// （包含被第三方容器吞掉、以及正在别的图上工作的那些），这是代理本体无论如何都回答不了
    /// 的问题，因此 registry 不可被取代。
    ///
    /// 本 Hediff 只是在**代理身上**再存一份副本：HediffSet 随存档深保存
    /// （原版 Source/Verse/HediffSet.cs:184 用 Scribe_Collections.Look(..., LookMode.Deep)），
    /// 于是归属表一旦损坏或丢失，仍可从代理身上把归属救回来。
    ///
    /// 只存 int 而不是 Map 引用：地图被销毁后它只是查不到，不会留下悬空引用。
    /// </summary>
    public class Hediff_OmniWorkProxyHome : HediffWithComps
    {
        /// <summary>归属池所在地图的 uniqueID；-1 表示未登记。</summary>
        public int homeMapUniqueId = -1;

        /// <summary>创建该代理时绑定的工作站 thingIDNumber；-1 表示未知。</summary>
        public int stationThingId = -1;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref homeMapUniqueId, "homeMapUniqueId", -1);
            Scribe_Values.Look(ref stationThingId, "stationThingId", -1);
        }
    }
}
