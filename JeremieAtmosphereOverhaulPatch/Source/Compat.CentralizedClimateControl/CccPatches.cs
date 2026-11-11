using System;
using System.Reflection;
using CentralizedClimateControl;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.CentralizedClimateControl
{
    /// <summary>
    /// 把 CCC 三个建筑自己的 TickRare 关掉，改由气袋在 AO 步里统一推进。
    ///
    /// 只关「写场」的部分：
    ///   - Building_IntakeFan.TickRare   读八邻温度、算风量，随后被气袋取代；
    ///   - Building_AirVent.TickRare     整房间 Room.Temperature += a，与 AO 双写；
    ///   - Building_AirThermal.TickRare  尾部向南侧 PushHeat，与 AO 双写。
    ///
    /// 保留 CompAirFlowConsumer.TickRare：它只读网络均值、处理优先级变更，不写场。
    /// </summary>
    [HarmonyPatch(typeof(Building_IntakeFan), "TickRare")]
    public static class IntakeFanTickRarePatch
    {
        public static bool Prefix()
        {
            // 风机状态（电源、挡格、故障）由 AirLoopMapComponent 维护，
            // 这里只阻止原版再算一遍风量并写 CompAirFlowProducer 的字段。
            return false;
        }
    }

    [HarmonyPatch(typeof(Building_AirVent), "TickRare")]
    public static class AirVentTickRarePatch
    {
        public static bool Prefix(Building_AirVent __instance)
        {
            CompAirFlowConsumer consumer = __instance.CompAirFlowConsumer;
            if (consumer != null)
            {
                consumer.TickRare();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Building_AirThermal), "TickRare")]
    public static class AirThermalTickRarePatch
    {
        public static bool Prefix()
        {
            return false;
        }
    }

    /// <summary>
    /// 在 AO 完成一次模拟、把结果同步回主线程之后推进气袋。
    /// 用 lastSynchronizedSimulationTick 的差值得到真实 dt —— AO 未跑完时会累加到 12/18/24 tick。
    /// </summary>
    [HarmonyPatch]
    public static class AirLoopStepPatch
    {
        private static Type _targetType;
        private static FieldInfo _synchronizedTickField;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("AtmosphereOverhaul.RimWorld.AtmosphereMapComponent");
            if (_targetType == null)
            {
                return false;
            }
            _synchronizedTickField = AccessTools.Field(_targetType, "lastSynchronizedSimulationTick");
            if (_synchronizedTickField == null)
            {
                return false;
            }
            // 方法缺失时只跳过这一条补丁，不能连累三个 TickRare 拦截。
            return AccessTools.Method(_targetType, "CompleteFinishedSimulation") != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(_targetType, "CompleteFinishedSimulation");
        }

        public static void Prefix(object __instance, out int __state)
        {
            __state = (int)_synchronizedTickField.GetValue(__instance);
        }

        public static void Postfix(object __instance, int __state)
        {
            int now = (int)_synchronizedTickField.GetValue(__instance);
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

            AirLoopMapComponent loops = component.map.GetComponent<AirLoopMapComponent>();
            if (loops == null)
            {
                return;
            }

            loops.StepAll(dtTicks / 60f);
        }
    }
}
