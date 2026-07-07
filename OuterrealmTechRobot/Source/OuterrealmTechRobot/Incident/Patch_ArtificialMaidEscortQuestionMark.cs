using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ShouldShowQuestionMark))]
    public static class Patch_Pawn_ShouldShowQuestionMark_ArtificialMaidEscort
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (__result)
            {
                return;
            }

            // 复用原版黄色问号覆盖层，标记可交互的护卫队领队。
            __result = ArtificialMaidEscortUtility.CanDismissEscortLeader(__instance);
        }
    }
}
