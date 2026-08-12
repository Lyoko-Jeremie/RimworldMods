using RimWorld;
using Verse;

namespace OuterrealmTechDailyThings
{
    /// <summary>
    /// 超维科技返老还童药剂的使用效果属性。
    /// 参数均可在 ThingDef 的 comps 中通过 XML 覆盖。
    /// </summary>
    public class CompProperties_UseEffect_ReverseAge : CompProperties_UseEffect
    {
        /// <summary>逆转的年数（默认 10 年）。</summary>
        public float yearsToReverse = 10f;

        /// <summary>
        /// 是否仅允许人形生物使用（默认 true）。
        /// 机械体、动物等非人形生物没有生物学意义上的年龄逆转需求。
        /// </summary>
        public bool onlyHumanlike = true;

        /// <summary>
        /// 构造时指定实际效果组件类，供引擎实例化。
        /// </summary>
        public CompProperties_UseEffect_ReverseAge()
        {
            compClass = typeof(CompUseEffect_ReverseAge);
        }
    }
}
