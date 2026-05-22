using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // 修复门在幻影墙旁边时旋转不正确的问题（连接性逻辑）
    [HarmonyPatch(typeof(DoorUtility), "AlignQualityAgainst")]
    public static class Patch_DoorUtility_AlignQualityAgainst
    {
        public static void Postfix(IntVec3 c, IntVec3 offset, Map map, ref int __result)
        {
            // 如果原来的结果不是 9 (Impassable)，我们检查是否是幻影墙
            if (__result < 9)
            {
                IntVec3 targetCell = c + offset;
                if (targetCell.InBounds(map))
                {
                    Building edifice = targetCell.GetEdifice(map);
                    if (edifice != null && (edifice is Building_OmniPhantomWall || edifice is Building_OmniPhantomWall2))
                    {
                        // 幻影墙虽然是 Standable，但逻辑上应该被视为墙，所以返回 9
                        __result = 9;
                    }
                }
            }
        }
    }

    // 修复幻影墙视觉上不连接门的问题
    [HarmonyPatch(typeof(Graphic_Linked), nameof(Graphic_Linked.ShouldLinkWith))]
    public static class Patch_GraphicLinked_ShouldLinkWith
    {
        public static void Postfix(IntVec3 c, Thing parent, ref bool __result)
        {
            // 如果已经连接了，或者父物体不是幻影墙，我们不干预
            if (__result) return;
            if (!(parent is Building_OmniPhantomWall) && !(parent is Building_OmniPhantomWall2)) return;

            // 检查目标格子是否有门
            if (c.InBounds(parent.Map))
            {
                if (c.GetDoor(parent.Map) != null)
                {
                    __result = true;
                }
            }
        }
    }
}
