using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// RimExodus 软兼容入口。
    /// 所有 RimExodus 类型均通过字符串反射访问，确保未安装时程序集仍可正常加载。
    /// </summary>
    internal static class RimExodusCompat
    {
        internal const string PackageId = "RimExodus.SeamlessWorld.Dev";
        private const string GovernanceTypeName = "RimExodus.SeamlessMapGovernance";

        private static readonly bool ActiveInt =
            ModsConfig.IsActive(PackageId) && AccessTools.TypeByName(GovernanceTypeName) != null;

        internal static bool Active => ActiveInt;

        internal static bool LifecycleIntegrated { get; private set; }

        internal static void InstallDynamicPatches(Harmony harmony)
        {
            if (!Active || harmony == null)
            {
                return;
            }

            Type governanceType = AccessTools.TypeByName(GovernanceTypeName);
            MethodInfo target = AccessTools.Method(
                governanceType,
                "IsNativeFamily",
                new[] { typeof(MapParent) });
            MethodInfo postfix = AccessTools.Method(
                typeof(RimExodusCompat),
                nameof(IsNativeFamilyPostfix));

            if (target == null || postfix == null)
            {
                Log.Warning("[OuterrealmArchotechTeleportStation] RimExodus lifecycle integration could not be bound; " +
                    "station maps will be kept loaded to preserve seamless return travel.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                LifecycleIntegrated = true;
            }
            catch (Exception ex)
            {
                Log.Warning("[OuterrealmArchotechTeleportStation] RimExodus lifecycle integration failed; " +
                    "station maps will be kept loaded to preserve seamless return travel. " + ex);
            }
        }

        private static void IsNativeFamilyPostfix(MapParent __0, ref bool __result)
        {
            if (__0 is OuterrealmArchotechTeleportStationWorldObject &&
                !__0.Destroyed && __0.Tile.Valid && __0.Tile.LayerDef == PlanetLayerDefOf.Surface)
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// 统一安装本 Mod 的常规 Harmony 补丁和 RimExodus 动态补丁。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class OuterrealmArchotechTeleportStationHarmony
    {
        private const string HarmonyId = "Jeremie.Outerrealm.Tech.ArchotechTeleportStation";

        static OuterrealmArchotechTeleportStationHarmony()
        {
            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            RimExodusCompat.InstallDynamicPatches(harmony);
        }
    }

    /// <summary>
    /// RimExodus 模式下统一传送站地图尺寸。
    /// 该入口同时覆盖玩家直接进入与 RimExodus 从邻图预加载两条生成路径。
    /// </summary>
    [HarmonyPatch(typeof(GetOrGenerateMapUtility), nameof(GetOrGenerateMapUtility.GetOrGenerateMap),
        new[]
        {
            typeof(PlanetTile),
            typeof(IntVec3),
            typeof(WorldObjectDef),
            typeof(IEnumerable<GenStepWithParams>),
            typeof(bool)
        })]
    internal static class Patch_GetOrGenerateMap_RimExodusStationSize
    {
        private static void Prefix(PlanetTile tile, ref IntVec3 size)
        {
            if (!RimExodusCompat.Active || Find.WorldObjects == null)
            {
                return;
            }

            if (Find.WorldObjects.MapParentAt(tile) is OuterrealmArchotechTeleportStationWorldObject)
            {
                size = Find.World.info.initialMapSize;
            }
        }
    }

    /// <summary>
    /// 生命周期动态补丁绑定失败时的安全兜底。
    /// 正常绑定时交给 RimExodus 的治理 Prefix；失败时阻止传送站在 Pawn 刚跨缝后立即卸图。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.CheckRemoveMapNow))]
    internal static class Patch_MapParent_CheckRemoveMapNow_RimExodusStationFallback
    {
        private static bool Prefix(MapParent __instance)
        {
            if (!RimExodusCompat.Active || RimExodusCompat.LifecycleIntegrated)
            {
                return true;
            }

            return !(__instance is OuterrealmArchotechTeleportStationWorldObject && __instance.HasMap);
        }
    }
}
