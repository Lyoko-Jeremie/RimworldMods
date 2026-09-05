using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    [HarmonyPatch(typeof(Thing), nameof(Thing.AmbientTemperature), MethodType.Getter)]
    public static class Patch_PersonalTemperature_Ambient
    {
        public static void Prefix(Thing __instance, out Pawn __state)
        {
            __state = PersonalTemperatureReadContext.Pawn;
            // 嵌套读取其他角色时，不能让婴儿或床主人的上下文污染其环境温度。
            if (__instance is Pawn) PersonalTemperatureReadContext.Pawn = null;
        }
        public static void Finalizer(Pawn __state) { PersonalTemperatureReadContext.Pawn = __state; }
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Thing __instance, ref float __result)
        {
            if (__instance is Pawn pawn && pawn.SpawnedOrAnyParentSpawned
                && PersonalTemperatureUtility.TryGetOverride(pawn, pawn.PositionHeld, pawn.MapHeld, out float value))
                __result = value;
        }
    }

    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.SafeTemperatureAtCell))]
    public static class Patch_PersonalTemperature_SafeCell
    {
        public static void Prefix(out Pawn __state)
        {
            __state = PersonalTemperatureReadContext.Pawn;
            PersonalTemperatureReadContext.Pawn = null;
        }
        public static void Finalizer(Pawn __state) { PersonalTemperatureReadContext.Pawn = __state; }
        public static void Postfix(Pawn p, IntVec3 cell, Map map, ref bool __result)
        {
            if (PersonalTemperatureUtility.TryGetOverride(p, cell, map, out float value))
                __result = p.SafeTemperatureRange().Includes(value);
        }
    }

    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.ComfortableTemperatureAtCell))]
    public static class Patch_PersonalTemperature_ComfortableCell
    {
        public static void Prefix(out Pawn __state)
        {
            __state = PersonalTemperatureReadContext.Pawn;
            PersonalTemperatureReadContext.Pawn = null;
        }
        public static void Finalizer(Pawn __state) { PersonalTemperatureReadContext.Pawn = __state; }
        public static void Postfix(Pawn p, IntVec3 cell, Map map, ref bool __result)
        {
            if (PersonalTemperatureUtility.TryGetOverride(p, cell, map, out float value))
                __result = p.ComfortableTemperatureRange().Includes(value);
        }
    }

    [HarmonyPatch(typeof(JobGiver_SeekSafeTemperature), nameof(JobGiver_SeekSafeTemperature.ClosestRegionWithinTemperatureRange))]
    public static class Patch_PersonalTemperature_SeekRegion
    {
        public static bool Prefix(IntVec3 root, Map map, Pawn pawn, ref FloatRange tempRange, ref Region __result)
        {
            if (!PersonalTemperatureUtility.TryGetOverride(pawn, root, map, out float value)) return true;
            // 效果覆盖整张地图：目标值不安全时不存在更好的区域，避免来回寻找。
            if (!tempRange.Includes(value)) { __result = null; return false; }
            // 所有区域对该角色温度相同，继续保留原版的可达性和活动区检查。
            tempRange = new FloatRange(float.MinValue, float.MaxValue);
            return true;
        }
    }

    // 仅在原版缺少角色参数的婴儿选床和床信息调用内传递上下文。
    // 线程局部变量支持嵌套，Finalizer 保证异常时也恢复，不能污染后续环境温度查询。
    internal static class PersonalTemperatureReadContext
    {
        [ThreadStatic] internal static Pawn Pawn;
    }

    [HarmonyPatch(typeof(ChildcareUtility), nameof(ChildcareUtility.SafePlaceForBaby))]
    public static class Patch_PersonalTemperature_BabyPlace
    {
        public static void Prefix(Pawn baby, out Pawn __state)
        {
            __state = PersonalTemperatureReadContext.Pawn;
            PersonalTemperatureReadContext.Pawn = baby;
        }
        public static void Finalizer(Pawn __state) { PersonalTemperatureReadContext.Pawn = __state; }
    }

    [HarmonyPatch(typeof(ChildcareUtility), nameof(ChildcareUtility.BabyNeedsMovingForTemperatureReasons))]
    public static class Patch_PersonalTemperature_BabyMove
    {
        public static void Prefix(Pawn baby, out Pawn __state)
        {
            __state = PersonalTemperatureReadContext.Pawn;
            PersonalTemperatureReadContext.Pawn = baby;
        }
        public static void Finalizer(Pawn __state) { PersonalTemperatureReadContext.Pawn = __state; }
    }

    [HarmonyPatch(typeof(Building_Bed), nameof(Building_Bed.GetInspectString))]
    public static class Patch_PersonalTemperature_BedInspect
    {
        public static void Prefix(Building_Bed __instance, out Pawn __state)
        {
            __state = PersonalTemperatureReadContext.Pawn;
            PersonalTemperatureReadContext.Pawn = __instance.OwnersForReading.Count == 1 ? __instance.OwnersForReading[0] : null;
        }
        public static void Finalizer(Pawn __state) { PersonalTemperatureReadContext.Pawn = __state; }
    }

    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.GetTemperatureForCell))]
    public static class Patch_PersonalTemperature_ContextCell
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(IntVec3 c, Map map, ref float __result)
        {
            Pawn pawn = PersonalTemperatureReadContext.Pawn;
            if (pawn != null && pawn.MapHeld == map
                && PersonalTemperatureUtility.TryGetOverride(pawn, c, map, out float value)) __result = value;
        }
    }
}
