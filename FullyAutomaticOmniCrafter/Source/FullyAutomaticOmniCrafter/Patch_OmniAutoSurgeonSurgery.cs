using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 确保全自动手术台执行的手术永远不会失败。
    /// </summary>
    [HarmonyPatch(typeof(Recipe_Surgery), "CheckSurgeryFail")]
    public static class Patch_OmniAutoSurgeonSurgery_CheckSurgeryFail
    {
        public static bool Prefix(ref bool __result, Pawn surgeon, Pawn patient, List<Thing> ingredients, BodyPartRecord part, Bill bill)
        {
            if (!OmniAutoSurgeonSurgeryContext.IsActive)
            {
                return true;
            }

            // OmniAutoSurgeon operations should never fail surgery checks.
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 手术过程中不消耗原材料（因为全自动手术台通常是模拟或直接从内部消耗）。
    /// </summary>
    [HarmonyPatch(typeof(RecipeWorker), "ConsumeIngredient")]
    public static class Patch_OmniAutoSurgeonSurgery_ConsumeIngredient
    {
        public static bool Prefix()
        {
            return !OmniAutoSurgeonSurgeryContext.IsActive;
        }
    }

    /// <summary>
    /// 防止手术逻辑中意外销毁物品。
    /// </summary>
    [HarmonyPatch(typeof(Thing), "Destroy")]
    public static class Patch_OmniAutoSurgeonSurgery_ThingDestroy
    {
        public static bool Prefix(Thing __instance)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // We prevent destruction of things during OmniAutoSurgeon operations.
                // This covers cases where RecipeWorkers manually destroy ingredients.
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Thing), "SplitOff")]
    public static class Patch_OmniAutoSurgeonSurgery_ThingSplitOff
    {
        public static bool Prefix(Thing __instance, int count, ref Thing __result)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // Return the instance itself instead of splitting it, 
                // preventing stack count reduction.
                __result = __instance;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Recipe_AdministerUsableItem), "ApplyOnPawn")]
    public static class Patch_Recipe_AdministerUsableItem_ApplyOnPawn
    {
        public static bool Prefix(Recipe_AdministerUsableItem __instance, Pawn pawn, List<Thing> ingredients)
        {
            if (!OmniAutoSurgeonSurgeryContext.IsActive) return true;

            // If ingredients is empty, we find the thing def from recipe and simulate usage
            if (ingredients.Count == 0)
            {
                ThingDef itemDef = __instance.recipe.fixedIngredientFilter?.AnyAllowedDef;
                if (itemDef != null)
                {
                    // Create a temporary thing to get its CompUsable
                    Thing tempThing = ThingMaker.MakeThing(itemDef);
                    if (tempThing != null)
                    {
                        CompUsable comp = tempThing.TryGetComp<CompUsable>();
                        if (comp != null)
                        {
                            comp.UsedBy(pawn);
                        }
                    }
                }
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 强制手术质量为最高（例如闪耀世界医药的 2.0 倍效率）。
    /// </summary>
    [HarmonyPatch(typeof(SurgeryOutcomeComp_MedicineQuality), "XGetter")]
    public static class Patch_SurgeryOutcomeComp_MedicineQuality_XGetter
    {
        public static bool Prefix(ref float __result)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // Always return high quality (e.g. 2.0 for Glitterworld medicine potency)
                __result = 2.0f;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 允许从任何部位（即使不清洁或是动物部位）掉落/回收零件。
    /// </summary>
    [HarmonyPatch(typeof(MedicalRecipesUtility), "IsCleanAndDroppable")]
    public static class Patch_MedicalRecipesUtility_IsCleanAndDroppable
    {
        public static bool Prefix(ref bool __result, Pawn pawn, BodyPartRecord part)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                // If the part has a spawn thing, we allow it regardless of cleanliness or animal status
                if (part.def.spawnThingOnRemoved != null)
                {
                    __result = true;
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 手术期间强制部位为“清洁”，以绕过原版对感染或非人工部位的限制。
    /// </summary>
    [HarmonyPatch(typeof(MedicalRecipesUtility), "IsClean")]
    public static class Patch_MedicalRecipesUtility_IsClean
    {
        public static bool Prefix(ref bool __result)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive)
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 当物品尚未生成时，提供手术台所在的地图作为虚拟返回值。
    /// 这可以防止原版逻辑在处理刚创建但未放置的物品时因 Map 为 null 而报错。
    /// 注意：此补丁由 Patch_HighFrequency_Manual 手动挂载以优化性能。
    /// </summary>
    public static class Patch_Thing_Map
    {
        public static bool Prefix(Thing __instance, ref Map __result)
        {
            if (!__instance.Spawned && OmniAutoSurgeonSurgeryContext.CurrentSurgeon != null)
            {
                __result = OmniAutoSurgeonSurgeryContext.CurrentSurgeon.Map;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 当物品尚未生成时，提供手术台的位置（或其交互格）作为虚拟返回值。
    /// 注意：此补丁由 Patch_HighFrequency_Manual 手动挂载以优化性能。
    /// </summary>
    public static class Patch_Thing_Position
    {
        public static bool Prefix(Thing __instance, ref IntVec3 __result)
        {
            if (!__instance.Spawned && OmniAutoSurgeonSurgeryContext.CurrentSurgeon != null)
            {
                // Use interaction cell if available, otherwise machine position
                __result = OmniAutoSurgeonSurgeryContext.CurrentSurgeon.def.hasInteractionCell 
                    ? OmniAutoSurgeonSurgeryContext.CurrentSurgeon.InteractionCell 
                    : OmniAutoSurgeonSurgeryContext.CurrentSurgeon.Position;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 确保从 Hediff 生成的物体（如摘除的器官）掉落在手术台位置。
    /// </summary>
    [HarmonyPatch(typeof(MedicalRecipesUtility), "SpawnThingsFromHediffs")]
    public static class Patch_MedicalRecipesUtility_SpawnThingsFromHediffs
    {
        public static void Prefix(Pawn pawn, BodyPartRecord part, ref IntVec3 pos, ref Map map)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive && OmniAutoSurgeonSurgeryContext.CurrentSurgeon != null)
            {
                map = OmniAutoSurgeonSurgeryContext.CurrentSurgeon.Map;
                pos = OmniAutoSurgeonSurgeryContext.CurrentSurgeon.def.hasInteractionCell 
                    ? OmniAutoSurgeonSurgeryContext.CurrentSurgeon.InteractionCell 
                    : OmniAutoSurgeonSurgeryContext.CurrentSurgeon.Position;
            }
        }
    }

    /// <summary>
    /// 确保摘除的自然部位掉落在手术台位置。
    /// </summary>
    [HarmonyPatch(typeof(MedicalRecipesUtility), "SpawnNaturalPartIfClean")]
    public static class Patch_MedicalRecipesUtility_SpawnNaturalPartIfClean
    {
        public static void Prefix(Pawn pawn, BodyPartRecord part, ref IntVec3 pos, ref Map map)
        {
            if (OmniAutoSurgeonSurgeryContext.IsActive && OmniAutoSurgeonSurgeryContext.CurrentSurgeon != null)
            {
                // Force spawn at surgeon building's location
                map = OmniAutoSurgeonSurgeryContext.CurrentSurgeon.Map;
                pos = OmniAutoSurgeonSurgeryContext.CurrentSurgeon.def.hasInteractionCell 
                    ? OmniAutoSurgeonSurgeryContext.CurrentSurgeon.InteractionCell 
                    : OmniAutoSurgeonSurgeryContext.CurrentSurgeon.Position;
            }
        }
    }

    /// <summary>
    /// 手动补丁管理类。
    /// 用于动态挂载和卸载那些高频调用且对性能影响较大的补丁（如 Thing.Map 和 Thing.Position）。
    /// 补丁仅在全自动手术台工作的极短时间内生效，从而消除平时的性能开销。
    /// </summary>
    public static class Patch_HighFrequency_Manual
    {
        private static int patchCount = 0;
        private static readonly object lockObject = new object();

        public static void PatchHighFrequencyMethods(HarmonyLib.Harmony harmony)
        {
            lock (lockObject)
            {
                if (patchCount == 0)
                {
                    var mapGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
                    var posGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position));

                    if (mapGetter != null)
                    {
                        harmony.Patch(mapGetter, prefix: new HarmonyMethod(typeof(Patch_Thing_Map), nameof(Patch_Thing_Map.Prefix)));
                    }

                    if (posGetter != null)
                    {
                        harmony.Patch(posGetter, prefix: new HarmonyMethod(typeof(Patch_Thing_Position), nameof(Patch_Thing_Position.Prefix)));
                    }
                }
                patchCount++;
            }
        }

        public static void UnpatchHighFrequencyMethods(HarmonyLib.Harmony harmony)
        {
            lock (lockObject)
            {
                if (patchCount > 0)
                {
                    patchCount--;
                    if (patchCount == 0)
                    {
                        var mapGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map));
                        var posGetter = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position));

                        if (mapGetter != null)
                        {
                            harmony.Unpatch(mapGetter, HarmonyPatchType.Prefix, harmony.Id);
                        }

                        if (posGetter != null)
                        {
                            harmony.Unpatch(posGetter, HarmonyPatchType.Prefix, harmony.Id);
                        }
                    }
                }
            }
        }
    }
}

