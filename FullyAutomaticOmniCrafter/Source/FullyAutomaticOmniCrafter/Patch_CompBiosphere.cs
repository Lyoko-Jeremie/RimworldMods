using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    [StaticConstructorOnStartup]
    public static class CompBiosphereManager
    {
        private static Dictionary<Map, List<CompBiosphere>> biosphereComps = new Dictionary<Map, List<CompBiosphere>>();

        public static void Register(CompBiosphere comp)
        {
            if (comp.parent.Map == null) return;
            if (!biosphereComps.ContainsKey(comp.parent.Map))
            {
                biosphereComps[comp.parent.Map] = new List<CompBiosphere>();
            }
            if (!biosphereComps[comp.parent.Map].Contains(comp))
            {
                biosphereComps[comp.parent.Map].Add(comp);
            }
        }

        public static void Deregister(CompBiosphere comp, Map map)
        {
            if (map != null && biosphereComps.ContainsKey(map))
            {
                biosphereComps[map].Remove(comp);
            }
        }

        public static CompBiosphere GetBiosphereAt(Map map, IntVec3 cell)
        {
            if (map == null || !biosphereComps.ContainsKey(map)) return null;
            foreach (var comp in biosphereComps[map])
            {
                if (comp.SelectedArea != null && comp.SelectedArea[cell])
                {
                    return comp;
                }
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(Plant), "Tick")]
    public static class Patch_Plant_Tick
    {
        public static bool Prefix(Plant __instance)
        {
            if (!__instance.Spawned) return true;
            var biosphere = CompBiosphereManager.GetBiosphereAt(__instance.Map, __instance.Position);
            if (biosphere != null)
            {
                if (biosphere.growthMode == PlantGrowthMode.Stopped)
                {
                    return false; // 停止生长
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(WildPlantSpawner), "CheckCellForWildPlant")]
    public static class Patch_WildPlantSpawner_CheckCellForWildPlant
    {
        public static bool Prefix(WildPlantSpawner __instance, IntVec3 c, ref bool __result)
        {
            // 通过 AccessTools 获取私有字段 map
            Map map = (Map)AccessTools.Field(typeof(WildPlantSpawner), "map").GetValue(__instance);
            if (map == null) return true;

            var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
            if (biosphere != null && biosphere.growthMode == PlantGrowthMode.Cleared)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GenTemperature), "GetTemperatureForCell")]
    public static class Patch_GenTemperature_GetTemperatureForCell
    {
        public static void Postfix(IntVec3 c, Map map, ref float __result)
        {
            var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
            if (biosphere != null && biosphere.controlTemperature)
            {
                __result = biosphere.targetTemperature;
            }
        }
    }

    [HarmonyPatch(typeof(GlowGrid), "GameGlowAt")]
    public static class Patch_GlowGrid_GameGlowAt
    {
        private static readonly Dictionary<GlowGrid, Map> glowGridMapCache = new Dictionary<GlowGrid, Map>();

        public static void Postfix(GlowGrid __instance, IntVec3 c, bool ignoreSky, ref float __result)
        {
            if (!glowGridMapCache.TryGetValue(__instance, out Map map))
            {
                map = Find.Maps.FirstOrDefault(m => m.glowGrid == __instance);
                if (map != null) glowGridMapCache[__instance] = map;
            }
            if (map == null) return;

            var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
            if (biosphere != null)
            {
                if (biosphere.lightingMode == LightingMode.Sunlight)
                {
                    __result = 1f;
                }
                else if (biosphere.lightingMode == LightingMode.Light)
                {
                    __result = Mathf.Max(__result, 0.5f);
                }
            }
        }
    }

    // 真空处理 (如果有 SOS2 等 Mod，可能需要额外的 Patch)
    // 这里 Patch Room.OpenRoofCount 使其始终认为有屋顶（如果 biosphere.ensureNoVacuum）
    // 这能防止原版中的“室外”判定，从而保持氧气/温度。
    [HarmonyPatch(typeof(Room), "OpenRoofCount", MethodType.Getter)]
    public static class Patch_Room_OpenRoofCount
    {
        public static void Postfix(Room __instance, ref int __result)
        {
            if (__result == 0 || __instance.Map == null) return;
            // 只要房间内有一个 cell 在 biosphere 保护下且开启了 ensureNoVacuum
            foreach (IntVec3 cell in __instance.Cells)
            {
                var biosphere = CompBiosphereManager.GetBiosphereAt(__instance.Map, cell);
                if (biosphere != null && biosphere.ensureNoVacuum)
                {
                    __result = 0; // 伪装成完全封闭
                    return;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Room), "OpenRoofPercentage", MethodType.Getter)]
    public static class Patch_Room_OpenRoofPercentage
    {
        public static void Postfix(Room __instance, ref float __result)
        {
            if (__result == 0f || __instance.Map == null) return;
            foreach (IntVec3 cell in __instance.Cells)
            {
                var biosphere = CompBiosphereManager.GetBiosphereAt(__instance.Map, cell);
                if (biosphere != null && biosphere.ensureNoVacuum)
                {
                    __result = 0f;
                    return;
                }
            }
        }
    }
}
