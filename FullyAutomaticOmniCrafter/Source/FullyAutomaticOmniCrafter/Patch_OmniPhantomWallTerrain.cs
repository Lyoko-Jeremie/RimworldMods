using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 增加hook，使得OmniPhantomWall2和OmniPhantomWall可以建造在任何地形上。
    /// 拦截 GenConstruct.CanBuildOnTerrain，对本 Mod 的幻影墙始终返回 true。
    /// </summary>
    [HarmonyPatch(typeof(GenConstruct), "CanBuildOnTerrain")]
    public static class Patch_GenConstruct_CanBuildOnTerrain
    {
        [HarmonyPrefix]
        public static bool Prefix(BuildableDef entDef, ref bool __result)
        {
            if (entDef == null) return true;

            // 检查是否为本 Mod 的幻影墙类建筑
            if (IsOmniPhantomWall(entDef))
            {
                __result = true;
                return false; // 跳过原版地形检查
            }

            return true;
        }

        private static bool IsOmniPhantomWall(BuildableDef def)
        {
            string defName = def.defName;
            
            // 匹配 OmniPhantomWall 和 OmniPhantomWall2 及其变体
            if (defName.StartsWith("OmniPhantomWall") || defName.StartsWith("OmniPhantomWall2"))
            {
                return true;
            }

            // 安全检查：检查类名
            if (def is ThingDef thingDef)
            {
                if (typeof(Building_OmniPhantomWall).IsAssignableFrom(thingDef.thingClass) ||
                    typeof(Building_OmniPhantomWall2).IsAssignableFrom(thingDef.thingClass))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
