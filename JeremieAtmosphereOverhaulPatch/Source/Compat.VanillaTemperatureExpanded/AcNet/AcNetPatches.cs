using System;
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

            return true;
        }

        public static bool AcUnitTickRarePrefix(Building_AcUnit __instance)
        {
            if (__instance == null || !AoBridge.Available)
            {
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

            int tick = Find.TickManager.TicksGame;
            if (ledger.NeedsRun(tick))
            {
                ledger.Run(net);
            }

            // 账本已写入 AO 连续源，原 TickRare 不再需要写房间温度。
            return false;
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
    }
}
