using System;
using System.Reflection;
using CentralizedClimateControl;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.CentralizedClimateControl
{
    /// <summary>
    /// 只关两处「写场」的 TickRare，其余交给原版：
    ///   - Building_IntakeFan.TickRare   它会用八邻域重算 CurrentAirFlow 并覆盖 IsBlocked，
    ///                                   必须拦住，否则气袋回填的风量每 250 tick 被清零；
    ///   - Building_AirThermal.TickRare  它用单格 South 判定重写 IsBlocked，尾部还 PushHeat，
    ///                                   必须拦住，否则温控状态被原版覆盖。
    ///   - Building_AirVent.TickRare     它内部是 roomOrAdjacent.Temperature += ...，
    ///                                   AO 会把这条写入摊到房间每一格（ApplyRoomTemperatureWrite），
    ///                                   也就是整房同时升温。本项目要的是「从出风口向外蔓延」，
    ///                                   所以不能放行，改为由 AirLoopMapComponent 在出风口附近
    ///                                   写一个径向递减的连续热源，让 AO 的平流与扩散把它带开。
    ///                                   这里仍要调用 CompAirFlowConsumer.TickRare，否则
    ///                                   ConvertedTemperature 不再刷新。
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
            // 只保留「刷新 ConvertedTemperature / 处理优先级变更」这一半，
            // 写温度与写体积由 AirLoopMapComponent 负责。
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
