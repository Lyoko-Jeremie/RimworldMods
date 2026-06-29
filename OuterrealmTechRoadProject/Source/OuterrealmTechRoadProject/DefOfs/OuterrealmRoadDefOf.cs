using RimWorld;
using Verse;

namespace OuterrealmTechRoadProject.DefOfs
{
    /// <summary>
    /// RoadDef 的强类型入口，避免在源码中到处用字符串查 Def。
    /// 字段名必须与 XML 中的 defName 完全一致。
    /// </summary>
    [DefOf]
    public static class OuterrealmRoadDefOf
    {
        /// <summary>
        /// 本 Mod 唯一新增的世界道路：超维链路。
        /// </summary>
        public static RoadDef OuterrealmTech_OuterrealmLink;

        static OuterrealmRoadDefOf()
        {
            // DefOfHelper 会在 defs 加载后把同名 def 绑定到静态字段。
            DefOfHelper.EnsureInitializedInCtor(typeof(OuterrealmRoadDefOf));
        }
    }
}
