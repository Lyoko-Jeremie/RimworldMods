using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 拦截 Pawn_CarryTracker.TryDropCarriedThing（两个重载）：
    /// 游戏在征召/切换 job/进入容器等场景会强制调用它丢弃携带物。
    /// 当女仆正抱着主人时，除"女仆自己主动放下"与"原版合法携带 job"外一律拦截，
    /// 保证主人不会被意外丢下（征召/战斗/换 job 期间始终抱在怀里）。
    /// 放行判定见 ArtificialMaidCarryDropGuard.ShouldBlockDrop。
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CarryTracker_TryDropCarriedThing_BlockMasterDrop
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Pawn_CarryTracker), "TryDropCarriedThing",
                new Type[]
                {
                    typeof(IntVec3),
                    typeof(ThingPlaceMode),
                    typeof(Thing).MakeByRefType(),
                    typeof(Action<Thing, int>)
                });
        }

        public static bool Prefix(Pawn ___pawn, ref bool __result, ref Thing resultingThing)
        {
            if (!ArtificialMaidCarryDropGuard.ShouldBlockDrop(___pawn))
            {
                return true;
            }

            resultingThing = null;
            __result = false;
            return false;
        }
    }

    /// <summary>TryDropCarriedThing 的带数量重载（5 参）拦截。</summary>
    [HarmonyPatch]
    public static class Patch_CarryTracker_TryDropCarriedThingCount_BlockMasterDrop
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Pawn_CarryTracker), "TryDropCarriedThing",
                new Type[]
                {
                    typeof(IntVec3),
                    typeof(int),
                    typeof(ThingPlaceMode),
                    typeof(Thing).MakeByRefType(),
                    typeof(Action<Thing, int>)
                });
        }

        public static bool Prefix(Pawn ___pawn, ref bool __result, ref Thing resultingThing)
        {
            if (!ArtificialMaidCarryDropGuard.ShouldBlockDrop(___pawn))
            {
                return true;
            }

            resultingThing = null;
            __result = false;
            return false;
        }
    }
}
