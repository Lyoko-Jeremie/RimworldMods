using System;
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
        private static Dictionary<Area, List<CompBiosphere>> areaToBiosphere = new Dictionary<Area, List<CompBiosphere>>();
        private static HashSet<Pawn> pawnsGrantedHediff = new HashSet<Pawn>();
        private static List<Pawn> tmpPawnsToRemove = new List<Pawn>();

        public static List<CompBiosphere> GetCompsForMap(Map map)
        {
            if (map != null && biosphereComps.TryGetValue(map, out var list))
            {
                return list;
            }
            return null;
        }

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
            UpdateAreaMapping(comp, null, comp.SelectedArea);
        }

        public static void Deregister(CompBiosphere comp, Map map)
        {
            if (map != null && biosphereComps.ContainsKey(map))
            {
                biosphereComps[map].Remove(comp);
            }
            UpdateAreaMapping(comp, comp.SelectedArea, null);
        }

        public static void UpdateAreaMapping(CompBiosphere comp, Area oldArea, Area newArea)
        {
            if (oldArea != null && areaToBiosphere.ContainsKey(oldArea))
            {
                areaToBiosphere[oldArea].Remove(comp);
                if (areaToBiosphere[oldArea].Count == 0)
                {
                    areaToBiosphere.Remove(oldArea);
                }
            }

            if (newArea != null)
            {
                if (!areaToBiosphere.ContainsKey(newArea))
                {
                    areaToBiosphere[newArea] = new List<CompBiosphere>();
                }
                
                // 如果区域已经有其他 Biosphere 控制，则同步设置
                if (areaToBiosphere[newArea].Count > 0 && !areaToBiosphere[newArea].Contains(comp))
                {
                    comp.SyncSettingsFrom(areaToBiosphere[newArea][0]);
                }

                if (!areaToBiosphere[newArea].Contains(comp))
                {
                    areaToBiosphere[newArea].Add(comp);
                }
            }
        }

        public static void NotifySettingsChanged(CompBiosphere source)
        {
            Area area = source.SelectedArea;
            if (area != null && areaToBiosphere.TryGetValue(area, out var comps))
            {
                foreach (var comp in comps)
                {
                    if (comp != source)
                    {
                        comp.SyncSettingsFrom(source);
                    }
                }
            }
        }

        public static void NotifyAreaChanged(Area area, IntVec3 cell)
        {
            if (areaToBiosphere.TryGetValue(area, out var comps))
            {
                foreach (var comp in comps)
                {
                    if (comp.lightingMode != LightingMode.None)
                    {
                        area.Map.glowGrid.DirtyCell(cell);
                    }
                }
            }
        }

        public static CompBiosphere GetBiosphereAt(Map map, IntVec3 cell)
        {
            if (map == null || !biosphereComps.ContainsKey(map)) return null;
            foreach (var comp in biosphereComps[map])
            {
                Area area = comp.SelectedArea;
                if (area != null && area.ActiveCells.Count() > 0 && area[cell])
                {
                    return comp;
                }
            }
            return null;
        }

        public static void MaintainPawnEffects(Map map)
        {
            if (map == null || !biosphereComps.ContainsKey(map)) return;

            tmpPawnsToRemove.Clear();
            foreach (Pawn pawn in pawnsGrantedHediff)
            {
                if (pawn == null || !pawn.Spawned || pawn.Map != map || GetBiosphereAt(pawn.Map, pawn.Position) == null)
                {
                    tmpPawnsToRemove.Add(pawn);
                }
            }
            for (int i = 0; i < tmpPawnsToRemove.Count; i++)
            {
                Pawn pawn = tmpPawnsToRemove[i];
                pawnsGrantedHediff.Remove(pawn);
                RemovePawnEffects(pawn, map);
            }

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.health == null || !IsFriendlyPawn(pawn))
                {
                    continue;
                }

                var biosphere = GetBiosphereAt(pawn.Map, pawn.Position);
                if (biosphere != null)
                {
                    CompProperties_CompBiosphere props = biosphere.Props;
                    HediffDef hediffDef = props?.FriendlyHediffDefToUse;
                    if (props == null || !props.applyFriendlyHediff || hediffDef == null)
                    {
                        continue;
                    }

                    if (pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef) == null)
                    {
                        pawn.health.AddHediff(hediffDef);
                    }
                    pawnsGrantedHediff.Add(pawn);
                }
            }
        }

        private static void RemovePawnEffects(Pawn pawn, Map map)
        {
            if (pawn == null || pawn.health == null || map == null || !biosphereComps.ContainsKey(map))
            {
                return;
            }

            foreach (var comp in biosphereComps[map])
            {
                CompProperties_CompBiosphere props = comp?.Props;
                HediffDef hediffDef = props?.FriendlyHediffDefToUse;
                if (props == null || !props.removeFriendlyHediffWhenLeaving || hediffDef == null)
                {
                    continue;
                }

                Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                if (hediff != null)
                {
                    pawn.health.RemoveHediff(hediff);
                }
            }
        }

        private static bool IsFriendlyPawn(Pawn pawn)
        {
            if (pawn == null) return false;
            return pawn.Faction == Faction.OfPlayer;
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
    public static class Patch_Map_PostTick_Biosphere
    {
        public static void Postfix(Map __instance)
        {
            if (__instance.IsHashIntervalTick(60))
            {
                CompBiosphereManager.MaintainPawnEffects(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Plant), "Resting", MethodType.Getter)]
    public static class Patch_Plant_Resting
    {
        public static void Postfix(Plant __instance, ref bool __result)
        {
            if (!__result || !__instance.Spawned) return;
            var biosphere = CompBiosphereManager.GetBiosphereAt(__instance.Map, __instance.Position);
            if (biosphere != null && biosphere.lightingMode == LightingMode.Sunlight)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.TickLong))]
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

    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt))]
    public static class Patch_WildPlantSpawner_CheckSpawnWildPlantAt
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

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt))]
    public static class Patch_GlowGrid_GroundGlowAt
    {
        public static void Postfix(GlowGrid __instance, IntVec3 c, bool ignoreSky, ref float __result)
        {
            Map map = (Map)AccessTools.Field(typeof(GlowGrid), "map").GetValue(__instance);
            if (map == null) return;

            var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
            if (biosphere != null)
            {
                if (biosphere.lightingMode == LightingMode.Sunlight)
                {
                    if (ignoreSky)
                    {
                        __result = Mathf.Max(__result, 1f);
                        return;
                    }
                    if (map.roofGrid.Roofed(c))
                    {
                        __result = Mathf.Max(__result, map.skyManager.CurSkyGlow);
                    }
                    __result = Mathf.Max(__result, 1f);
                }
                else if (biosphere.lightingMode == LightingMode.Light)
                {
                    // 修复亮度问题：如果外部光照大于50%，按照外部亮度显示
                    __result = Mathf.Max(__result, Mathf.Max(0.5f, map.skyManager.CurSkyGlow));
                }
            }
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.VisualGlowAt), new[] { typeof(IntVec3) })]
    public static class Patch_GlowGrid_VisualGlowAtCell
    {
        public static void Postfix(GlowGrid __instance, IntVec3 c, ref Color32 __result)
        {
            Map map = (Map)AccessTools.Field(typeof(GlowGrid), "map").GetValue(__instance);
            if (map == null) return;

            var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
            if (biosphere != null)
            {
                // 直接获取逻辑亮度（GroundGlowAt），确保视觉与逻辑一致
                float logicGlow = __instance.GroundGlowAt(c);
                byte light = (byte)Mathf.Clamp(Mathf.RoundToInt(logicGlow * 255f), 0, 255);

                __result.r = (byte)Mathf.Max(__result.r, light);
                __result.g = (byte)Mathf.Max(__result.g, light);
                __result.b = (byte)Mathf.Max(__result.b, light);
                __result.a = (byte)Mathf.Max(__result.a, (byte)255);
            }
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.VisualGlowAt), new[] { typeof(int) })]
    public static class Patch_GlowGrid_VisualGlowAtIndex
    {
        public static void Postfix(GlowGrid __instance, int index, ref Color32 __result)
        {
            Map map = (Map)AccessTools.Field(typeof(GlowGrid), "map").GetValue(__instance);
            if (map == null) return;

            IntVec3 c = map.cellIndices.IndexToCell(index);
            var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
            if (biosphere != null)
            {
                // 直接获取逻辑亮度（GroundGlowAt），确保视觉与逻辑一致
                float logicGlow = __instance.GroundGlowAt(c);
                byte light = (byte)Mathf.Clamp(Mathf.RoundToInt(logicGlow * 255f), 0, 255);

                __result.r = (byte)Mathf.Max(__result.r, light);
                __result.g = (byte)Mathf.Max(__result.g, light);
                __result.b = (byte)Mathf.Max(__result.b, light);
                __result.a = (byte)Mathf.Max(__result.a, (byte)255);
            }
        }
    }

    [HarmonyPatch(typeof(SectionLayer_IndoorMask), "HideCommon")]
    public static class Patch_Biosphere_IndoorMask
    {
        public static void Postfix(Map map, IntVec3 c, ref bool __result)
        {
            if (__result && map != null)
            {
                var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
                if (biosphere != null && biosphere.lightingMode == LightingMode.Sunlight)
                {
                    __result = false; // 取消室内阴影，让阳光看起来透进来
                }
            }
        }
    }

    [HarmonyPatch(typeof(Plant), "GrowthRateFactor_Light", MethodType.Getter)]
    public static class Patch_Biosphere_PlantLight
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            if (!__instance.Spawned) return;
            var biosphere = CompBiosphereManager.GetBiosphereAt(__instance.Map, __instance.Position);
            if (biosphere != null && biosphere.lightingMode == LightingMode.Sunlight)
            {
                // 强制获得满光照生长率
                __result = Mathf.Max(__result, 1f);
            }
        }
    }

    [HarmonyPatch(typeof(CompPowerPlantSolar), "DesiredPowerOutput", MethodType.Getter)]
    public static class Patch_Biosphere_SolarPowerOutput
    {
        public static void Postfix(CompPowerPlantSolar __instance, ref float __result)
        {
            if (__instance?.parent?.Spawned != true) return;
            Map map = __instance.parent.Map;

            bool inBiosphereSunlight = false;
            foreach (IntVec3 c in __instance.parent.OccupiedRect())
            {
                var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
                if (biosphere != null && biosphere.lightingMode == LightingMode.Sunlight)
                {
                    inBiosphereSunlight = true;
                    break;
                }
            }

            if (inBiosphereSunlight)
            {
                // 强制满太阳运行（负值表示发电）
                __result = -__instance.Props.PowerConsumption;
            }
        }
    }

    [HarmonyPatch(typeof(CompPowerPlantSolar), "RoofedPowerOutputFactor", MethodType.Getter)]
    public static class Patch_Biosphere_SolarRoofedFactor
    {
        public static void Postfix(CompPowerPlantSolar __instance, ref float __result)
        {
            if (__instance?.parent?.Spawned != true) return;
            Map map = __instance.parent.Map;
            
            int cellCount = 0;
            int biosphereSunlightCount = 0;
            foreach (IntVec3 c in __instance.parent.OccupiedRect())
            {
                cellCount++;
                var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
                if (biosphere != null && biosphere.lightingMode == LightingMode.Sunlight)
                {
                    biosphereSunlightCount++;
                }
            }

            if (cellCount > 0 && biosphereSunlightCount > 0)
            {
                // 计算受生物圈保护的比例
                float factor = (float)biosphereSunlightCount / cellCount;
                __result = Mathf.Max(__result, factor);
            }
        }
    }

    [HarmonyPatch(typeof(PlaceWorker_NotUnderRoof), nameof(PlaceWorker_NotUnderRoof.AllowsPlacing))]
    public static class Patch_Biosphere_NotUnderRoofPlacement
    {
        public static void Postfix(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, ref AcceptanceReport __result)
        {
            if (__result.Accepted || map == null) return;

            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, checkingDef.Size))
            {
                var biosphere = CompBiosphereManager.GetBiosphereAt(map, c);
                if (biosphere == null || biosphere.lightingMode != LightingMode.Sunlight)
                {
                    return; // 只要有一个格没被覆盖且有屋顶，就维持原判
                }
            }

            __result = true; // 全部被覆盖，允许放置
        }
    }

    // 真空处理 (如果有 SOS2 等 Mod，可能需要额外的 Patch)
    // 这里 Patch Room.OpenRoofCount 使其始终认为有屋顶（如果 biosphere.ensureNoVacuum）
    // 这能防止原版中的“室外”判定，从而保持氧气/温度。
    [HarmonyPatch(typeof(Room), nameof(Room.OpenRoofCount), MethodType.Getter)]
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

    [HarmonyPatch(typeof(Area), "Set")]
    public static class Patch_Area_Set
    {
        public static void Postfix(Area __instance, IntVec3 c)
        {
            CompBiosphereManager.NotifyAreaChanged(__instance, c);
        }
    }
}
