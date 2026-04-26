using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // 拦截《短路事件》 (Zzztt...) 以防止毁灭性的核弹级爆炸
    // 整合了 CompMatterEnergyConverterBattery 和 CompOmniCrafterSmartInfiniteBattery 的处理逻辑
    [HarmonyPatch(typeof(ShortCircuitUtility), "DoShortCircuit")]
    public static class Patch_DoShortCircuit_Unified
    {
        [HarmonyPrefix]
        public static void Prefix(Building culprit, out Dictionary<CompPowerBattery, float> __state)
        {
            __state = new Dictionary<CompPowerBattery, float>();
            PowerNet net = culprit.PowerComp?.PowerNet;
            if (net == null) return;

            foreach (CompPowerBattery battery in net.batteryComps)
            {
                // 处理本 Mod 的两种无限容量电池
                if (battery is FullyAutomaticOmniCrafter.CompOmniCrafterSmartInfiniteBattery
                    || battery is FullyAutomaticOmniCrafter.CompMatterEnergyConverterBattery)
                {
                    // 直接读取私有字段 storedEnergy，绕过 StoredEnergy 属性（可能被 Harmony 拦截返回 0）
                    float currentEnergy = Traverse.Create(battery).Field("storedEnergy").GetValue<float>();
                    if (currentEnergy > 0f)
                    {
                        __state.Add(battery, currentEnergy);
                        battery.DrawPower(currentEnergy); // 暂时清空，骗过爆炸计算
                    }
                }
            }
        }

        [HarmonyPostfix]
        public static void Postfix(Dictionary<CompPowerBattery, float> __state)
        {
            if (__state == null || __state.Count == 0) return;

            // 3. 爆炸计算结束后，将没收的电量全额归还
            foreach (var kvp in __state)
            {
                kvp.Key.AddEnergy(kvp.Value); // 爆炸计算结束后，全额归还
            }
        }
    }
}