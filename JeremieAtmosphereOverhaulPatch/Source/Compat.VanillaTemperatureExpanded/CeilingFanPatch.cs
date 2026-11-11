using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded.AcNet;
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
            // 无条件日志：先确认这个程序集本身有没有被游戏加载、这个类有没有被扫描到。
            // 排查「有风无温度」时日志里连吊扇补丁的痕迹都没有，
            // 要么 DLL 没加载，要么安装逻辑被静默跳过。
            Log.Message("[AOPatch/VTE] 兼容程序集已加载。");

            // RimUnity 的 DeferredModInitialization 可能在本类之后才实例化 AOPatchMod，
            // 那时 Instance / HarmonyInstance 还是 null。等到加载完成再装，顺序就稳了。
            if (AOPatchMod.Instance == null || AOPatchMod.HarmonyInstance == null)
            {
                Log.Message("[AOPatch/VTE] 宿主尚未就绪，延后到加载完成再安装。");
                LongEventHandler.ExecuteWhenFinished(Install);
                return;
            }

            Install();
        }

        /// <summary>真正安装补丁。可能由 cctor 直接调用，也可能由 LongEventHandler 延后调用。</summary>
        private static void Install()
        {
            if (AOPatchMod.Instance == null)
            {
                Log.Error("[AOPatch/VTE] 安装时 AOPatchMod.Instance 仍为 null，VTE 兼容模块停用。");
                return;
            }
            if (AOPatchMod.Settings != null && !AOPatchMod.Settings.MasterEnabled)
            {
                return;
            }

            Harmony harmony = AOPatchMod.HarmonyInstance;
            if (harmony == null)
            {
                Log.Error("[AOPatch/VTE] AOPatchMod.HarmonyInstance 为 null，VTE 兼容模块停用。");
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

            // VTE 中央空调（冷媒网）的三管式 VRF 接管。失败只停这个子模块，吊扇仍可用。
            if (!AcNetPatches.Install(harmony))
            {
                Log.Warning("[AOPatch/VTE] 中央空调接管安装失败：找不到 Building_AcUnit.TickRare 或 AcPipeNet.PipeSystemTick。");
            }
        }
    }
}
