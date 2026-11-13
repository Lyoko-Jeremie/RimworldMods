using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.GoodClimate
{
    /// <summary>
    /// 让「强力通风」按钮在任意一侧是室外时仍可点。
    ///
    /// 只替换 GC 的允许判定；实际交换由 <see cref="GcVentExchangePatch"/> 抑制，
    /// 改由 AO 的 AirMover 接管，因此这里不再要求两侧都 !PsychologicallyOutdoors。
    /// </summary>
    [HarmonyPatch]
    internal static class GcVentAllowPatch
    {
        private static bool Prepare()
        {
            return GcAirRelay.GcThingCompType != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(GcAirRelay.GcThingCompType, "GC_IE_ventAllowIntensiveExchange");
        }

        private static bool Prefix(ThingComp __instance, ref bool __result)
        {
            __result = false;

            Building building = __instance.parent as Building;
            if (building == null || building.Map == null)
            {
                return false;
            }

            CompPowerTrader power = building.GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return false;
            }

            CompFlickable flick = building.GetComp<CompFlickable>();
            if (flick != null && !flick.SwitchIsOn)
            {
                return false;
            }

            Room roomA = GridsUtility.GetRoom(building.Position + building.Rotation.FacingCell, building.Map);
            Room roomB = GridsUtility.GetRoom(building.Position - building.Rotation.FacingCell, building.Map);
            if (roomA == null || roomB == null)
            {
                return false;
            }

            // 与 GC 原版唯一差异：不再要求两侧都 !PsychologicallyOutdoors。
            __result = roomA.Vacuum <= 0f && roomB.Vacuum <= 0f;
            return false;
        }
    }

    /// <summary>
    /// 抑制 GC 的房间级强力交换（AO 下 room.Temperature 直写不成立），
    /// 并用同一个 60 tick 节拍做 AirMover 的注册/注销扫描。
    ///
    /// Harmony 的 Postfix 在 Prefix 返回 false 后仍会执行，这里正好利用这一点。
    /// </summary>
    [HarmonyPatch]
    internal static class GcVentExchangePatch
    {
        private static bool Prepare()
        {
            return GcAirRelay.GcMapComponentType != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(GcAirRelay.GcMapComponentType, "GC_IE_ventIntensiveExchange");
        }

        // 原方法整体跳过；扫描放在 Prefix 内，避免依赖「Prefix 返回 false 后 Postfix 仍执行」
        // 这一 Harmony 实现细节（Harmony 3 上若 Postfix 不执行，AirMover 就永远不会注册）。
        private static bool Prefix(MapComponent __instance)
        {
            if (__instance != null)
            {
                GcAirRelay.Scan(__instance.map);
            }

            return false;
        }
    }

    /// <summary>
    /// 本程序集只在 AO 与 Good Climate 同时启用时由 LoadFolders 加载，
    /// 因此用 StaticConstructorOnStartup 自举，不需要主程序集调用。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class GcBootstrap
    {
        static GcBootstrap()
        {
            if (!GcAirRelay.Ready)
            {
                Log.Message("[AO Patch][GC] 未检测到 Good Climate 或 AO，跳过主动通风口接管。");
                return;
            }

            try
            {
                Harmony harmony = new Harmony("Jeremie.AtmosphereOverhaulPatch.GoodClimate");
                harmony.PatchAll(typeof(GcBootstrap).Assembly);
                Log.Message("[AO Patch][GC] 主动通风口 AirMover 接管已启用。");
            }
            catch (Exception e)
            {
                Log.Error("[AO Patch][GC] Harmony 初始化失败：" + e);
            }
        }
    }
}
