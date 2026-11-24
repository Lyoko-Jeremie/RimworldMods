using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Patches
{
    /// <summary>
    /// 接管 AO 的温控出口计算。
    ///
    /// AO 的 TempControlSubclassPatches 会给每个 Building_TempControl 子类的 TickRare 打 Prefix，
    /// Prefix 里调用 FacilityPatchUtility.RunTempControl(building)，用返回值决定是否放行原方法。
    /// 本 Prefix 挂在 RunTempControl 上：先用自己的格子布局写 AO 连续源，
    /// 再把 __result 置 false 并 return false 跳过 AO 的原实现。
    /// __result = false 对 AO 的语义是「已接管」，因此子类原方法仍会被 RoomTemperatureWriteHook 抑制，
    /// 不会出现两条热源通道叠加。
    ///
    /// 只对挂了 CompTempOutputShape 的建筑生效；其余建筑原样走 AO 路径。
    /// </summary>
    public static class TempOutputShapePatch
    {
        // 注意命名空间：FacilityPatchUtility 在 AtmosphereOverhaul.Patches，不在 .Buildings。
        private const string FacilityPatchUtilityTypeName = "AtmosphereOverhaul.Patches.FacilityPatchUtility";

        public static bool Install(Harmony harmony)
        {
            if (harmony == null)
            {
                return false;
            }

            // 优先把形状翻译成 AO 的 XML 出风口布局：AO 原生实现已经覆盖
            // 多格出风、效率、热侧倍率与除湿，不需要再接管 RunTempControl。
            if (OutletLayoutBootstrap.TryApplyAll())
            {
                return false;
            }

            Type utilityType = AccessTools.TypeByName(FacilityPatchUtilityTypeName);
            if (utilityType == null)
            {
                return false;
            }

            MethodInfo target = AccessTools.Method(
                utilityType,
                "RunTempControl",
                new[] { typeof(Building_TempControl) });
            if (target == null)
            {
                return false;
            }

            harmony.Patch(target, new HarmonyMethod(typeof(TempOutputShapePatch), "Prefix"));
            return true;
        }

        public static bool Prefix(Building_TempControl __instance, ref bool __result)
        {
            if (__instance == null)
            {
                return true;
            }

            Comps.CompTempOutputShape comp = __instance.GetComp<Comps.CompTempOutputShape>();
            if (comp == null)
            {
                return true;
            }

            if (!comp.Apply())
            {
                // 没有槽位或 AO 不可用：交回 AO 原实现，不能静默吞掉热量。
                return true;
            }

            __result = false;
            return false;
        }
    }
}
