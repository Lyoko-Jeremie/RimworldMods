using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using PipeSystem;
using RimWorld;
using VanillaTemperatureExpanded;
using VanillaTemperatureExpanded.Buildings;
using VanillaTemperatureExpanded.Comps;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded.AcNet
{
    /// <summary>
    /// 把 VTE 三个部件的 inspect 文本改成三管式 VRF 的语义。
    ///
    /// 原版把 AcPipeNet.Efficiency 当作「管网损耗」，只影响耗电；
    /// 三管式账本把它改写成「室外机满足率 s」——不平衡量能被室外机吃掉多少。
    /// 电费也改成按需求热负荷计，所以界面必须同步，否则玩家看到的「工作效率」与实际耗电对不上。
    ///
    /// 单位统一用「台当量」：1 台当量 = 单台满额室内机在当前模拟步的输出，
    /// 与 Def 里压缩机「可支持 2 台 / 6 台」的写法同源。
    /// </summary>
    internal static class AcNetInspectPatches
    {
        public static void Install(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            MethodInfo acInspect = AccessTools.Method(
                typeof(CompResourceTrader_AC), "CompInspectStringExtra", Type.EmptyTypes);
            if (acInspect != null)
            {
                harmony.Patch(acInspect, new HarmonyMethod(
                    typeof(AcNetInspectPatches), "AcUnitInspectPrefix"));
            }

            MethodInfo controllerInspect = AccessTools.Method(
                typeof(CompResourceSingleton), "CompInspectStringExtra", Type.EmptyTypes);
            if (controllerInspect != null)
            {
                harmony.Patch(controllerInspect, new HarmonyMethod(
                    typeof(AcNetInspectPatches), "ControllerInspectPrefix"));
            }

            MethodInfo compressorInspect = AccessTools.Method(
                typeof(CompResourceTrader_Compressor), "CompInspectStringExtra", Type.EmptyTypes);
            if (compressorInspect != null)
            {
                harmony.Patch(compressorInspect, new HarmonyMethod(
                    typeof(AcNetInspectPatches), "CompressorInspectPrefix"));
            }
        }

        /// <summary>室内空调：满足率 + 本机负荷率。</summary>
        private static bool AcUnitInspectPrefix(CompResourceTrader_AC __instance, ref string __result)
        {
            if (__instance == null || __instance.parent == null || !__instance.parent.Spawned)
            {
                return true;
            }

            AcPipeNet net = __instance.AcPipeNet;
            if (net == null)
            {
                return true;
            }

            // 未运行时保留 VTE 原版的「缺控制器 / 缺压缩机 / 控制器过多」提示。
            if (!__instance.ResourceOn)
            {
                return true;
            }

            StringBuilder sb = new StringBuilder(128);
            sb.AppendLine(L("JAP.ACNet.Satisfaction") + ": " + GenText.ToStringPercent(net.Efficiency));

            AcNetLedger ledger = AcNetLedgerManager.Get(net);
            Building_AcUnit unit = __instance.parent as Building_AcUnit;
            float drive;
            if (ledger != null && unit != null && ledger.TryGetUnitDrive(unit, out drive))
            {
                sb.AppendLine(L("JAP.ACNet.LoadRatio") + ": " + GenText.ToStringPercent(drive));
            }

            __result = sb.ToString().Trim();
            return false;
        }

        /// <summary>中控器：目标温度、满足率、室内机与压缩机的台当量、网内回收与净负荷。</summary>
        private static bool ControllerInspectPrefix(CompResourceSingleton __instance, ref string __result)
        {
            if (__instance == null || __instance.parent == null || !__instance.parent.Spawned)
            {
                return true;
            }

            AcPipeNet net = __instance.AcPipeNet;
            if (net == null || !__instance.ResourceOn)
            {
                return true;
            }

            Building_AcControlUnit controller = __instance.parent as Building_AcControlUnit;
            AcNetLedger ledger = AcNetLedgerManager.Get(net);

            StringBuilder sb = new StringBuilder(192);
            if (controller != null)
            {
                sb.AppendLine(L("JAP.ACNet.NetworkTemp") + ": "
                              + GenText.ToStringTemperature(controller.TargetNetworkTemperature, "F0"));
            }

            sb.AppendLine(L("JAP.ACNet.Satisfaction") + ": " + GenText.ToStringPercent(net.Efficiency));

            if (ledger != null && ledger.UnitReference > 0f)
            {
                float unitRef = ledger.UnitReference;
                sb.AppendLine(L("JAP.ACNet.IndoorUnits") + ": " + ledger.UnitCount + " " + L("JAP.ACNet.Unit")
                              + " / " + L("JAP.ACNet.Demand") + " "
                              + (ledger.LastDemand / unitRef).ToString("F1"));
                sb.AppendLine(L("JAP.ACNet.Compressors") + ": " + ledger.CompressorCount + " " + L("JAP.ACNet.Unit")
                              + " / " + L("JAP.ACNet.Capacity") + " "
                              + (ledger.LastCapacity / unitRef).ToString("F1"));
                sb.AppendLine(L("JAP.ACNet.Recovered") + ": " + (ledger.LastRecovered / unitRef).ToString("F1"));
                sb.AppendLine(L("JAP.ACNet.NetLoad") + ": " + (ledger.LastNet / unitRef).ToString("F1"));
                sb.AppendLine(L("JAP.ACNet.OutdoorLoad") + ": " + GenText.ToStringPercent(ledger.LastLoadRatio));
            }

            __result = sb.ToString().Trim();
            return false;
        }

        /// <summary>压缩机：满足率、本机容量、均摊到的净负荷。</summary>
        private static bool CompressorInspectPrefix(CompResourceTrader_Compressor __instance, ref string __result)
        {
            if (__instance == null || __instance.parent == null || !__instance.parent.Spawned)
            {
                return true;
            }

            AcPipeNet net = __instance.AcPipeNet;
            if (net == null || !__instance.ResourceOn)
            {
                return true;
            }

            AcNetLedger ledger = AcNetLedgerManager.Get(net);

            StringBuilder sb = new StringBuilder(128);
            sb.AppendLine(L("JAP.ACNet.Satisfaction") + ": " + GenText.ToStringPercent(net.Efficiency));

            float units = -__instance.Props.consumptionPerTick;
            sb.AppendLine(L("JAP.ACNet.Capacity") + ": " + units.ToString("F0") + " " + L("JAP.ACNet.Unit"));

            if (ledger != null)
            {
                // 真实出力比例：loadRatio 同时是电费依据（PowerOutput = -PowerConsumption * loadRatio）。
                sb.AppendLine(L("JAP.ACNet.Workload") + ": " + GenText.ToStringPercent(ledger.LastLoadRatio));

                if (ledger.CompressorCount > 0 && ledger.UnitReference > 0f)
                {
                    float share = ledger.LastNet / ledger.UnitReference / ledger.CompressorCount;
                    sb.AppendLine(L("JAP.ACNet.Borne") + ": " + share.ToString("F1") + " " + L("JAP.ACNet.Unit"));
                }
            }

            __result = sb.ToString().Trim();
            return false;
        }

        /// <summary>取翻译文本；缺 key 时返回 key 本身，便于在日志里发现漏翻。</summary>
        private static string L(string key)
        {
            return Translator.Translate(key);
        }
    }
}
