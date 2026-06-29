using UnityEngine;
using Verse;

namespace OuterrealmTechRoadProject.UI
{
    /// <summary>
    /// 超维链路相关 UI 贴图缓存。
    /// RimWorld 的 Texture2D 应在静态构造阶段预加载，避免每次绘制 gizmo 时重复查找资源。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OuterrealmLinkTex
    {
        /// <summary>
        /// “规划超维链路”按钮图标。
        /// 如果纹理暂时不存在，回退为白色贴图，避免缺图导致报错。
        /// </summary>
        public static readonly Texture2D IconPlanOuterrealmLink =
            ContentFinder<Texture2D>.Get("UI/Commands/PlanOuterrealmLink", false) ?? BaseContent.WhiteTex;
    }
}
