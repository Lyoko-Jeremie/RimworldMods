using UnityEngine;
using Verse;

namespace OuterrealmTechRoadProject.UI
{
    [StaticConstructorOnStartup]
    public static class OuterrealmLinkTex
    {
        public static readonly Texture2D IconPlanOuterrealmLink =
            ContentFinder<Texture2D>.Get("UI/Commands/PlanOuterrealmLink", false) ?? BaseContent.WhiteTex;
    }
}
