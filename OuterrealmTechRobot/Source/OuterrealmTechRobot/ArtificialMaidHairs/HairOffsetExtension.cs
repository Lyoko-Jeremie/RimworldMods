using RimWorld;
using UnityEngine;
using Verse;
using HarmonyLib;

namespace OuterrealmTechRobot.ArtificialMaidHairs
{
    public class ArtificialMaidHairOffsetExtension : DefModExtension
    {
        // 默认全为 0。使用 Vector3 方便同时控制 X(左右), Y(图层), Z(上下)
        public Vector3 offsetSouth = Vector3.zero;
        public Vector3 offsetNorth = Vector3.zero;
        public Vector3 offsetEast  = Vector3.zero;
        public Vector3 offsetWest  = Vector3.zero;
    }
    
    [HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.OffsetFor))]
    public static class Patch_HairOffset
    {
        public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref Vector3 __result)
        {
            if (!(node is PawnRenderNode_Hair))
            {
                return;
            }

            // 快速失败：如果没有小人或没头发，直接跳过
            if (parms.pawn?.story?.hairDef == null) return;

            // 核心魔法：GetModExtension 获取该发型绑定的扩展数据！
            // 这个操作在底层是被高度缓存的，性能极其优异
            var extension = parms.pawn.story.hairDef.GetModExtension<ArtificialMaidHairOffsetExtension>();
            
            // 如果这个发型没有我们写的扩展，说明是原版发型或其他不用改的发型，直接跳过
            if (extension == null) return;

            // 如果有扩展，根据小人的朝向，将 XML 里写的偏移量加上去
            if (parms.facing == Rot4.South)
            {
                __result += extension.offsetSouth;
            }
            else if (parms.facing == Rot4.North)
            {
                __result += extension.offsetNorth;
            }
            else if (parms.facing == Rot4.East)
            {
                __result += extension.offsetEast;
            }
            else if (parms.facing == Rot4.West)
            {
                __result += extension.offsetWest;
            }
        }
    }
}
