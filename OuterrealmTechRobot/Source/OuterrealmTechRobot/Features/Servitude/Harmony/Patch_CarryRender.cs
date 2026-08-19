using HarmonyLib;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 被抱主人的渲染（借鉴 WolfeinMihoRatkinCarry 的三补丁方案）：
    ///   ① DynamicDrawPhaseAt Postfix：女仆画完后，在偏移位置手动 RenderPawnAt 画出被抱主人；
    ///   ② RenderPawnInternal Prefix：防止被抱主人被双绘、并清空女仆的原版"扛人"剪影参数；
    ///   ③ DynamicDrawPhaseAt Prefix：被抱主人作为普通 pawn 被地图绘制时跳过（它已 DeSpawn，正常不会触发，防御双绘）。
    /// 偏移：公主抱=被抱者位于女仆正前方、抬高至胸前；倒地主人（躺姿）自然形成横抱效果。
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.DynamicDrawPhaseAt))]
    public static class Patch_PawnRenderer_DynamicDrawPhaseAt_CarryMaster
    {
        public static bool Prefix(Pawn ___pawn, DrawPhase phase)
        {
            // 被抱主人自己的正常绘制 → 跳过（由载体 Postfix 手动绘制，避免双绘/错位）
            return ArtificialMaidCarryDrawState.Active ||
                   phase != DrawPhase.Draw ||
                   !ArtificialMaidCarryUtility.IsMasterCarriedByMaid(___pawn);
        }

        public static void Postfix(Pawn ___pawn, DrawPhase phase, Vector3 drawLoc, Rot4? rotOverride)
        {
            if (phase != DrawPhase.Draw || !ArtificialMaidCarryUtility.IsCarryingMaster(___pawn))
            {
                return;
            }

            Pawn carried = ___pawn.carryTracker.CarriedThing as Pawn;
            if (carried == null)
            {
                return;
            }

            Rot4 carrierRot = rotOverride ?? ___pawn.Rotation;
            Vector3 carriedPos = drawLoc + OffsetFor(carrierRot, carried);

            ArtificialMaidCarryDrawState.Begin();
            try
            {
                // 面对面托抱：被抱者朝向与载体相反；neverAimWeapon 避免被抱者端枪姿势
                carried.Drawer.renderer.RenderPawnAt(carriedPos, new Rot4?(carrierRot.Opposite), true);
            }
            finally
            {
                ArtificialMaidCarryDrawState.End();
            }
        }

        /// <summary>
        /// 公主抱偏移（载体朝向坐标系：North=0/East=1/South=2/West=3，+z 为南、+x 为东）。
        /// 被抱者位于载体正前方，站姿抬高至胸前、前伸略多；躺姿（倒地主人）略低、贴身，呈横抱。
        /// </summary>
        private static Vector3 OffsetFor(Rot4 rot, Pawn carried)
        {
            bool downed = carried.Downed;
            float lift = downed ? 0.35f : 0.7f;
            float fwd = downed ? 0.25f : 0.4f;
            switch (rot.AsInt)
            {
                case 0: // North → 前方 z-
                    return new Vector3(0f, lift, -fwd);
                case 1: // East → 前方 x+
                    return new Vector3(fwd, lift, 0f);
                case 2: // South → 前方 z+
                    return new Vector3(0f, lift, fwd);
                default: // West → 前方 x-
                    return new Vector3(-fwd, lift, 0f);
            }
        }
    }

    /// <summary>渲染参数修正：被抱主人跳过普通绘制；女仆清空原版"扛人"剪影参数。</summary>
    [HarmonyPatch(typeof(PawnRenderer), "RenderPawnInternal")]
    public static class Patch_PawnRenderer_RenderPawnInternal_CarryMaster
    {
        public static bool Prefix(ref PawnDrawParms parms)
        {
            // 被抱主人若被"正常"渲染 → 跳过（已由载体 Postfix 手动绘制）
            if (!ArtificialMaidCarryDrawState.Active && ArtificialMaidCarryUtility.IsMasterCarriedByMaid(parms.pawn))
            {
                return false;
            }

            // 女仆渲染时：原版"扛人"剪影（PawnDrawParms.carriedThing）不适用于公主抱 → 清空
            if (ArtificialMaidCarryUtility.IsCarryingMaster(parms.pawn))
            {
                parms.carriedThing = null;
            }

            return true;
        }
    }
}
