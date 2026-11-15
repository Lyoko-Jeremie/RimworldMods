using System;
using System.Collections.Generic;
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
    /// 给通风口追加「自然通风」开关按钮。
    ///
    /// 强力模式开启时按钮禁用并提示原因；关闭强力模式后玩家可手动打开自然通风。
    /// 新放置的通风口默认关闭自然通风（见 GcAirRelay.EnsureDefaultClosed）。
    /// </summary>
    [HarmonyPatch]
    internal static class GcNaturalVentGizmoPatch
    {
        private static bool Prepare()
        {
            return GcAirRelay.GcThingCompType != null && GcAirRelay.AirVentType != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(GcAirRelay.GcThingCompType, "CompGetGizmosExtra");
        }

        private static void Postfix(ref IEnumerable<Gizmo> __result, ThingComp __instance)
        {
            // Harmony 对 iterator 方法的返回值替换并不总是可靠（Postfix 直接 yield
            // 在部分 Harmony 版本里不会写回 __result），这里显式构造列表再赋值。
            List<Gizmo> gizmos = new List<Gizmo>();
            if (__result != null)
            {
                foreach (Gizmo gizmo in __result)
                {
                    gizmos.Add(gizmo);
                }
            }

            if (__instance == null || __instance.parent == null)
            {
                __result = gizmos;
                return;
            }

            ThingComp airVent = GcAirRelay.FindAirVent(__instance.parent);
            if (airVent != null)
            {
                gizmos.Add(GcAirRelay.BuildNaturalVentGizmo(airVent));
            }

            __result = gizmos;
        }
    }

    /// <summary>
    /// 让 AO 的速度滑条与「自然通风」按钮共用同一个值（CompAirVent.speedLimit），
    /// 只处理两件事：
    /// 1. 主动通风（GC 的 intensive 模式）运行中，把滑条的任何写入压成 0，滑条锁死；
    /// 2. 按钮点击的那一帧，丢弃滑条的写回。
    ///
    /// 为什么需要第 2 点：原版 Gizmo_Slider 每次 GizmoOnGUI 都执行
    /// `this.Target = this.targetValuePct`（Verse/Gizmo_Slider.cs:114-117），
    /// 而 GizmoGridDrawer 在同一个 Time.frameCount 内复用已建好的 gizmo 列表
    /// （Verse/GizmoGridDrawer.cs:52-82）。点击发生在该帧中段，滑条实例的
    /// targetValuePct 还是点击前读到的旧值，Repaint 时会把旧值写回来，点击即被撤销。
    ///
    /// 只对同时挂了 GC_ThingComp 的通风口生效；纯 AO 通风建筑完全不受影响。
    /// </summary>
    [HarmonyPatch]
    internal static class GcNaturalVentSpeedPatch
    {
        private static bool Prepare()
        {
            return GcAirRelay.AirVentType != null
                && AccessTools.Method(GcAirRelay.AirVentType, "SetSpeedLimit", new[] { typeof(float) }) != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(GcAirRelay.AirVentType, "SetSpeedLimit", new[] { typeof(float) });
        }

        private static bool Prefix(ThingComp __instance, ref float __0)
        {
            Building building = __instance == null ? null : __instance.parent as Building;
            if (building == null || !GcAirRelay.HasGcComp(building))
            {
                return true;
            }

            // 主动通风运行中：滑条锁死在 0（AO 自己的守卫会跳过重复写入）。
            if (GcAirRelay.IsIntensiveActive(building))
            {
                __0 = 0f;
                return true;
            }

            // 按钮刚接管了这一帧：丢弃滑条的写回。
            if (GcAirRelay.IsSliderWriteSuppressed(building))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 通风口拆除时把该格残留风速抵消为 0。
    ///
    /// AO 自己的 Thing.DeSpawn Prefix 只做 MarkCachedMapDataDirty，不会清速度场；
    /// 而拆除后 ClampVentVelocities 不再覆盖该格，残留速度会一直留在那里。
    /// AO 的 Building.DeSpawn 会先调用 base(Thing.DeSpawn)，因此这里拦截得到。
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    internal static class GcVentDespawnPatch
    {
        private static bool Prepare()
        {
            return GcAirRelay.AirVentType != null;
        }

        private static void Prefix(Thing __instance)
        {
            Building building = __instance as Building;
            if (building != null)
            {
                GcAirRelay.ClearVentVelocity(building);
            }
        }
    }

    /// <summary>
    /// 通风口一建造出来就关闭自然通风，不等 60 tick 扫描。
    ///
    /// patch 的是 Building.SpawnSetup（override），普通建筑（含 Building_Vent）都走这里；
    /// 少数再 override 的建筑会由扫描兜底。
    /// </summary>
    [HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    internal static class GcVentSpawnPatch
    {
        private static bool Prepare()
        {
            return GcAirRelay.AirVentType != null;
        }

        private static void Postfix(Building __instance)
        {
            // 建造任何建筑（含墙）都会走 Building.SpawnSetup；先按 def 过滤，
            // 避免对每一块墙都做一次 AllComps 遍历 + 反射字段读取。
            if (__instance == null || __instance.def == null
                || !__instance.def.HasComp(GcAirRelay.AirVentType))
            {
                return;
            }

            GcAirRelay.EnsureDefaultClosed(__instance);
        }
    }

    /// <summary>
    /// 每模拟周期在 AO 完成本步输入准备后，做一次成对温度注入：
    /// 出口 +delta、入口 -delta。
    ///
    /// 喷流的喷出温度被 AO 钳在 max(室外温度, 入口邻格均温) 以上，冷端搬不过去；
    /// 这里改用公开的 AddTemperatureDelta 直接写两端，既没有室外下限，也不参与整格混合。
    /// </summary>
    [HarmonyPatch]
    internal static class GcTemperatureRelayPatch
    {
        private static Type ComponentType()
        {
            return AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
        }

        private static bool Prepare()
        {
            Type type = ComponentType();
            return type != null && AccessTools.Method(type, "PrepareTemperatureSources") != null;
        }

        private static MethodBase TargetMethod()
        {
            Type type = ComponentType();
            return type == null ? null : AccessTools.Method(type, "PrepareTemperatureSources");
        }

        private static void Postfix(MapComponent __instance)
        {
            if (__instance != null)
            {
                GcAirRelay.RelayTemperature(__instance.map);
            }
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
                Log.Message("[AO Patch][GC] 主动通风口定向喷流接管已启用。");
            }
            catch (Exception e)
            {
                Log.Error("[AO Patch][GC] Harmony 初始化失败：" + e);
            }
        }
    }
}
