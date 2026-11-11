using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using RimWorld;
using UnityEngine;
using VanillaTemperatureExpanded.Buildings;
using VanillaTemperatureExpanded.Comps;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded
{
    /// <summary>
    /// 把 CompCeilingFan.CompTick 换成 AO 连续源版本。
    ///
    /// 必须拦 CompTick 本体而不是基类 CompHeatPusher.CompTick：
    /// CompCeilingFan 用的是 override（不是 new），Harmony 对基类方法打的 Prefix
    /// 只覆盖基类实现，虚调用会走到子类，拦不到。
    /// 同理 AO 的 CompHeatPusher_CompTick_SourcePatch 也拦不到它，
    /// 所以吊扇在 AO 地图上一直走原版 PushHeat 路径。
    /// </summary>
    public static class CeilingFanPatch
    {
        public static bool Prefix(CompCeilingFan __instance)
        {
            if (__instance == null || __instance.parent == null)
            {
                return true;
            }

            CompCeilingFanAtmosphere comp = __instance.parent.GetComp<CompCeilingFanAtmosphere>();
            if (comp == null)
            {
                return true;
            }

            if (!comp.Tick(__instance))
            {
                return true;
            }

            Spin(__instance);
            return false;
        }

        /// <summary>照抄原版 CompTick 的视觉部分：转速按高/低功率档增减，扇叶累加角度。</summary>
        private static void Spin(CompCeilingFan fan)
        {
            CompPowerTrader power = fan.parent.GetComp<CompPowerTrader>();
            if (power == null || !power.PowerOn)
            {
                return;
            }

            CompProperties_CeilingFan props = fan.Props;
            float offset = props.maxSpinRate / 180f;

            if (fan.ShouldPushHeatNow)
            {
                fan.spinRate = Mathf.Min(props.maxSpinRate, fan.spinRate + offset);
            }
            else
            {
                fan.spinRate = Mathf.Max(props.minSpinRate, fan.spinRate - offset);
            }

            Building_CeilingFan building = fan.parent as Building_CeilingFan;
            if (building != null)
            {
                building.angle += fan.spinRate;
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class VanillaTemperatureExpandedBootstrap
    {
        static VanillaTemperatureExpandedBootstrap()
        {
            if (AOPatchMod.Instance == null)
            {
                return;
            }
            if (AOPatchMod.Settings != null && !AOPatchMod.Settings.MasterEnabled)
            {
                return;
            }

            Harmony harmony = AOPatchMod.HarmonyInstance;
            if (harmony == null)
            {
                return;
            }

            AoBridge.Resolve();
            if (!AoBridge.Available)
            {
                Log.Warning("[AOPatch/VTE] AO 连续源 API 不完整，吊扇模块停用。原因：" + AoBridge.Failure);
                return;
            }

            harmony.Patch(
                AccessTools.Method(typeof(CompCeilingFan), "CompTick"),
                new HarmonyMethod(typeof(CeilingFanPatch), "Prefix"));
        }
    }
}
