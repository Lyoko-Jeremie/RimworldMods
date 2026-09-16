using HarmonyLib;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // 代理球形机体视觉：以下补丁只替换渲染节点产出的贴图，不改 race、bodyType、headType、
    // 基因、部位或任何 Pawn 数据，因此工作能力、身体部位与第三方 mod 的判定继续沿用人类语义。
    //
    // 调用时机：这些 GraphicFor 只在 PawnRenderNode.EnsureInitialized 首次初始化或渲染树被
    // SetDirty 重建时调用（EnsureInitializationWithoutRecache 无任何子类重写、requestRecache
    // 只有 DevMode 的渲染树调试窗口会置位），既不在每帧路径，也不在并行预绘制路径上；
    // 非代理在第一次引用比较后立刻交回原版逻辑。

    /// <summary>机体：把 humanlike 身体贴图整体换成球形机体贴图。</summary>
    [HarmonyPatch(typeof(PawnRenderNode_Body), nameof(PawnRenderNode_Body.GraphicFor), new[] { typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyBodyGraphic
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref Graphic __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = OmniWorkProxyVisuals.SphereGraphic;
            return false;
        }
    }

    /// <summary>头部：代理不显示肉身人头（头型数据仍保留，不影响任何逻辑）。</summary>
    [HarmonyPatch(typeof(PawnRenderNode_Head), nameof(PawnRenderNode_Head.GraphicFor), new[] { typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyHeadGraphic
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref Graphic __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>头发：代理不显示头发。</summary>
    [HarmonyPatch(typeof(PawnRenderNode_Hair), nameof(PawnRenderNode_Hair.GraphicFor), new[] { typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyHairGraphic
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref Graphic __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>胡须：代理不显示胡须。</summary>
    [HarmonyPatch(typeof(PawnRenderNode_Beard), nameof(PawnRenderNode_Beard.GraphicFor), new[] { typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyBeardGraphic
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref Graphic __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>身体纹身（Ideology）：代理不显示纹身。</summary>
    [HarmonyPatch(typeof(PawnRenderNode_Tattoo_Body), nameof(PawnRenderNode_Tattoo_Body.GraphicFor), new[] { typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyBodyTattooGraphic
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref Graphic __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>面部纹身（Ideology）：代理不显示纹身。</summary>
    [HarmonyPatch(typeof(PawnRenderNode_Tattoo_Head), nameof(PawnRenderNode_Tattoo_Head.GraphicFor), new[] { typeof(Pawn) })]
    internal static class Patch_OmniWorkProxyHeadTattooGraphic
    {
        [HarmonyPrefix]
        private static bool Prefix(Pawn pawn, ref Graphic __result)
        {
            if (!OmniWorkProxyUtility.IsProxy(pawn)) return true;
            __result = null;
            return false;
        }
    }
}
