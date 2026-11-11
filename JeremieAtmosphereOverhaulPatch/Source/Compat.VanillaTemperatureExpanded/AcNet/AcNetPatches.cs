using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using PipeSystem;
using RimWorld;
using VanillaTemperatureExpanded;
using VanillaTemperatureExpanded.Buildings;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.VanillaTemperatureExpanded.AcNet
{
    /// <summary>
    /// VTE 中央空调三管式 VRF 的接管补丁。
    ///
    /// 三层短路：
    /// 1. Building_AcUnit.TickRare：原方法只对自己那一格做 room.Temperature +=，
    ///    在 AO 地图上会被翻译成「每格 +a」，且完全不参与热回收。改由账本统一写槽位。
    /// 2. AcPipeNet.PipeSystemTick：VTE 只允许恰好 1 个控制器，且把 Efficiency 只当电费乘数。
    ///    本 mod 要求「至少 1 个控制器」，Efficiency 改成满足率，必须整体接管。
    /// 3. CompTempControlAtmosphere.CompTickRare：AO 组件仍会被 ThingWithComps.TickRare 调到，
    ///    不挡住会用 AO 的单向默认逻辑覆盖账本刚写好的槽位。
    /// </summary>
    internal static class AcNetPatches
    {
        /// <summary>安装全部补丁。任一关键目标缺失都返回 false，由调用方停用整个模块。</summary>
        public static bool Install(Harmony harmony)
        {
            if (harmony == null)
            {
                return false;
            }

            MethodInfo tickRare = AccessTools.Method(typeof(Building_AcUnit), "TickRare", Type.EmptyTypes);
            MethodInfo pipeTick = AccessTools.Method(typeof(AcPipeNet), "PipeSystemTick", Type.EmptyTypes);
            if (tickRare == null || pipeTick == null)
            {
                return false;
            }

            // 必须抢在 AO 的 TempControlSubclassPatches 之前，否则 AO 的 Prefix 会先把
            // CompTempControlAtmosphere.Run 跑一遍。Priority.High (600) > Normal (400)。
            HarmonyMethod unitPrefix = new HarmonyMethod(typeof(AcNetPatches), "AcUnitTickRarePrefix");
            unitPrefix.priority = Priority.High;
            harmony.Patch(tickRare, unitPrefix);

            harmony.Patch(pipeTick, new HarmonyMethod(typeof(AcNetPatches), "PipeSystemTickPrefix"));

            // AO 的组件不在本程序集，按名字找；找不到就只失去「防覆盖」这一层，
            // 账本仍然会在下一 tick 把槽位写回来，不至于停摆。
            Type aoCompType = AccessTools.TypeByName("AtmosphereOverhaul.Buildings.CompTempControlAtmosphere");
            if (aoCompType != null)
            {
                MethodInfo aoTickRare = AccessTools.Method(aoCompType, "CompTickRare", Type.EmptyTypes);
                if (aoTickRare != null)
                {
                    harmony.Patch(aoTickRare, new HarmonyMethod(typeof(AcNetPatches), "AoCompTickRarePrefix"));
                }
            }

            // 账本随 AO 的模拟步推进（默认 30 tick 一步），而不是每 250 tick 抽一次。
            // 与 CCC 的 AirLoopStepPatch 同一入口：CompleteFinishedSimulation 前后
            // lastSynchronizedSimulationTick 的差值就是本次模拟实际推进的 tick 数。
            bool stepPatched = false;
            Type aoMapType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
            if (aoMapType != null)
            {
                FieldInfo tickField = AccessTools.Field(aoMapType, "lastSynchronizedSimulationTick");
                MethodInfo complete = AccessTools.Method(aoMapType, "CompleteFinishedSimulation");
                if (tickField != null && complete != null)
                {
                    _simulationTickField = tickField;
                    harmony.Patch(
                        complete,
                        new HarmonyMethod(typeof(AcNetPatches), "SimulationStepPrefix"),
                        new HarmonyMethod(typeof(AcNetPatches), "SimulationStepPostfix"));
                    stepPatched = true;
                }
            }

            // UI：让 inspect 文本与新语义一致（满足率、台当量、按热负荷计费）。
            AcNetInspectPatches.Install(harmony);

            Log.Message("[AOPatch/VTE] 中央空调三管式 VRF 已接管：Building_AcUnit.TickRare + AcPipeNet.PipeSystemTick"
                        + (stepPatched ? " + AO 模拟步推进" : "（未找到 CompleteFinishedSimulation，账本停摆）")
                        + (aoCompType != null ? " + AO 组件防覆盖" : "（未找到 AO 温控组件类型，防覆盖层缺失）")
                        + " + inspect");
            return true;
        }

        public static bool AcUnitTickRarePrefix(Building_AcUnit __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            if (!AoBridge.Available)
            {
                FallbackOnce("AoBridge 不可用：" + AoBridge.Failure);
                return true;
            }

            Map map = __instance.Map;
            if (!AoBridge.IsActive(map))
            {
                // 非 AO 地图：让 VTE 原逻辑继续跑，别把普通存档也改掉。
                return true;
            }

            AcPipeNet net = __instance.AcPipeNet;
            if (net == null)
            {
                return true;
            }

            AcNetLedger ledger = AcNetLedgerManager.Get(net);
            if (ledger == null)
            {
                return true;
            }

            // 温度与风由「模拟步账本」统一写进 AO 连续源，原 TickRare 不再需要写房间温度。
            // 账本由 AO 的 CompleteFinishedSimulation 回调推进，不在这里跑。
            return false;
        }

        private static FieldInfo _simulationTickField;

        private static bool _fallbackLogged;

        private static void FallbackOnce(string reason)
        {
            if (_fallbackLogged)
            {
                return;
            }
            _fallbackLogged = true;
            Log.Warning("[AOPatch/VTE] 中央空调回退原逻辑：" + reason);
        }

        public static bool PipeSystemTickPrefix(AcPipeNet __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            if (!AoBridge.Available || !AoBridge.IsActive(__instance.map))
            {
                return true;
            }

            AcNetLedgerManager.Cleanup(Find.TickManager.TicksGame);
            // VTE 的控制器门控（== 1）与 Efficiency 公式都被账本取代。
            return false;
        }

        public static bool AoCompTickRarePrefix(ThingComp __instance)
        {
            if (__instance == null)
            {
                return true;
            }

            // VTE 空调的 AO 温控组件不再自行换热，避免覆盖账本槽位。
            return !(__instance.parent is Building_AcUnit);
        }

        /// <summary>记录本次模拟开始前的同步 tick，Postfix 里算差值得到真实 dt。</summary>
        public static void SimulationStepPrefix(object __instance, out int __state)
        {
            __state = _simulationTickField != null ? (int)_simulationTickField.GetValue(__instance) : 0;
        }

        /// <summary>AO 完成一次模拟后，按真实 dt 推进一步所有 AcPipeNet 的账本。</summary>
        public static void SimulationStepPostfix(object __instance, int __state)
        {
            if (_simulationTickField == null)
            {
                return;
            }

            int now = (int)_simulationTickField.GetValue(__instance);
            int dtTicks = now - __state;
            if (dtTicks <= 0)
            {
                return;
            }

            MapComponent component = __instance as MapComponent;
            if (component == null)
            {
                return;
            }

            Map map = component.map;
            if (map == null || !AoBridge.IsActive(map))
            {
                return;
            }

            PipeNetManager manager = map.GetComponent<PipeNetManager>();
            if (manager == null)
            {
                return;
            }

            List<PipeNet> nets = manager.pipeNets;
            for (int i = 0; i < nets.Count; i++)
            {
                AcPipeNet net = nets[i] as AcPipeNet;
                if (net == null)
                {
                    continue;
                }

                AcNetLedger ledger = AcNetLedgerManager.Get(net);
                if (ledger != null)
                {
                    ledger.Run(net, dtTicks);
                }
            }
        }
    }
}
