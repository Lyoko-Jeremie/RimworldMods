using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Bridge;
using PipeSystem;
using RimWorld;
using VanillaTemperatureExpanded;
using VanillaTemperatureExpanded.Buildings;
using VanillaTemperatureExpanded.Comps;
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

            // 图标纠正 + 目标温度同步：VTE 在管网合并/拆分时（AcPipeNet.RegisterComp/UnregisterComp）
            // 会直接按「当时」的 Efficiency 写 PipeNetOverlayDrawer，绕过 AcNetOverlay 的 _lastState 缓存；
            // 合并那一刻被并入的网往往还是账本给「无控制器网」写下的 0，于是中控器图标被点亮。
            // 同时这两个钩子是「机器刚刚进入一张网」的唯一可靠时机，在这里把中控器的网络温度
            // 补写进新机器的 targetTemperature（VTE 的 push 广播会漏掉它们）。
            //
            // 两个钩子必须拆成两个 Postfix：UnregisterComp 时 comp.PipeNet 不会被清空，
            // 而 PipeNet.Destroy 是边遍历 connectors 边调 UnregisterComp 的半拆状态，
            // 在那里同步温度会读到即将解散的网。
            bool overlayPatched = false;
            MethodInfo registerComp = AccessTools.Method(typeof(AcPipeNet), "RegisterComp");
            MethodInfo unregisterComp = AccessTools.Method(typeof(AcPipeNet), "UnregisterComp");
            MethodInfo singletonDeSpawn = AccessTools.Method(typeof(CompResourceSingleton), "PostDeSpawn");
            if (registerComp != null && unregisterComp != null)
            {
                harmony.Patch(registerComp, postfix: new HarmonyMethod(typeof(AcNetPatches), "ControllerRegisteredPostfix"));
                harmony.Patch(unregisterComp, postfix: new HarmonyMethod(typeof(AcNetPatches), "ControllerUnregisteredPostfix"));
                overlayPatched = true;
            }
            if (singletonDeSpawn != null)
            {
                harmony.Patch(singletonDeSpawn, postfix: new HarmonyMethod(typeof(AcNetPatches), "ControllerDeSpawnPostfix"));
            }

            // AO 的组件不在本程序集，按名字找；找不到就只失去「防覆盖」这一层，
            // 账本仍然会在下一 tick 把槽位写回来，不至于停摆。
            //
            // Run 才是唯一换热门：CompTickRare 和 AO 自己的 TempControlSubclassPatches.Prefix
            // 最终都走 RunTempControl -> Run。Harmony 里高优先级 Prefix 返回 false 只跳过原方法，
            // 低优先级 Prefix 仍会执行，所以只靠 Priority.High 拦不住 AO 的 Prefix，
            // 必须直接拦在 Run 上（见 AoRunPrefix）。
            Type aoCompType = AccessTools.TypeByName("AtmosphereOverhaul.Buildings.CompTempControlAtmosphere");
            bool runPatched = false;
            if (aoCompType != null)
            {
                MethodInfo aoTickRare = AccessTools.Method(aoCompType, "CompTickRare", Type.EmptyTypes);
                if (aoTickRare != null)
                {
                    harmony.Patch(aoTickRare, new HarmonyMethod(typeof(AcNetPatches), "AoCompTickRarePrefix"));
                }

                MethodInfo aoRun = AccessTools.Method(aoCompType, "Run", new[] { typeof(Building_TempControl) });
                if (aoRun != null)
                {
                    _aoSlotsField = AccessTools.Field(aoCompType, "slots");
                    if (_aoSlotsField != null)
                    {
                        harmony.Patch(aoRun, new HarmonyMethod(typeof(AcNetPatches), "AoRunPrefix"));
                        runPatched = true;
                    }
                }
            }

            // 账本随 AO 的模拟步推进（默认 30 tick 一步），而不是每 250 tick 抽一次。
            // 优先订阅 StepApplied：elapsedTicks 由 AO 直接给出，不依赖内部字段，
            // 也不会因为补丁顺序出现偏差；存档期间 AO 不触发，符合「不扰动存档」。
            bool stepPatched = AoBridge.SubscribeStepApplied(OnStepApplied);
            if (!stepPatched)
            {
                // 回退路径：AO 版本没有 StepApplied 时维持旧写法。
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
            }

            // UI：让 inspect 文本与新语义一致（满足率、台当量、按热负荷计费）。
            AcNetInspectPatches.Install(harmony);

            Log.Message("[AOPatch/VTE] 中央空调三管式 VRF 已接管：Building_AcUnit.TickRare + AcPipeNet.PipeSystemTick"
                        + (stepPatched ? " + AO 模拟步推进" : "（未找到 CompleteFinishedSimulation，账本停摆）")
                        + (aoCompType != null ? " + AO 组件防覆盖" : "（未找到 AO 温控组件类型，防覆盖层缺失）")
                        + (runPatched ? " + AO Run 拦截" : "（未找到 AO Run，背面风可能残留）")
                        + (overlayPatched ? " + 图标纠正 + 目标温度同步" : "（未找到 RegisterComp/UnregisterComp，图标与温度同步均缺失）")
                        + " + inspect + " + AcNetOverlay.Describe());
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

        /// <summary>AO 温控组件的私有槽位容器，用于清理 RunCooler 可能留在 slot1..3 的热侧源。</summary>
        private static FieldInfo _aoSlotsField;
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

        /// <summary>
        /// 机器刚被注册进一张网（新建、合并、拆分后重建都会走这里）时的收尾：
        /// 1. 控制器（singleton）：作废图标缓存；若它是中控器，再把它这一刻的网络温度
        ///    补写给网内所有联动室内机（可能它比室内机更晚注册）。
        /// 2. 室内机：直接向本网中控器取一次温度写进字段。
        ///
        /// 为什么必须在这里同步：VTE 的 push 模型只在「玩家改温那一刻」广播给当时在网的机器，
        /// 之后进网的机器永远收不到；而 AcPipeNet/CompResource 的 PostSpawnSetup 顺序又保证
        /// CompAcTempControl.PostSpawnSetup 早于 RegisterComp（PipeNet 还是 null），
        /// 于是它自己的初始化分支必然早退。这里是唯一的可靠时机。
        /// </summary>
        public static void ControllerRegisteredPostfix(CompResource __0)
        {
            if (__0 == null || __0.parent == null)
            {
                return;
            }

            AcPipeNet net = __0.PipeNet as AcPipeNet;
            if (net == null)
            {
                return;
            }

            CompResourceSingleton singleton = __0 as CompResourceSingleton;
            if (singleton != null)
            {
                AcNetOverlay.Forget(__0.parent.thingIDNumber);
                Building_AcControlUnit controller = __0.parent as Building_AcControlUnit;
                if (controller != null)
                {
                    SyncNetworkTarget(controller, net);
                }
                return;
            }

            SyncUnitTarget(__0.parent as Building_AcUnit, net);
        }

        /// <summary>
        /// 机器被移出管网时的收尾。这里只作废图标缓存，绝不同步温度：
        /// <c>PipeNet.UnregisterComp</c> 不会清空 <c>comp.PipeNet</c>，而 <c>PipeNet.Destroy</c>
        /// 是边遍历 connectors 边调用本钩子的半拆状态，此时读网内控制器/室内机都是残余数据。
        /// </summary>
        public static void ControllerUnregisteredPostfix(CompResource __0)
        {
            CompResourceSingleton singleton = __0 as CompResourceSingleton;
            if (singleton == null || singleton.parent == null)
            {
                return;
            }

            AcNetOverlay.Forget(singleton.parent.thingIDNumber);
        }

        /// <summary>把中控器当前的网络温度写给本网所有联动室内机。</summary>
        private static void SyncNetworkTarget(Building_AcControlUnit controller, AcPipeNet net)
        {
            if (controller == null || net == null)
            {
                return;
            }

            // 多控制器时用账本会选中的那一台，而不是「刚注册的这台」：
            // 否则新建的中控器把自己尚未设置的默认温度写进全网字段，
            // 面板显示与账本（取 FindController 的第一个）就会不一致。
            Building_AcControlUnit effective = AcNetLedger.FindController(net);
            if (effective == null || !effective.Spawned)
            {
                return;
            }

            Map map = effective.Map;
            if (map == null || !AoBridge.IsActive(map))
            {
                return;
            }

            float networkTarget = effective.TargetNetworkTemperature;
            List<CompResourceTrader> receivers = net.receivers;
            for (int i = 0; i < receivers.Count; i++)
            {
                CompResourceTrader trader = receivers[i];
                if (trader == null)
                {
                    continue;
                }

                WriteUnitTarget(trader.parent as Building_AcUnit, networkTarget);
            }
        }

        /// <summary>让某台室内机向本网中控器取一次温度。</summary>
        private static void SyncUnitTarget(Building_AcUnit unit, AcPipeNet net)
        {
            if (unit == null || net == null || !unit.Spawned)
            {
                return;
            }

            Map map = unit.Map;
            if (map == null || !AoBridge.IsActive(map))
            {
                return;
            }

            Building_AcControlUnit controller = AcNetLedger.FindController(net);
            if (controller == null)
            {
                return;
            }

            WriteUnitTarget(unit, controller.TargetNetworkTemperature);
        }

        /// <summary>
        /// 把网络温度写进某台室内机的 targetTemperature 字段（只写联动机，独立机不动）。
        /// 与账本共用 <see cref="AcNetLedger.ResolveTarget"/>，保证面板与出风同源。
        /// </summary>
        private static void WriteUnitTarget(Building_AcUnit unit, float networkTarget)
        {
            if (unit == null || !unit.Spawned)
            {
                return;
            }

            CompTempControl control = AcNetLedger.GetTempControl(unit);
            if (control == null)
            {
                return;
            }

            float target = AcNetLedger.ResolveTarget(unit, networkTarget);
            if (control.TargetTemperature != target)
            {
                control.TargetTemperature = target;
            }
        }

        /// <summary>控制器被拆除/销毁后清账，避免 _lastState 随拆建无限增长。</summary>
        public static void ControllerDeSpawnPostfix(CompResourceSingleton __instance)
        {
            if (__instance == null || __instance.parent == null)
            {
                return;
            }

            AcNetOverlay.Forget(__instance.parent.thingIDNumber);
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

        /// <summary>
        /// AO 的唯一换热门。两条路径最终都落到这里：
        /// CompTempControlAtmosphere.CompTickRare（组件 tick）和
        /// TempControlSubclassPatches.Prefix -> FacilityPatchUtility.RunTempControl（Building_AcUnit.TickRare 的 AO Prefix）。
        ///
        /// 后者拦不住：Harmony 里我们高优先级的 Prefix 返回 false 只跳过原方法，
        /// AO 那个没有 __runOriginal 参数的 Prefix 仍会执行 RunTempControl，把 RunCooler 跑一遍。
        ///
        /// 也不能给「未接入管网」的空调放行：AO 的 Run 只看 compPowerTrader.PowerOn、不看
        /// resourceComp.ResourceOn，放行等于给一台没有接管的空调凭空造冷热；VTE 本身要求接入管网才工作。
        /// </summary>
        public static bool AoRunPrefix(ThingComp __instance, Building_TempControl b)
        {
            if (!(b is Building_AcUnit))
            {
                return true;
            }

            if (_aoSlotsField != null)
            {
                object slots = _aoSlotsField.GetValue(__instance);
                if (slots != null)
                {
                    // AO 之前可能已经把热侧源写到 slot1..3（贴墙机的墙外那格会真的生效），
                    // 这里清掉防止残留源继续被每步应用；slot0 留给账本，下次模拟步会重写。
                    AoBridge.ZeroFrom(slots, b, 1);
                }
            }

            return false;
        }

        /// <summary>
        /// StepApplied 回调：AO 主线程、一步完成并发布快照后。
        /// elapsedTicks 是这一步实际覆盖的游戏 tick 数。
        /// </summary>
        private static void OnStepApplied(Map map, int gameTick, int elapsedTicks)
        {
            if (map == null || elapsedTicks <= 0)
            {
                return;
            }

            if (!AoBridge.IsActive(map))
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
                    ledger.Run(net, elapsedTicks);
                }
            }
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
