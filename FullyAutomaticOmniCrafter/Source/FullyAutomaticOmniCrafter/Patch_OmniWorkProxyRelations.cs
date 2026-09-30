using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 工作代理是建筑的工具而不是"人"，因此绝不允许它以任何身份参与原版的
    /// "生成期亲属关系"（GeneratePawnRelations）。
    ///
    /// 原版 <c>PawnGenerator.GeneratePawnRelations</c> 的候选池是
    /// <c>PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead</c> 中 def 与正在生成的 pawn
    /// 相同的全部 pawn，而代理的 race 正是 Human（与殖民者同一个 ThingDef），必然落在池内。
    /// 代理一旦被抽中，关系 worker 就会把它写成新 pawn 的父母 / 配偶 / 兄弟，而原版假定
    /// humanlike 的名字是 NameTriple：
    /// <list type="bullet">
    /// <item><c>PawnRelationWorker_Parent.ResolveMyName</c> 直接做 <c>(NameTriple)</c> 强制转换；</item>
    /// <item><c>SpouseRelationUtility.ResolveNameForSpouseOnGeneration</c> 用 <c>as NameTriple</c>
    /// 之后不做判空就解引用。</item>
    /// </list>
    /// 于是代理会造成 InvalidCastException / NullReferenceException，连带打断正在进行的
    /// pawn 生成（对代理自身与对任何人类 pawn 都成立）。
    ///
    /// 这里在 <c>PawnRelationWorker.BaseGenerationChanceFactor</c> 上把生成权重归零：原版真正
    /// 参与生成期随机亲属的关系类型（Parent / Child / Sibling / Spouse / Lover / Fiance /
    /// ExSpouse / ExLover）的 GenerationChance 都乘入这个公共因子，因此一处即可覆盖全部；
    /// 权重为 0 时 <c>RandomElementByWeightWithDefault</c> 永远不会选中它。其余关系类型
    /// （Grandparent / Cousin / HalfSibling 等）没有重写 GenerationChance，基类返回 0，
    /// 本就不会在生成期建立。
    /// 该补丁只影响"生成期随机亲属"，不触碰既有的社交与恋爱系统，也不使用 transpiler。
    /// </summary>
    [HarmonyPatch(typeof(PawnRelationWorker), nameof(PawnRelationWorker.BaseGenerationChanceFactor))]
    internal static class Patch_OmniWorkProxy_Relations
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn generated, Pawn other, ref float __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(generated) && !OmniWorkProxyUtility.IsProxy(other))
                return true;
            __result = 0f;
            return false;
        }
    }
}
